/*
 * Implements serialization and startup validation for the coordinator's world-level banking state.
 * Save captures one immutable revision briefly under the gate, then serializes outside it.  The binary envelope
 * binds data to a world identifier and frames four ordered JSON sections: journal, financial state, control state,
 * and recipient registry.  Each section carries its own schema version, length bound, and SHA-256 checksum.
 * Checksums detect changed bytes; they are not proof of authenticity against someone able to replace a world save.
 *
 * Restore validates framing and indispensable financial/control state before replaying monetary projections from
 * the journal.  Saved cash checkpoints are accepted only after consistency checks.  Unreadable authority returns a
 * failed load for the server adapter to handle.
 *
 * Pre-restart conversations and their tokens are intentionally inactive after reload.  Runtime clocks are reanchored,
 * cooldown durations are rebased to the new process.  The host still owns current-player observation, inventory
 * changes, and processing of newly due events.
 *
 * This implementation reads schema version one only; future versions need explicit migration handling before use.
 * Serialization success updates an in-memory marker, not a disk-durability guarantee.  The game adapter must stage the
 * returned bytes through the world-save API, retain them on I/O failure, and never replace a failed load with Create.
 */

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FirstGearBank.Core;

/// Startup outcome carrying either a usable coordinator or a load failure, plus the original envelope bytes.
/// The server adapter decides how to handle a failed load without exposing save data to players.
public sealed record BankLoadResult(BankingCoordinator? Bank, BankError Error, ImmutableArray<byte> PreservedBytes);

/// Version-one financial section retaining world/revision identity, clock anchors, realized rates, and liquidity state.
/// These inputs interpret journal balances and future accrual; they cannot be replaced by defaults on load failure.
internal sealed record FinancialSection(string WorldId, long Revision, FinancialClock Clock, MarketHistory Market,
    LiquidityState Liquidity, EconomicSettings? PendingEconomics, CoreOptions Options);

/// Independently checked recipient authority: current epoch, observation revision, and name/audit records by hidden
/// UID.
/// Its reverse lookup index is rebuilt after validation without importing historical or other-world player membership.
internal sealed record RegistrySection(Guid Epoch, long Revision, ImmutableDictionary<string, NameEntry> Names);

/// Persisted request, notification, and checkpoint facts that cannot all be derived from balance postings.
/// Scopes and tokens are retained in the save representation but not reactivated after restart.
/// Cooldowns use remaining durations so a new process never interprets old absolute monotonic timestamps.
internal sealed record ControlSection(ImmutableDictionary<string, ImmutableQueue<CachedResponse>> Responses,
    ImmutableDictionary<Guid, Notice> Notices, ImmutableDictionary<string, long> Watermarks,
    ImmutableDictionary<string, FinancialInstant> Checkpoints,
    ImmutableDictionary<Guid, ScopeState> Scopes, ImmutableDictionary<Guid, CdQuote> Quotes,
    ImmutableDictionary<Guid, TransferConfirmation> Confirmations,
    ImmutableDictionary<string, decimal> CooldownRemaining);

