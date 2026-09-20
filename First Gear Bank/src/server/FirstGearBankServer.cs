/*
 * Hosts one world's banking coordinator behind Vintage Story's authoritative server lifecycle.
 * Startup loads validated YAML, selects the approved exact checkpoint scanner unless an index is supplied, restores
 * world authority, and observes authenticated connected players.  World-save hooks restage immutable bank snapshots;
 * Disconnect, suspension, resume, and disposal retire transient access without transferring customer money.
 *
 * This host implements IBankingHost using server identities, current privileges, the trusted clock, Banker sessions,
 * and retained inventory transactions.  Network requests are normalized here on the main thread and responses contain
 * only safe core views.  Administrative corrections use private short-lived scopes unavailable to ordinary packets.
 *
 * Banker content registers validated loaded entities through RegisterBanker.  This adapter does not manufacture branch
 * topology, spawn NPCs, provide a client GUI, or guess hard-crash repairs.  Those integration boundaries
 * remain explicit.  Checkpoint scans preserve per-account rounding at an accepted account-count-dependent cost;
 * ordinary ticks and quotes reuse the core's held liquidity target unless financial events have become due.
 */

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FirstGearBank.Core;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace FirstGearBank.Server;

/// World-scoped authoritative banking host, owned and disposed by the mod system.
/// Public content integration methods must run on the game server thread; the coordinator is never exposed to clients.
public sealed class FirstGearBankServer : IBankingHost, IDisposable
{
    private readonly ICoreServerAPI api;
    private readonly ServerDiagnostics log;
    private readonly ServerConfiguration configuration;
    private readonly VintageStoryClock clock;
    private readonly BankerSessions sessions;
    private readonly BankingProtocol protocol;
    private readonly IExactLiquidityIndex liquidityIndex;
    private readonly object inventoryGate = new();
    private readonly Dictionary<Guid, string> administrativeScopes = new();
    private readonly HashSet<string> reconciledPlayers = new(StringComparer.Ordinal);
    private readonly HashSet<string> quarantinedPlayers = new(StringComparer.Ordinal);
    private RequestKey? inventoryRequest;
    private ServerSettings settings;
    private WorldBankStorage? storage;
    private BankingCoordinator? bank;
    private long tickListener;
    private int serverThread;
    private bool loaded;
    private bool disposed;
    private bool faulted;
    private decimal nextNoticeDelivery;
    public string Status { get; private set; } = "Starting";
    // NPC death samples current adapter settings; already sampled replacement timers retain their stored duration.
    internal (double Minimum, double Maximum) ReplacementDelayRange =>
        (settings.ReplacementMinimumDays, settings.ReplacementMaximumDays);
    // New Charters sample current adapter settings once; saved waits are never changed by configuration reload.
    internal (double Minimum, double Maximum) CharterArrivalDelayRange =>
        (settings.CharterMinimumDays, settings.CharterMaximumDays);
    // Spacing changes affect only future branch acceptance; existing reservations are retained.
    internal int MinimumBranchSpacing => settings.MinimumBranchSpacing;



