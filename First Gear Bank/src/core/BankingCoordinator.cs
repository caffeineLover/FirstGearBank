/*
 * Implements the entry point and publication boundary for one world's banking core.
 * The coordinator owns the currently published BankState and serializes access with one gate.  Financial helpers
 * return immutable candidates containing journal records, account projections, request results, and control changes;
 * readers see those changes only when the coordinator assigns the resulting revision.
 *
 * Execute checks conversation ownership and request sequencing before doing financial work.  New requests obtain one
 * trusted clock sample, process due global events, and materialize the accounts needed by their business operation.
 * Identical retries return cached terminal results.  Business failures discard unpublished financial changes but
 * still finalize the authenticated sequence, preventing later execution of the same failed intent.
 *
 * Deposits and withdrawals additionally retain a host inventory lock and apply the preflighted item mutation before
 * publishing financial success.  A live application failure requests rollback and reports an ordinary inventory error.
 * This sequence does not establish atomic disk persistence across player inventory and world data.
 *
 * Other partial declarations implement identity/transfers, CDs/statements, and persistence under this same gate.
 * IBankingHost supplies authentication, permissions, clocks, and inventory operations; IExactLiquidityIndex supplies
 * accrued funding totals.  Neither dependency should reenter this coordinator with another mutation during a callback.
 */

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace FirstGearBank.Core;

/// World-scoped owner of immutable bank state and the serialization point for financial and control mutations.
/// Create is restricted to confirmed new installations; Restore handles existing saved authority.
/// The host owns the lifetime of adapter dependencies and must route authenticated operations through this instance.
/// Public snapshots contain internal identities, while dedicated view methods produce player-safe responses.
public sealed partial class BankingCoordinator
{
    private readonly Lock gate = new();
    private readonly IBankingHost host;
    private readonly IExactLiquidityIndex liquidityIndex;
    private BankState state;



    //// Attaches the initial immutable state and borrowed host/index dependencies to one coordinator instance.
    ////
    //// Only Create and Restore call this constructor after their respective initialization or validation steps.
    //// It does not open a world, subscribe to engine events, validate arbitrary saved bytes, or take responsibility
    //// for disposing the host.  The gate and published state remain private to this world instance.
    ////
    private BankingCoordinator(BankState state, IBankingHost host, IExactLiquidityIndex liquidityIndex)
    {
        this.state = state;
        this.host = host;
        this.liquidityIndex = liquidityIndex;
    }