/// Persistence portion of the world coordinator, sharing its gate and immutable published state.
/// Framing, checksums, and replay belong here; game save-file access belongs to the host adapter.
public sealed partial class BankingCoordinator
{
    private const int MaximumSectionBytes = 64 * 1024 * 1024;
    private const int MaximumEnvelopeBytes = 4 * MaximumSectionBytes + 4096;
    private static readonly byte[] Magic = "FGBSTATE"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 64 };
    private long serializedRevision = -1;



    //// Reports whether the current published revision differs from the last successfully serialized revision.
    //// A true result requests another snapshot; a false result says nothing about whether the engine wrote it to disk.
    ////
    public bool NeedsSerialization()
    {
        lock (gate) return serializedRevision != state.Revision;
    }



    //// Returns a complete version-one envelope for one captured immutable bank revision.
    ////
    //// The gate is held only while capturing state and updating the serialization marker.  JSON encoding and framing
    //// occur outside it; a concurrent newer revision therefore remains dirty.  Serialization or size errors leave the
    //// marker unchanged and perform no host file write.
    ////
    public byte[] Save()
    {
        BankState snapshot;
        // Immutable collections keep this captured revision stable after releasing the publication gate.
        lock (gate) snapshot = state;
        var financial = new FinancialSection(snapshot.WorldId, snapshot.Revision, snapshot.Clock, snapshot.Market,
            snapshot.Liquidity, snapshot.PendingEconomics, snapshot.Options);
        var control = new ControlSection(snapshot.Responses, snapshot.Notices, snapshot.DeliveryWatermarks,
            snapshot.Accounts.ToImmutableDictionary(p => p.Key, p => p.Value.Checkpoint),
            snapshot.Scopes, snapshot.Quotes, snapshot.Confirmations,
            snapshot.Cooldowns.ToImmutableDictionary(p => p.Key, p => Math.Max(0, p.Value - snapshot.Clock.RuntimeAnchor)));
        byte[][] sections = [JsonSerializer.SerializeToUtf8Bytes(snapshot.Journal, JsonOptions),
            JsonSerializer.SerializeToUtf8Bytes(financial, JsonOptions),
            JsonSerializer.SerializeToUtf8Bytes(control, JsonOptions),
            JsonSerializer.SerializeToUtf8Bytes(new RegistrySection(snapshot.RegistryEpoch, snapshot.RegistryRevision,
                snapshot.Names), JsonOptions)];
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(Magic);
        writer.Write(1);
        writer.Write(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.WorldId)));
        writer.Write(false);
        writer.Write(sections.Length);
        // Ordered section IDs and independent digests reject malformed saved authority before it reaches the core.
        for (var i = 0; i < sections.Length; i++)
        {
            var bytes = sections[i];
            if (bytes.Length > MaximumSectionBytes) throw new BankException(BankError.ArithmeticFault);
            writer.Write(i + 1);
            writer.Write(1);
            writer.Write(bytes.Length);
            writer.Write(SHA256.HashData(bytes));
            writer.Write(bytes);
        }
        writer.Flush();
        var result = stream.ToArray();
        lock (gate) serializedRevision = snapshot.Revision;
        return result;
    }



    //// Loads existing world-bound authority and returns its validation outcome without overwriting input.
    ////
    //// Framing and checksums are checked before typed decoding.  Financial state is validated, monetary projections
    //// are rebuilt from the journal, and control references/checkpoints are reconciled before a coordinator is
    //// exposed.
    //// Pre-restart scopes stay closed.  A failed load retains PreservedBytes for the server adapter's startup handling.
    ////
    public static BankLoadResult Restore(ReadOnlySpan<byte> envelope, string worldId, IBankingHost host,
        IExactLiquidityIndex liquidityIndex)
    {
        var preserved = ImmutableArray.Create(envelope.ToArray());
        try
        {
            if (envelope.Length > MaximumEnvelopeBytes) throw new BankException(BankError.CorruptState);
            using var stream = new MemoryStream(envelope.ToArray(), false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            if (!reader.ReadBytes(8).AsSpan().SequenceEqual(Magic) || reader.ReadInt32() != 1 ||
                !reader.ReadBytes(32).AsSpan().SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(worldId))))
                throw new BankException(BankError.CorruptState);
            _ = reader.ReadBoolean();
            if (reader.ReadInt32() != 4) throw new BankException(BankError.CorruptState);
            var sections = new byte[4][];
            for (var i = 0; i < 4; i++)
            {
                var id = reader.ReadInt32();
                var version = reader.ReadInt32();
                var length = reader.ReadInt32();
                var digest = reader.ReadBytes(32);
                if (id != i + 1 || version != 1 || length < 0 || length > MaximumSectionBytes ||
                    length > stream.Length - stream.Position) throw new BankException(BankError.CorruptState);
                sections[i] = reader.ReadBytes(length);
                if (!CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(sections[i])))
                    throw new BankException(BankError.CorruptState);
            }
            if (stream.Position != stream.Length) throw new BankException(BankError.CorruptState);
            var journal = ReadSection<ImmutableList<JournalRecord>>(sections[0]);
            var financial = ReadSection<FinancialSection>(sections[1]);
            var control = ReadSection<ControlSection>(sections[2]);
            if (financial.WorldId != worldId || financial.Revision < 0)
                throw new BankException(BankError.CorruptState);
            ValidateFinancial(financial);
            var snapshot = new BankState
            {
                WorldId = worldId,
                Revision = financial.Revision,
                Clock = financial.Clock,
                Market = financial.Market,
                Liquidity = financial.Liquidity,
                PendingEconomics = financial.PendingEconomics,
                Options = financial.Options,
                Journal = journal,
                Responses = control.Responses,
                Notices = control.Notices,
                DeliveryWatermarks = control.Watermarks
            };
            snapshot = Ledger.Replay(snapshot);
            snapshot = RestoreCheckpoints(snapshot, control.Checkpoints);
            ValidateControl(snapshot);
            snapshot = RestoreRegistry(snapshot, ReadSection<RegistrySection>(sections[3]));
            var sample = host.SampleClock();
            if (control.CooldownRemaining.Values.Any(v => v < 0)) throw new BankException(BankError.CorruptState);
            snapshot = snapshot with
            {
                Clock = snapshot.Clock.Reanchor(sample),
                Cooldowns = control.CooldownRemaining.ToImmutableDictionary(p => p.Key,
                    p => checked(sample.RuntimeSeconds + p.Value))
            };
            return new(new BankingCoordinator(snapshot, host, liquidityIndex), BankError.None, preserved);
        }
        catch (Exception exception) when (exception is JsonException or BankException or IOException or
            ArgumentException or OverflowException or InvalidOperationException or NullReferenceException)
        {
            return new(null, BankError.CorruptState, preserved);
        }
    }



    //// Deserializes a version-one payload after Restore has accepted its section header and checksum.
    //// Null or malformed data is rejected for the caller's load-failure path.  This helper performs no migration;
    //// additional schemas require explicit version dispatch before reaching their corresponding decoder.
    ////
    private static T ReadSection<T>(byte[] bytes)
    {
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new BankException(BankError.CorruptState);
    }



    //// Checks that saved financial time, rate history, liquidity state, and configuration are internally usable.
    ////
    //// Monthly serials must be contiguous through the clock's current month.  Rates/shocks must remain finite and
    //// obey their stored parameters, including Constant mode's equality to theta.  Validation consumes no randomness,
    //// so a damaged realized month is rejected rather than reconstructed with a different draw.
    ////
    private static void ValidateFinancial(FinancialSection financial)
    {
        financial.Clock.Position.Validate();
        financial.Liquidity.Checkpoint.Validate();
        if (!Enum.IsDefined(financial.Clock.Basis) || financial.Market.Months.Count == 0 ||
            financial.Market.Current.Serial != financial.Clock.Position.Month ||
            financial.Liquidity.Checkpoint.Months > financial.Clock.Position.Months ||
            !double.IsFinite(financial.Liquidity.Observed) || !double.IsFinite(financial.Liquidity.Target) ||
            !double.IsFinite(financial.Liquidity.HalfLifeMonths) || financial.Liquidity.HalfLifeMonths <= 0 ||
            financial.Options.RustyDisplayPrecision is < 0 or > 6 || financial.Options.TransferCooldownSeconds < 0 ||
            financial.Options.PrintedRecentTransactions is < 0 or > 100)
            throw new BankException(BankError.CorruptState);
        long serial = 0;
        foreach (var pair in financial.Market.Months)
        {
            var month = pair.Value;
            if (pair.Key != serial++ || month.Serial != pair.Key || !double.IsFinite(month.Rate) || month.Rate < 0 ||
                !double.IsFinite(month.Theta) || month.Theta < 0 ||
                BitConverter.DoubleToInt64Bits(month.Theta) !=
                BitConverter.DoubleToInt64Bits(month.Settings.Theta) ||
                (month.Settings.Model == RateModel.Constant &&
                    BitConverter.DoubleToInt64Bits(month.Rate) != BitConverter.DoubleToInt64Bits(month.Theta)) ||
                !double.IsFinite(month.LevelShock) || !double.IsFinite(month.SlopeShock) ||
                Math.Abs(month.LevelShock) > month.Settings.LevelVariation ||
                Math.Abs(month.SlopeShock) > month.Settings.SlopeVariation)
                throw new BankException(BankError.CorruptState);
            month.Settings.Validate();
        }
        financial.PendingEconomics?.Validate(financial.Market.Current.Rate);
    }



    //// Reattaches persisted cash checkpoints after journal replay has established each account's balances.
    ////
    //// A valid checkpoint cannot precede its replayed account or exceed the financial clock.  Advancing to it must
    //// produce no extra monetary records; otherwise trusting the checkpoint would hide missing accrual.  This permits
    //// zero-rounded intervals to retain their materialization position without inventing a new money posting.
    ////
    private static BankState RestoreCheckpoints(BankState snapshot,
        ImmutableDictionary<string, FinancialInstant> checkpoints)
    {
        if (snapshot.Accounts.Count != checkpoints.Count) throw new BankException(BankError.CorruptState);
        foreach (var (player, account) in snapshot.Accounts)
        {
            if (!checkpoints.TryGetValue(player, out var checkpoint) || checkpoint.Months < account.Checkpoint.Months ||
                checkpoint.Months > snapshot.Clock.Position.Months) throw new BankException(BankError.CorruptState);
            var projected = FinanceEngine.Accrue(snapshot, player, checkpoint, snapshot.Clock.WorldAnchor,
                Guid.NewGuid(), null);
            if (projected.Journal.Count != snapshot.Journal.Count) throw new BankException(BankError.CorruptState);
            snapshot = snapshot with { Accounts = snapshot.Accounts.SetItem(player, account with { Checkpoint = checkpoint }) };
        }
        return snapshot;
    }



    //// Checks structural links between retained control state and the reconstructed financial journal.
    ////
    //// Notices must reference existing recipients and journal sequence bounds.  Cached responses must fit retention,
    //// identity, sequence, and revision constraints.
    ////
    private static void ValidateControl(BankState snapshot)
    {
        foreach (var (id, notice) in snapshot.Notices)
            if (id != notice.Id || id == Guid.Empty || notice.Count <= 0 || notice.LastSequence <= 0 ||
                notice.LastSequence > snapshot.Journal.Count || !Enum.IsDefined(notice.Kind) ||
                !Enum.IsDefined(notice.Currency) || !snapshot.Accounts.ContainsKey(notice.Recipient))
                throw new BankException(BankError.CorruptState);
        foreach (var (player, responses) in snapshot.Responses)
            if (responses.Count() > 1024 || responses.Any(r => r.Key.Player != player || r.Key.Sequence <= 0 ||
                r.Result.Revision > snapshot.Revision || !Enum.IsDefined(r.Result.Error)))
                throw new BankException(BankError.CorruptState);
    }



    //// Validates recipient records and builds a fresh case-insensitive index of current canonical names.
    ////
    //// The persisted epoch and observation versions must be valid.  Multiple identities with the same name remain a
    //// valid ambiguous mapping; historical names remain audit-only.  No account creation or player backfill occurs,
    //// and the host must observe only currently authenticated players after restoration.
    ////
    private static BankState RestoreRegistry(BankState snapshot, RegistrySection registry)
    {
        if (registry.Epoch == Guid.Empty || registry.Revision < 0)
            throw new BankException(BankError.CorruptState);
        var index = ImmutableDictionary.Create<string, ImmutableHashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (player, entry) in registry.Names)
        {
            ValidatePlayer(player);
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 256 || entry.MappingVersion <= 0 ||
                entry.MappingVersion > registry.Revision || entry.PreviousNames.IsDefault)
                throw new BankException(BankError.CorruptState);
            index = index.SetItem(entry.Name, index.GetValueOrDefault(entry.Name, []).Add(player));
        }
        return snapshot with
        {
            RegistryEpoch = registry.Epoch,
            RegistryRevision = registry.Revision,
            Names = registry.Names,
            NameIndex = index
        };
    }



}