    //// Creates one server adapter and registers lifecycle callbacks before the save becomes available.
    //// A supplied index must satisfy the exact-liability contract; otherwise use the approved checkpoint scanner.
    ////
    public FirstGearBankServer(ICoreServerAPI api, IExactLiquidityIndex? liquidityIndex = null)
    {
        this.api = api;
        this.liquidityIndex = liquidityIndex ?? new ScanningLiquidityIndex();
        log = new(api);
        configuration = new(api.DataBasePath, log);
        settings = configuration.Load();
        clock = new(api);
        protocol = new(api, HandleRequest, log);
        sessions = new(api, ConversationClosed);
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, LoadWorld);
        api.Event.GameWorldSave += SaveWorld;
        api.Event.PlayerNowPlaying += ObservePlayer;
        api.Event.PlayerDisconnect += DisconnectPlayer;
        api.Event.ServerSuspend += Suspend;
        api.Event.ServerResume += Resume;
        tickListener = api.Event.RegisterGameTickListener(Tick, 1000);
        api.ChatCommands.Create("bankadmin").WithDescription("Bank status, reload, and audited balance corrections")
            .RequiresPrivilege(Privilege.controlserver)
            .WithArgs(api.ChatCommands.Parsers.OptionalAll("status | reload | correct name currency amount reason"))
            .HandleWith(AdminCommand);
    }



    //// Informs an affected client when session pruning, branch cutoff, or replacement closes its conversation.
    //// This is display invalidation only; it cannot cancel an already committed financial operation.
    ////
    private void ConversationClosed(IServerPlayer player, Guid scope)
    {
        try { protocol.Send(player, new(scope, 0, "ConversationClosed")); }
        catch (Exception)
        {
            // A disappearing connection must not disable unrelated customers after its authority is already retired.
            log.Write("WARN", "sessions", "Conversation closed; client display notification could not be sent.");
        }
    }



    //// Loads authority only at RunGame, after 1.22.7 restores calendar state during GameReady.
    //// SaveGameLoaded is too early: anchoring there would count historical world time again on the first live tick.
    //// The selected exact index serves both creation and restore.  Corrupt saved authority stays untouched.
    ////
    private void LoadWorld()
    {
        if (loaded || disposed || api.Server.CurrentRunPhase != EnumServerRunPhase.RunGame) return;
        loaded = true;
        serverThread = Environment.CurrentManagedThreadId;
        try
        {
            if (liquidityIndex is ScanningLiquidityIndex)
                log.Write("INFO", "liquidity", "Using approved exact account scans at financial-month and funding-changing checkpoints.");
            storage = new(api.WorldManager.SaveGame, log);
            bank = storage.Load(this, liquidityIndex, settings);
            if (bank is null)
            {
                Status = "CorruptState";
                return;
            }
            bank.Configure(settings.Economics, settings.Core, settings.TimeBasis);
            Status = "Ready";
            foreach (var player in api.World.AllOnlinePlayers.OfType<IServerPlayer>()) ObservePlayer(player);
            if (api.ModLoader.IsModEnabled("configlib"))
                log.Write("INFO", "configuration",
                    "Config Lib detected; First Gear Bank YAML settings are available in its editor.");
            log.Write("INFO", "startup", "Bank authority loaded; authenticated server adapters are ready.");
        }
        catch (Exception error)
        {
            Fault("startup", error);
        }
    }



    //// Restages current authority whenever the engine is preparing a world save, even after prior serialization.
    //// A staging failure closes monetary admission; the last successfully staged authority remains recoverable.
    ////
    private void SaveWorld()
    {
        if (bank is null || disposed) return;
        try
        {
            RequireThread();
            storage?.Stage(bank);
        }
        catch (Exception error)
        {
            Fault("storage", error);
        }
    }



    //// Processes due financial events and cleans only live sessions on the engine's one-second callback.
    //// Notification retries are limited to once per ten real seconds and fifty durable notices per connected player.
    ////
    private void Tick(float elapsedSeconds)
    {
        if (disposed) return;
        // The fallback handles an adapter loaded after the save lifecycle event, without inventing a new installation.
        if (!loaded && api.Server.CurrentRunPhase == EnumServerRunPhase.RunGame) LoadWorld();
        if (bank is null || faulted || clock.Suspended) return;
        try
        {
            RequireThread();
            bank.Advance();
            sessions.Prune(clock.RealSeconds, bank);
            if (clock.RealSeconds < nextNoticeDelivery) return;
            nextNoticeDelivery = clock.RealSeconds + 10;
            foreach (var player in api.World.AllOnlinePlayers.OfType<IServerPlayer>()) SendNotices(player);
        }
        catch (Exception error)
        {
            Fault("maintenance", error);
        }
    }



    //// Updates the current-epoch name registry only from a fully authenticated, currently playing connection.
    //// Joining records discoverability, not account creation; no world-wide offline player database is queried.
    ////
    private void ObservePlayer(IServerPlayer player)
    {
        if (bank is null || faulted || !IsAuthenticated(player)) return;
        try
        {
            RequireThread();
            if (!reconciledPlayers.Contains(player.PlayerUID))
            {
                if (!InventoryRecoveryReceipts.Matches(player, bank.Snapshot()))
                {
                    quarantinedPlayers.Add(player.PlayerUID);
                    log.Write("CRIT", "recovery", "Player inventory and bank receipt evidence disagree; player banking quarantined.");
                }
                reconciledPlayers.Add(player.PlayerUID);
            }
            try { bank.ObservePlayer(player.PlayerUID, player.PlayerName); }
            catch (BankException error) when (error.Error == BankError.RecipientServiceUnavailable)
            {
                // Registry quarantine must not disable unrelated savings, inventory reconciliation, or save staging.
            }
            SendNotices(player);
        }
        catch (Exception error)
        {
            Fault("identity", error);
        }
    }



    //// Retires the connection's conversation and admission bucket without deleting accounts or pending notices.
    ////
    private void DisconnectPlayer(IServerPlayer player)
    {
        protocol.Disconnect(player);
        reconciledPlayers.Remove(player.PlayerUID);
        quarantinedPlayers.Remove(player.PlayerUID);
        if (bank is not null) sessions.Close(player.PlayerUID, bank);
    }



    //// Checkpoints active runtime immediately before the engine suspends simulation.
    //// The host never vetoes engine suspension; financial errors disable banking and leave the game free to pause.
    ////
    private EnumSuspendState Suspend()
    {
        try
        {
            if (!clock.Suspended && bank is not null && !faulted) bank.Advance();
        }
        catch (Exception error) { Fault("clock", error); }
        clock.Suspended = true;
        return EnumSuspendState.Ready;
    }



    //// Reanchors the paused runtime interval before admitting the next active sample, excluding pause duration.
    ////
    private void Resume()
    {
        try
        {
            if (clock.Suspended && bank is not null && !faulted) bank.Advance();
        }
        catch (Exception error) { Fault("clock", error); }
        clock.Suspended = false;
    }



    //// Routes validated transport intent through current session authority and normalized core operations.
    //// Business rejections become named statuses; internal identities and exceptions are never serialized.
    ////
    private void HandleRequest(IServerPlayer player, BankingRequest request)
    {
        RequireThread();
        if (bank is null || faulted || clock.Suspended)
        {
            protocol.Send(player, new(request.Scope, request.Sequence, clock.Suspended ? "Suspended" : Status));
            return;
        }
        try
        {
            var scope = request.Scope;
            object? body;
            // Outbox acknowledgments are connection-authenticated and intentionally do not require a Banker visit.
            if (request.Action == "acknowledge")
            {
                bank.AcknowledgeNotice(player.PlayerUID, request.Token);
                protocol.Send(player, new(scope, request.Sequence, "None"));
                return;
            }
            if (request.Action == "open")
            {
                if (!reconciledPlayers.Contains(player.PlayerUID) || quarantinedPlayers.Contains(player.PlayerUID))
                    throw new BankException(BankError.SettlementQuarantined);
                scope = sessions.Open(player, request.BankerEntity, clock.RealSeconds, bank);
                var snapshot = bank.Snapshot();
                body = new { DisplayPrecision = snapshot.Scopes[scope].DisplayPrecision, NextSequence = 1,
                    settings.RecipientMode, snapshot.Market.Current.Settings.TenorsMonths,
                    snapshot.Market.Current.Settings.MinimumCdPrincipalUnits };
            }
            else
            {
                if (!sessions.Valid(player.PlayerUID, scope, clock.RealSeconds))
                    throw new BankException(BankError.InvalidSession);
                switch (request.Action)
                {
                    case "close":
                        sessions.Close(player.PlayerUID, bank);
                        body = null;
                        break;
                    case "statement":
                        body = StatementPage(player.PlayerUID, scope, request.Offset, request.Limit);
                        break;
                    case "previewDeposit":
                    case "previewWithdraw":
                        body = PreviewPhysical(player, request);
                        break;
                    case "names":
                        if (settings.RecipientMode != "KnownPlayerListing")
                            throw new BankException(BankError.PermissionDenied);
                        body = bank.KnownNames(request.Name ?? "", request.Offset, request.Limit);
                        break;
                    case "confirmTransfer":
                        var token = bank.ConfirmRecipient(player.PlayerUID, scope, request.Name ?? "", request.Amount);
                        var confirmation = bank.Snapshot().Confirmations[token];
                        body = new { Token = token, Name = confirmation.CanonicalName, confirmation.ExactUnits,
                            ExpiresInSeconds = Math.Max(0, confirmation.ExpiresAtSeconds - clock.RealSeconds) };
                        break;
                    case "quoteCd":
                        bank.Advance();
                        var quote = bank.QuoteCertificate(player.PlayerUID, scope, Money.Parse(request.Amount ?? ""),
                            request.TenorMonths);
                        body = new { Quote = quote,
                            ExpiresInSeconds = Math.Max(0, quote.ExpiresAtSeconds - clock.RealSeconds) };
                        break;
                    case "deposit":
                    case "withdraw":
                    case "transfer":
                    case "buyCd":
                        ExecuteMutation(player, request);
                        return;
                    default:
                        protocol.Send(player, new(scope, request.Sequence, "InvalidRequest"));
                        return;
                }
            }
            protocol.Send(player, new(scope, request.Sequence, "None", body));
        }
        catch (BankException error)
        {
            protocol.Send(player, new(request.Scope, request.Sequence, error.Error.ToString()));
        }
        catch (Exception error) when (error is FormatException or OverflowException or ArgumentException)
        {
            protocol.Send(player, new(request.Scope, request.Sequence, "InvalidRequest"));
        }
    }



    //// Resolves an exact cash intent for the confirmation screen without reserving funds or modifying inventory.
    //// Accrual uses the normal statement path; commit still repeats balance, denomination, and inventory checks.
    //// A first deposit is allowed without an account.  No other statement failure is mistaken for an empty balance.
    ////
    private object PreviewPhysical(IServerPlayer player, BankingRequest request)
    {
        if (!Enum.TryParse<Currency>(request.Currency, false, out var currency) || !Enum.IsDefined(currency))
            throw new BankException(BankError.InvalidCurrency);
        var withdrawal = request.Action == "previewWithdraw";
        var precision = bank!.Snapshot().Scopes[request.Scope].DisplayPrecision;
        var amount = Money.Parse(request.Amount ?? "", currency == Currency.Temporal ? 0 : precision);
        if (amount.Units <= 0) throw new BankException(BankError.InvalidAmount);
        long balance = 0;
        try
        {
            var statement = bank.GetStatement(player.PlayerUID, request.Scope, 0, 1);
            balance = currency == Currency.Rusty ? statement.RustyUnits : statement.TemporalUnits;
        }
        catch (BankException error) when (!withdrawal && error.Error == BankError.NoAccount)
        {
            // A preview must not create an account merely because the player has entered a prospective deposit.
        }
        Money.RequireBalance(new(withdrawal ? checked(balance - amount.Units) : checked(balance + amount.Units)));
        using var preview = InventorySettlement.Prepare(inventoryGate, player, api.World, currency,
            amount.Units, withdrawal);
        return new { Action = withdrawal ? "withdraw" : "deposit", Currency = currency.ToString(),
            Amount = amount.Gears.ToString("0.######", CultureInfo.InvariantCulture), ExactUnits = amount.Units };
    }



    //// Constructs the smallest semantic command so irrelevant client fields cannot select authority or permissions.
    //// The core owns retry admission and terminal sequence consumption; success is not undone by display failure.
    ////
    private void ExecuteMutation(IServerPlayer player, BankingRequest request)
    {
        BankCommand command;
        if (request.Action is "deposit" or "withdraw")
        {
            var kind = request.Action == "deposit" ? CommandKind.Deposit : CommandKind.Withdraw;
            try
            {
                if (!Enum.TryParse<Currency>(request.Currency, false, out var currency) || !Enum.IsDefined(currency))
                    throw new BankException(BankError.InvalidCurrency);
                var precision = bank!.Snapshot().Scopes[request.Scope].DisplayPrecision;
                var amount = Money.Parse(request.Amount ?? "", currency == Currency.Temporal ? 0 : precision);
                command = new(kind, currency, amount.Units);
            }
            catch (Exception error) when (error is BankException or OverflowException)
            {
                // Even malformed authenticated mutations must consume their admitted sequence.  Zero amount or an
                // invalid currency rejects inside the core; the inert reason digest distinguishes malformed retries.
                var invalidCurrency = error is BankException failure && failure.Error == BankError.InvalidCurrency;
                var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    request.Amount, request.Currency
                })));
                command = new(kind, invalidCurrency ? (Currency)(-1) : Currency.Rusty, 0, Reason: digest);
            }
        }
        else command = new(request.Action == "transfer" ? CommandKind.Transfer : CommandKind.BuyCd, Token: request.Token);
        if (inventoryRequest is not null) throw new BankException(BankError.InventoryUnavailable);
        BankResult result;
        inventoryRequest = new(player.PlayerUID, request.Scope, request.Sequence);
        try { result = bank!.Execute(new(inventoryRequest), command); }
        finally { inventoryRequest = null; }
        object? display = null;
        if (result.Error == BankError.None)
        {
            try { display = StatementPage(player.PlayerUID, request.Scope, 0, request.Limit); }
            catch (BankException)
            {
                // The terminal mutation result remains authoritative when a fresh display is unavailable.
            }
        }
        protocol.Send(player, new(request.Scope, request.Sequence, result.Error.ToString(),
            new { result.Revision, result.Operations, Display = display }));
    }



    //// Builds a bounded name-only history/CD display page without passing internal bank snapshots to the transport.
    //// The same offset pages both collections; independent totals let a client request subsequent pages as needed.
    ////
    private object StatementPage(string player, Guid scope, int offset, int limit)
    {
        var statement = bank!.GetStatement(player, scope, offset, limit);
        return new
        {
            Statement = statement with { Certificates = statement.Certificates.Skip(offset).Take(limit).ToImmutableArray() },
            TotalCertificates = statement.Certificates.Length,
            WorldCalendarDays = (decimal)api.World.Calendar.TotalDays,
            api.World.Calendar.DaysPerMonth,
            api.World.Calendar.HoursPerDay,
            HistoryCalendarDays = statement.History.Select(row =>
                bank.Snapshot().Journal[checked((int)row.Sequence - 1)].WorldCalendarDays).ToArray()
        };
    }



    //// Retries a bounded durable outbox batch; only an explicit authenticated acknowledgment removes a notice.
    ////
    private void SendNotices(IServerPlayer player)
    {
        if (bank is null || !IsAuthenticated(player)) return;
        var notices = bank.PendingNotices(player.PlayerUID, 50);
        if (!notices.IsEmpty)
        {
            // Namespace client display receipts without exposing either the save identifier or the player's UID.
            var scopeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("firstgearbank/notices/v1/" +
                api.WorldManager.SaveGame.SavegameIdentifier + "/" + player.PlayerUID)));
            protocol.Send(player, new(Guid.Empty, 0, "Notices", new { ScopeHash = scopeHash, Notices = notices }));
        }
    }



    //// Exposes trusted Banker registration to content code after its branch validation and entity spawning succeed.
    ////
    public void RegisterBanker(Guid branch, Entity banker, Func<bool> branchAvailable)
    {
        RequireThread();
        sessions.Register(branch, banker, branchAvailable);
        // This replicated hint enables interaction only; every request still proves registration on the server.
        banker.WatchedAttributes.SetBool("firstgearbank:banker", true);
    }



    //// Invalidates access when content unloads, kills, replaces, or disables its registered Banker.
    ////
    public void UnregisterBanker(long entityId)
    {
        RequireThread();
        api.World.GetEntityById(entityId)?.WatchedAttributes.SetBool("firstgearbank:banker", false);
        if (bank is not null) sessions.Unregister(entityId, bank);
    }



    //// Supplies a trusted clock sample to the core while its coordinator gate is held.
    ////
    public ClockSample SampleClock()
    {
        RequireThread();
        return clock.Sample();
    }



    //// Validates either a live Banker scope or a private administrator scope with current correction privilege.
    //// Administrative scopes are never accepted by ordinary network dispatch and are removed in a finally block.
    ////
    public bool IsSessionValid(string player, Guid scope)
    {
        RequireThread();
        return !faulted && !clock.Suspended && reconciledPlayers.Contains(player) &&
            !quarantinedPlayers.Contains(player) && ((administrativeScopes.TryGetValue(scope, out var owner) &&
            owner == player && CanCorrectBalances(player)) || sessions.Valid(player, scope, clock.RealSeconds));
    }



    //// Returns current authenticated names or previously observed registry names, never a raw UID fallback.
    ////
    public string PlayerName(string player)
    {
        if (api.World.PlayerByUid(player) is IServerPlayer online && IsAuthenticated(online)) return online.PlayerName;
        return bank?.Snapshot().Names.GetValueOrDefault(player)?.Name ?? "Unknown player";
    }



    //// Reports authenticated connection presence for notice grouping without querying the game's offline player store.
    ////
    public bool IsOnline(string player)
    {
        return api.World.PlayerByUid(player) is IServerPlayer online && IsAuthenticated(online);
    }



    //// Checks the game's current controlserver privilege for an online authenticated administrator.
    ////
    public bool CanCorrectBalances(string player)
    {
        return api.World.PlayerByUid(player) is IServerPlayer online && IsAuthenticated(online) &&
            online.HasPrivilege(Privilege.controlserver);
    }



    //// Prepares exact personal-inventory changes on the server thread after rechecking the player's live connection.
    ////
    public IInventoryChange PrepareInventory(string player, Currency currency, long units, bool withdrawal)
    {
        RequireThread();
        if (api.World.PlayerByUid(player) is not IServerPlayer online || !IsAuthenticated(online))
            throw new BankException(BankError.InventoryUnavailable);
        if (inventoryRequest is null || inventoryRequest.Player != player || quarantinedPlayers.Contains(player))
            throw new BankException(BankError.SettlementQuarantined);
        var change = InventorySettlement.Prepare(inventoryGate, online, api.World, currency, units, withdrawal);
        try
        {
            return new ReceiptedInventoryChange(change, online, api.WorldManager.SaveGame.SavegameIdentifier,
                inventoryRequest, currency, units, withdrawal);
        }
        catch
        {
            change.Dispose();
            throw;
        }
    }



    //// Appends an audited correction through a private one-request scope, optionally reversing a prior correction.
    //// The supplied player object must be the current authenticated connection; name resolution stays inside the core.
    ////
    public BankResult CorrectBalance(IServerPlayer administrator, string name, Currency currency, string amount,
        string reason, Guid? relatedOperation = null)
    {
        RequireThread();
        if (bank is null || faulted || !IsAuthenticated(administrator) || !CanCorrectBalances(administrator.PlayerUID))
            throw new BankException(BankError.PermissionDenied);
        var units = Money.Parse(amount).Units;
        var scope = bank.OpenConversation(administrator.PlayerUID);
        administrativeScopes.Add(scope, administrator.PlayerUID);
        try
        {
            return bank.Execute(new(new(administrator.PlayerUID, scope, 1)),
                new(CommandKind.AdminCorrection, currency, units, RecipientName: name, Reason: reason,
                    RelatedOperation: relatedOperation));
        }
        finally
        {
            administrativeScopes.Remove(scope);
            bank.CloseConversation(scope);
        }
    }



    //// Handles permission-gated status, YAML config reload, and name-based correction chat commands.
    //// Console callers may inspect or reload; corrections require an authenticated player for the audit trail.
    ////
    private TextCommandResult AdminCommand(TextCommandCallingArgs args)
    {
        RequireThread();
        var words = ((string?)args[0] ?? "status").Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words[0] == "status") return TextCommandResult.Success("First Gear Bank: " + Status);
        try
        {
            if (words[0] == "reload")
            {
                var next = configuration.Load(settings, bank?.Snapshot().Market.Current.Rate);
                if (bank is not null && !faulted) bank.Configure(next.Economics, next.Core, next.TimeBasis);
                settings = next;
                return TextCommandResult.Success(bank is null || faulted ?
                    "Configuration loaded; banking remains unavailable (" + Status + ")." :
                    "Configuration loaded; economics take effect at the next financial-month boundary.");
            }
            if (words.Length == 5 && words[0] == "correct" && args.Caller.Player is IServerPlayer administrator &&
                Enum.TryParse<Currency>(words[2], false, out var currency) && Enum.IsDefined(currency))
            {
                var result = CorrectBalance(administrator, words[1], currency, words[3], words[4]);
                return result.Error == BankError.None ? TextCommandResult.Success("Correction committed: " +
                    string.Join(", ", result.Operations)) : TextCommandResult.Error(result.Error.ToString());
            }
            return TextCommandResult.Error("Use /bankadmin status, reload, or correct name Rusty|Temporal amount reason.");
        }
        catch (BankException error) { return TextCommandResult.Error(error.Error.ToString()); }
        catch (Exception error) when (error is FormatException or OverflowException or ArgumentException)
        {
            return TextCommandResult.Error("Invalid correction or configuration.");
        }
    }



    //// Verifies a connection against the world's current authenticated player object, rejecting replaced sessions.
    ////
    private bool IsAuthenticated(IServerPlayer player)
    {
        return player.ConnectionState == EnumClientState.Playing &&
            ReferenceEquals(api.World.PlayerByUid(player.PlayerUID), player);
    }



    //// Enforces synchronous game-thread access so inventory preflight cannot interleave with queued client mutations.
    //// Startup captures the thread when the world becomes available, not when the mod is first constructed.
    ////
    private void RequireThread()
    {
        if (disposed || serverThread == 0 || Environment.CurrentManagedThreadId != serverThread)
            throw new InvalidOperationException("Banking requires the active server thread.");
    }



    //// Closes monetary admission after an unexpected host failure and records only a sanitized exception category.
    //// Keeping the coordinator allows later save hooks to preserve its last published authoritative revision.
    ////
    private void Fault(string component, Exception error)
    {
        if (!faulted) log.Write("CRIT", component, $"Banking disabled after {error.GetType().Name}; published authority retained.");
        faulted = true;
        Status = "ServiceUnavailable";
    }



    //// Stages final authority before removing callbacks, then closes the transport without claiming a disk flush.
    //// Mod disposal precedes 1.22.7's final save event; relying on that event alone loses recent changes.
    ////
    public void Dispose()
    {
        if (disposed) return;
        if (bank is not null)
        {
            try
            {
                if (!faulted && Environment.CurrentManagedThreadId == serverThread) bank.Advance();
            }
            catch (Exception error) { Fault("shutdown-clock", error); }
            // Even failed final accrual must not prevent staging the most recent successfully published revision.
            try { storage?.Stage(bank); }
            catch (Exception error) { Fault("shutdown-save", error); }
        }
        disposed = true;
        protocol.Dispose();
        api.Event.UnregisterGameTickListener(tickListener);
        api.Event.GameWorldSave -= SaveWorld;
        api.Event.PlayerNowPlaying -= ObservePlayer;
        api.Event.PlayerDisconnect -= DisconnectPlayer;
        api.Event.ServerSuspend -= Suspend;
        api.Event.ServerResume -= Resume;
        administrativeScopes.Clear();
        log.Write("INFO", "shutdown", "Server adapter stopped; engine-owned save completion is not asserted.");
    }



}