    //// Initializes the in-memory banking state for a confirmed new installation in the named world.
    ////
    //// Validated settings create month zero at theta with its initial spread draws and empty-bank liquidity target.
    //// The financial position starts at zero and raw clocks are anchored to the host's current sample.  The caller
    //// must serialize and stage this state before treating it as saved; initialization itself performs no I/O.
    //// Never call this method as a fallback for unreadable existing banking data.
    ////
    public static BankingCoordinator Create(string worldId, long worldSeed, IBankingHost host,
        IExactLiquidityIndex liquidityIndex, EconomicSettings? settings = null,
        InterestTimeBasis basis = InterestTimeBasis.InGame)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldId);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(liquidityIndex);
        if (!Enum.IsDefined(basis)) throw new BankException(BankError.InvalidConfiguration);
        settings ??= new();
        var market = MarketHistory.Create(worldSeed, settings);
        var clock = new FinancialClock(basis, new(0), 0, 0).Reanchor(host.SampleClock());
        var target = FinanceEngine.LiquidityTarget(settings, 0);
        return new(new BankState
        {
            WorldId = worldId,
            Market = market,
            Clock = clock,
            Liquidity = new(new(0), target, target, settings.AdjustmentHalfLifeMonths)
        }, host, liquidityIndex);
    }



    //// Returns the currently published immutable revision under the coordinator gate.
    ////
    //// Callers can retain the result while later revisions are published because its collections and records are
    //// immutable.  It contains hidden identities and authoritative control data; use dedicated views for client UI
    //// and restrict this method to trusted host integration or privileged diagnostics.
    ////
    public BankState Snapshot()
    {
        lock (gate) return state;
    }



    //// Processes elapsed financial time and publishes due month transitions and CD maturities.
    ////
    //// The server host calls this before serving requests that rely on the published market.  Ordinary updates do
    //// not materialize every cash account; work follows due events.  Liquidity checkpoints may incur the cost of
    //// the explicitly supplied index, including a full scan if the host opted into ScanningLiquidityIndex.
    ////
    public void Advance()
    {
        lock (gate)
        {
            Publish(FinanceEngine.Advance(state, host.SampleClock(), liquidityIndex, Guid.NewGuid()));
        }
    }



    //// Validates a requested configuration update and queues its economics for the next financial-month boundary.
    ////
    //// Current time is first processed under the existing settings.  An optional clock-basis switch checkpoints the
    //// old source before using the new one; contracts retain their financial maturity instants.  CoreOptions is
    //// published immediately, while scopes retain their captured display precision and active cooldown deadlines.
    //// A thrown validation or arithmetic error prevents this candidate from being published.
    ////
    public void Configure(EconomicSettings settings, CoreOptions options, InterestTimeBasis? basis = null)
    {
        lock (gate)
        {
            settings.Validate(state.Market.Current.Rate);
            if (options.RustyDisplayPrecision is < 0 or > 6 || options.TransferCooldownSeconds < 0 ||
                options.PrintedRecentTransactions is < 0 or > 100)
                throw new BankException(BankError.InvalidConfiguration);
            var sample = host.SampleClock();
            var candidate = FinanceEngine.Advance(state, sample, liquidityIndex, Guid.NewGuid());
            if (basis is { } selected) candidate = candidate with { Clock = candidate.Clock.Switch(selected, sample) };
            Publish(candidate with { PendingEconomics = settings, Options = options });
        }
    }



    //// Creates a fresh operation scope after the adapter has authenticated a Banker interaction.
    ////
    //// The scope starts at sequence zero and captures the current rusty display precision.  The host must associate
    //// the returned nonce with its Banker session so later execution-time checks can validate availability and range.
    //// Opening a conversation neither creates a financial account nor authorizes any client-selected identity.
    ////
    public Guid OpenConversation(string player)
    {
        lock (gate)
        {
            ValidatePlayer(player);
            var scope = Guid.NewGuid();
            Publish(state with { Scopes = state.Scopes.Add(scope, new(player, 0, state.Options.RustyDisplayPrecision)) });
            return scope;
        }
    }



    //// Removes a conversation and all quote/recipient tokens owned by it when the adapter reports closure.
    ////
    //// Scope absence makes later mutations invalid even if an old response remains cached.  Closing does not erase
    //// financial history or permanent committed request keys, and a future conversation receives a different nonce.
    ////
    public void CloseConversation(Guid scope)
    {
        lock (gate)
        {
            Publish(state with
            {
                Scopes = state.Scopes.Remove(scope),
                Quotes = state.Quotes.RemoveRange(state.Quotes.Where(p => p.Value.Scope == scope).Select(p => p.Key)),
                Confirmations = state.Confirmations.RemoveRange(state.Confirmations.Where(p => p.Value.Scope == scope)
                    .Select(p => p.Key))
            });
        }
    }



    //// Executes one normalized mutation or returns the original result of an identical cached request.
    ////
    //// Ownership, digest matching, and sequence admission occur before expensive accrual or inventory preparation.
    //// Admitted requests use one clock sample and correlation ID for the candidate's financial work.  Expected
    //// business/arithmetic failures retain the prior financial state while consuming the admitted sequence.
    //// Inventory-bearing success includes preparation and final publication; terminal bookkeeping is allocated
    //// before applying items so a published financial result remains authoritative even if later disposal fails.
    ////
    public BankResult Execute(CommandContext context, BankCommand command)
    {
        lock (gate)
        {
            // Admission rejects stale scopes and changed-payload retries before any time, account, or item mutation.
            var key = context.Request;
            if (!state.Scopes.TryGetValue(key.Scope, out var scope) || scope.Player != key.Player)
                return new(BankError.InvalidSession, state.Revision, []);
            var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command)));
            var cached = state.Responses.GetValueOrDefault(key.Player, ImmutableQueue<CachedResponse>.Empty)
                .FirstOrDefault(r => r.Key == key);
            if (cached is not null) return cached.Digest == digest ? cached.Result :
                new(BankError.PayloadMismatch, state.Revision, []);
            if (key.Sequence <= scope.HighWater || state.CommittedRequests.Contains(key))
                return new(BankError.AlreadyProcessedResponseExpired, state.Revision, []);
            if (key.Sequence != checked(scope.HighWater + 1)) return new(BankError.InvalidSequence, state.Revision, []);

            // Keep the published revision untouched while global catch-up and the requested business action are built.
            var before = state;
            BankState candidate;
            ImmutableArray<Guid> operations = [];
            var error = BankError.None;
            try
            {
                if (!host.IsSessionValid(key.Player, key.Scope)) throw new BankException(BankError.InvalidSession);
                if (!Enum.IsDefined(command.Kind) || !Enum.IsDefined(command.Currency))
                    throw new BankException(BankError.InvalidCurrency);
                var sample = host.SampleClock();
                var commandId = Guid.NewGuid();
                candidate = FinanceEngine.Advance(state, sample, liquidityIndex, commandId);
                candidate = ApplyCommand(candidate, context, command, sample, commandId);
                operations = candidate.Journal.Skip(before.Journal.Count).Select(r => r.OperationId).ToImmutableArray();
            }
            catch (BankException exception)
            {
                error = exception.Error;
                candidate = state;
            }
            catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
            {
                error = BankError.ArithmeticFault;
                candidate = state;
            }
            // An admitted failure still advances sequence state, but publishes none of the discarded candidate's money.
            if (error != BankError.None) operations = [];
            var inventory = error == BankError.None && command.Kind is CommandKind.Deposit or CommandKind.Withdraw;
            var revision = checked(state.Revision + 1);
            candidate = FinalizeRequest(candidate, key, scope, digest, error, operations, revision);
            if (inventory)
            {
                try { candidate = ApplyInventory(candidate, key, command); }
                catch (Exception)
                {
                    // Disposal failure after publication cannot turn committed money into a failed request.
                    var committed = state.Responses.GetValueOrDefault(key.Player, ImmutableQueue<CachedResponse>.Empty)
                        .FirstOrDefault(r => r.Key == key);
                    if (committed is not null) return committed.Result;
                    candidate = FinalizeRequest(state, key, scope, digest, BankError.InventoryUnavailable, [],
                        checked(state.Revision + 1));
                }
            }
            state = candidate;
            return candidate.Responses[key.Player].Last().Result;
        }
    }



    //// Adds the terminal response, advances scope high-water, and prepares the candidate's final revision number.
    ////
    //// Full responses are retained in per-player FIFO order with the latest 1,024 bodies preserved.  Successful
    //// financial work also adds its exact request key to permanent replay protection.  The method returns a new
    //// candidate and does not publish it or perform inventory work.
    ////
    private static BankState FinalizeRequest(BankState candidate, RequestKey key, ScopeState scope, string digest,
        BankError error, ImmutableArray<Guid> operations, long revision)
    {
        var result = new BankResult(error, revision, operations);
        var responses = candidate.Responses.GetValueOrDefault(key.Player, ImmutableQueue<CachedResponse>.Empty)
            .Enqueue(new(key, digest, result));
        while (responses.Count() > 1024) responses = responses.Dequeue();
        return candidate with
        {
            Revision = revision,
            Scopes = candidate.Scopes.SetItem(key.Scope, scope with { HighWater = key.Sequence }),
            Responses = candidate.Responses.SetItem(key.Player, responses),
            CommittedRequests = error == BankError.None && operations.Length > 0
                ? candidate.CommittedRequests.Add(key) : candidate.CommittedRequests
        };
    }



    //// Routes an admitted command to its financial rules while keeping all changes inside the supplied candidate.
    ////
    //// Transfers and corrections select their own participants; personal operations first accrue the caller's cash.
    //// Physical amounts must be whole temporal gears or quarter rusty gears, and insufficient funds or caps reject
    //// through ledger validation.  Rusty funding changes refresh liquidity at the same financial instant.
    ////
    private BankState ApplyCommand(BankState candidate, CommandContext context, BankCommand command,
        ClockSample sample, Guid commandId)
    {
        var player = context.Request.Player;
        var now = candidate.Clock.Position;
        // Participant selection must happen before accrual for commands that can affect somebody else's account.
        if (command.Kind == CommandKind.Transfer)
            return Transfer(candidate, context.Request, command.Token, sample, commandId);
        if (command.Kind == CommandKind.AdminCorrection)
            return Correct(candidate, context, command, sample, commandId);
        candidate = FinanceEngine.Accrue(candidate, player, now, sample.WorldDays, commandId, context.Request);
        if (command.Kind == CommandKind.Materialize) return candidate;
        if (command.Kind == CommandKind.BuyCd)
            return Purchase(candidate, context.Request, command.Token, sample, commandId);
        if (command.Units <= 0) throw new BankException(BankError.InvalidAmount);
        // Internal fractions remain valid cash; only a physical item movement must match a dispensable quantum.
        var quantum = command.Currency == Currency.Rusty ? Money.Scale / 4 : Money.Scale;
        if (command.Units % quantum != 0) throw new BankException(BankError.InvalidDenomination);
        var withdrawal = command.Kind == CommandKind.Withdraw;
        if (withdrawal && !candidate.Accounts.ContainsKey(player)) throw new BankException(BankError.NoAccount);
        var delta = withdrawal ? -command.Units : command.Units;
        candidate = Ledger.Append(candidate, commandId, context.Request,
            withdrawal ? TransactionType.Withdrawal : TransactionType.Deposit, now, sample.WorldDays,
            Names(player), FinanceEngine.Pair(player, command.Currency, LedgerKind.Custody, delta));
        return command.Currency == Currency.Rusty ?
            FinanceEngine.CheckpointLiquidity(candidate, now, liquidityIndex) : candidate;
    }



    //// Couples a fully built financial candidate to the host's preflighted, locked inventory mutation.
    ////
    //// The host independently verifies the item value and supplies exact before/after evidence.  The inventory change
    //// is applied before the already-built financial result is published.  A failed application attempts rollback and
    //// returns a normal inventory error rather than retaining a recovery state.
    ////
    private BankState ApplyInventory(BankState candidate, RequestKey key, BankCommand command)
    {
        IInventoryChange change;
        try
        {
            change = host.PrepareInventory(key.Player, command.Currency, command.Units,
            command.Kind == CommandKind.Withdraw);
        }
        catch { throw new BankException(BankError.InventoryUnavailable); }
        using (change)
        {
            if (change.VerifiedUnits != command.Units || change.Manifest.Deltas.IsDefaultOrEmpty ||
                string.IsNullOrWhiteSpace(change.Manifest.FingerprintBefore) ||
                string.IsNullOrWhiteSpace(change.Manifest.FingerprintAfter))
                throw new BankException(BankError.InventoryUnavailable);
            try { change.Apply(); }
            catch
            {
                try { change.Rollback(); }
                catch { }
                throw new BankException(BankError.InventoryUnavailable);
            }
            return candidate;
        }
    }



    //// Freezes host-provided display names for the distinct internal identities participating in one operation.
    //// The dictionary keys remain privileged identity links; only its display-name values belong in rendered history.
    ////
    private ImmutableDictionary<string, string> Names(params string[] players)
    {
        return players.Distinct().ToImmutableDictionary(p => p, p => host.PlayerName(p), StringComparer.Ordinal);
    }



    //// Replaces the published state with a candidate carrying the next checked global revision number.
    //// Callers must already hold the gate and finish validation before invoking this publication boundary.
    ////
    private void Publish(BankState candidate)
    {
        state = candidate with { Revision = checked(state.Revision + 1) };
    }



    //// Rejects empty or overlong internal identity values at the trusted API boundary.
    //// The 1,024-byte UTF-8 limit is a serialization bound, not a name-format rule; identity is never normalized.
    //// Errors expose only a stable status code and do not include the rejected UID.
    ////
    private static void ValidatePlayer(string player)
    {
        if (string.IsNullOrWhiteSpace(player) || System.Text.Encoding.UTF8.GetByteCount(player) > 1024)
            throw new BankException(BankError.InvalidSession);
    }



}
