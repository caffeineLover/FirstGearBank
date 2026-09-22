/*
 * Implements the privileged, engine-neutral recovery boundary for isolated recipient-registry damage and ambiguous
 * physical settlements.  Callers supply authenticated administrator attribution; the coordinator validates it again,
 * builds one immutable candidate under its existing gate, and publishes a new revision without editing old journal
 * records or inferring inventory state.
 *
 * Registry recovery invalidates transient conversations and confirmation tokens but preserves every financial record.
 * Settlement recovery accepts one explicit physical finding and either publishes the request-bound frozen record batch
 * or publishes no money.  Vintage Story command parsing, file export, connected-player observation, and receipt checks
 * remain host responsibilities.
 */

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FirstGearBank.Core;

/// Privileged recovery portion of the serialized world coordinator.
public sealed partial class BankingCoordinator
{



    //// Exports either the exact isolated registry bytes or a structured current-epoch snapshot for server-only storage.
    ////
    public RegistryRecoveryExport ExportRegistryRecovery()
    {
        lock (gate)
        {
            var raw = quarantinedRegistry;
            RegistryRecoverySnapshot? snapshot = null;
            if (raw.IsEmpty)
            {
                var registry = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                    new RegistrySection(state.RegistryEpoch, state.RegistryRevision, state.Names), JsonOptions);
                snapshot = new(1, state.WorldId, state.RegistryEpoch, state.RegistryRevision, state.Names,
                    DateTimeOffset.UtcNow.ToString("O"), Convert.ToHexString(SHA256.HashData(registry)));
            }
            var bytes = raw.IsEmpty ? Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(snapshot)) :
                raw.ToArray();
            return new(state.WorldId, snapshot, raw, Convert.ToHexString(SHA256.HashData(bytes)));
        }
    }



    //// Atomically installs one separately supplied same-world registry and invalidates all transient token authority.
    ////
    public BankResult RestoreRegistryRecovery(RegistryRecoverySnapshot recovery, RecoveryContext context)
    {
        lock (gate)
        {
            RequireFinance();
            ValidateRecoveryContext(context);
            if (!state.RegistryQuarantined || recovery.Version != 1 || recovery.WorldId != state.WorldId ||
                recovery.Names is null ||
                !DateTimeOffset.TryParse(recovery.ExportedUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out _) || !ValidRegistryHash(recovery))
                throw new BankException(BankError.CorruptState);
            var prior = state.Revision;
            var revision = checked(prior + 1);
            var candidate = RestoreRegistry(state, new(recovery.Epoch, recovery.Revision, recovery.Names)) with
            {
                RegistryQuarantined = false,
                Scopes = ImmutableDictionary<Guid, ScopeState>.Empty,
                Quotes = ImmutableDictionary<Guid, CdQuote>.Empty,
                Confirmations = ImmutableDictionary<Guid, TransferConfirmation>.Empty,
                Revision = revision
            };
            candidate = candidate with
            {
                RecoveryAudit = AppendAudit(candidate.RecoveryAudit, RecoveryAction.RegistryRestore, context,
                    recovery.Epoch, null, prior, revision)
            };
            quarantinedRegistry = [];
            state = candidate;
            return new(BankError.None, revision, []);
        }
    }



    //// Replaces only quarantined recipient authority with a new empty epoch after the host has exported damaged bytes.
    ////
    public BankResult ResetRegistry(RecoveryContext context)
    {
        lock (gate)
        {
            RequireFinance();
            ValidateRecoveryContext(context);
            if (!state.RegistryQuarantined || quarantinedRegistry.IsEmpty)
                throw new BankException(BankError.RecipientServiceUnavailable);
            var prior = state.Revision;
            var revision = checked(prior + 1);
            var epoch = Guid.NewGuid();
            var candidate = state with
            {
                RegistryEpoch = epoch,
                RegistryRevision = 0,
                RegistryQuarantined = false,
                Names = ImmutableDictionary<string, NameEntry>.Empty,
                NameIndex = ImmutableDictionary.Create<string, ImmutableHashSet<string>>(
                    StringComparer.OrdinalIgnoreCase),
                Scopes = ImmutableDictionary<Guid, ScopeState>.Empty,
                Quotes = ImmutableDictionary<Guid, CdQuote>.Empty,
                Confirmations = ImmutableDictionary<Guid, TransferConfirmation>.Empty,
                Revision = revision
            };
            candidate = candidate with
            {
                RecoveryAudit = AppendAudit(candidate.RecoveryAudit, RecoveryAction.RegistryReset, context,
                    epoch, null, prior, revision)
            };
            quarantinedRegistry = [];
            state = candidate;
            return new(BankError.None, revision, []);
        }
    }



    //// Resolves one quarantined settlement from an administrator's explicit physical finding without live inference.
    //// InventoryApplied publishes only the request-bound frozen records; already-materialized unrelated automatic work
    //// is not duplicated.  InventoryNotApplied retains the draft solely as evidence and publishes no financial record.
    ////
    public BankResult ResolveSettlement(Guid settlementId, SettlementFinding finding, RecoveryContext context)
    {
        lock (gate)
        {
            RequireFinance();
            ValidateRecoveryContext(context);
            if (settlementId == Guid.Empty || !Enum.IsDefined(finding) ||
                !state.Settlements.TryGetValue(settlementId, out var settlement) ||
                settlement.Phase != SettlementPhase.Quarantined || settlement.Resolution is not null)
                throw new BankException(BankError.SettlementQuarantined);
            var prior = state.Revision;
            var revision = checked(prior + 1);
            var operations = ImmutableArray<Guid>.Empty;
            var candidate = state;
            if (finding == SettlementFinding.InventoryApplied)
            {
                var records = settlement.PlannedRecords.Where(record => record.Request == settlement.Request).ToArray();
                ValidateSettlementRecords(settlement, records);
                var journal = candidate.Journal;
                var operationIds = journal.Select(record => record.OperationId).ToHashSet();
                foreach (var frozen in records)
                {
                    if (!operationIds.Add(frozen.OperationId)) throw new BankException(BankError.CorruptState);
                    var record = frozen with { Sequence = journal.Count + 1L };
                    Ledger.Validate(record);
                    journal = journal.Add(record);
                    operations = operations.Add(record.OperationId);
                }
                candidate = Ledger.Replay(candidate with { Journal = journal });
                if (settlement.Currency == Currency.Rusty)
                    candidate = FinanceEngine.CheckpointLiquidity(candidate, candidate.Clock.Position, liquidityIndex);
            }
            var resolution = new SettlementResolution(finding, context.Administrator, context.Reason,
                context.UtcTimestamp, revision);
            candidate = candidate with
            {
                Settlements = candidate.Settlements.SetItem(settlementId, settlement with
                {
                    Phase = SettlementPhase.Finalized,
                    Operations = operations,
                    Resolution = resolution
                }),
                Scopes = candidate.Scopes.RemoveRange(candidate.Scopes.Where(pair =>
                    pair.Value.Player == settlement.Request.Player).Select(pair => pair.Key)),
                Quotes = candidate.Quotes.RemoveRange(candidate.Quotes.Where(pair =>
                    pair.Value.Player == settlement.Request.Player).Select(pair => pair.Key)),
                Confirmations = candidate.Confirmations.RemoveRange(candidate.Confirmations.Where(pair =>
                    pair.Value.Player == settlement.Request.Player).Select(pair => pair.Key)),
                Responses = candidate.Responses.Remove(settlement.Request.Player),
                Revision = revision
            };
            candidate = candidate with
            {
                RecoveryAudit = AppendAudit(candidate.RecoveryAudit, RecoveryAction.SettlementResolution, context,
                    settlementId, finding, prior, revision)
            };
            state = candidate;
            return new(BankError.None, revision, operations);
        }
    }



    //// Confirms that the frozen request batch contains exactly one matching physical deposit or withdrawal record.
    ////
    private static void ValidateSettlementRecords(Settlement settlement, JournalRecord[] records)
    {
        if (records.Length == 0 || records.Any(record => record.Request != settlement.Request))
            throw new BankException(BankError.CorruptState);
        var expected = settlement.Direction == CommandKind.Deposit ? TransactionType.Deposit :
            settlement.Direction == CommandKind.Withdraw ? TransactionType.Withdrawal :
            throw new BankException(BankError.CorruptState);
        var physical = records.Where(record => record.Type == expected).ToArray();
        if (physical.Length != 1) throw new BankException(BankError.CorruptState);
        var units = physical[0].Postings.Where(posting => posting.Account == LedgerKind.Customer &&
            posting.Player == settlement.Request.Player && posting.Currency == settlement.Currency)
            .Aggregate(0L, (sum, posting) => checked(sum - posting.SignedUnits));
        if (units != (settlement.Direction == CommandKind.Deposit ? settlement.Units : -settlement.Units))
            throw new BankException(BankError.CorruptState);
    }



    //// Validates bounded trusted attribution before allocating any recovery candidate.
    ////
    private static void ValidateRecoveryContext(RecoveryContext context)
    {
        ValidatePlayer(context.Administrator);
        if (string.IsNullOrWhiteSpace(context.Reason) || Encoding.UTF8.GetByteCount(context.Reason) > 512 ||
            !DateTimeOffset.TryParse(context.UtcTimestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out _))
            throw new BankException(BankError.InvalidAmount);
    }



    //// Recomputes the canonical registry-section checksum without allowing malformed hex to escape as host errors.
    ////
    private static bool ValidRegistryHash(RegistryRecoverySnapshot recovery)
    {
        if (recovery.SourceSha256?.Length != 64) return false;
        try
        {
            var registry = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new RegistrySection(recovery.Epoch, recovery.Revision, recovery.Names), JsonOptions);
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(recovery.SourceSha256),
                SHA256.HashData(registry));
        }
        catch (FormatException)
        {
            return false;
        }
    }



    //// Appends one bounded immutable audit row whose resulting revision is the recovery publication revision.
    ////
    private static ImmutableList<RecoveryAuditRecord> AppendAudit(ImmutableList<RecoveryAuditRecord> audit,
        RecoveryAction action, RecoveryContext context, Guid? target, SettlementFinding? finding, long prior,
        long revision)
    {
        var next = audit.Add(new(Guid.NewGuid(), action, context.Administrator, context.Reason,
            context.UtcTimestamp, target, finding, prior, revision));
        return next.Count <= 4096 ? next : next.RemoveRange(0, next.Count - 4096);
    }



}
