/*
 * Defines the data exchanged between the banking coordinator, its financial services, and the game adapter.
 * BankState is one immutable world revision.  Its journal and financial/control records are server authority;
 * balances, transaction indexes, and cumulative totals are projections maintained alongside that authority.
 * The coordinator builds replacement records and collections before publishing a revision, so readers can retain
 * an earlier snapshot without seeing a partially applied transaction.
 *
 * Monetary long values are six-decimal bank units, not whole gears.  Journal postings use debit-positive signs;
 * CashAccount and display records use positive customer balances.  Decimal notice totals support aggregation beyond
 * an individual balance cap without introducing sub-unit money.  FinancialInstant is measured in financial months,
 * while quote expiry and cooldown deadlines use the host's current-process runtime seconds.
 *
 * Player strings in authority records are authenticated internal identities.  They are not suitable for packets,
 * ordinary logs, or printed statements.  The view records at the end of this file contain display names and opaque
 * operation identifiers instead.  BankCommand expresses intent; CommandContext and IBankingHost provide the trusted
 * execution context.  Host interfaces also define inventory-lock ownership and the exact-liquidity dependency.
 *
 * These contracts contain no Vintage Story types or persistence I/O.  BankPersistence selects which authoritative
 * sections to serialize and which caches to rebuild; immutability alone does not establish a valid restored state.
 */

using System.Collections.Immutable;

namespace FirstGearBank.Core;

/// Distinguishes customer liabilities, CD principal liabilities, and signed system counterparts within each currency.
/// These categories determine posting validation and how ledger signs become customer-facing balances.
public enum LedgerKind
{
    Customer, Custody, InterestExpense, StorageIncome, CdPrincipal, CdInterestExpense,
    CapOverflow, Correction
}
/// Identifies the financial meaning of one independently balanced journal record.
/// TransferIn and TransferOut are derived presentation directions, not separately committable financial records.
public enum TransactionType { Deposit, Withdrawal, Interest, Storage, Transfer, CdPurchase, CdMaturity, AdminCorrection }
/// Selects a normalized coordinator action.  Materialize catches up an existing account without moving physical items.
/// Token-bearing actions resolve their authoritative amounts and recipients from stored server confirmations.
public enum CommandKind { Deposit, Withdraw, Transfer, BuyCd, AdminCorrection, Materialize }
/// Describes an inventory settlement's progress or quarantine status separately from its monetary journal records.
/// Current recovery quarantines unfinished records; phase labels alone do not prove cross-blob save ordering.
public enum SettlementPhase { Prepared, InventoryApplied, BankCommitted, Finalized, Quarantined }

/// One signed ledger movement in bank units, with positive debits and negative credits.
/// Player identifies a customer liability owner; CdId distinguishes a contract where the ledger category requires it.
/// A posting is valid only within an independently balanced record, never as a standalone account update.
public sealed record Posting(LedgerKind Account, Currency Currency, string? Player, Guid? CdId, long SignedUnits);
/// Server-authenticated identity, nonreused conversation scope, and strictly increasing mutation sequence.
/// This complete key distinguishes retries from new intent; a client must never be allowed to choose Player.
public sealed record RequestKey(string Player, Guid Scope, long Sequence);
/// Immutable audit fact for one balanced financial operation; several records can share one atomic CommandId.
/// EffectiveTime is economic time, while WorldCalendarDays records the supplied execution-time calendar reading.
/// Names freezes display metadata, Request links client work to replay protection, and Contract retains CD issue facts.
/// Corrections append records referencing prior operations; existing records have no edit or removal API.
public sealed record JournalRecord(long Sequence, Guid OperationId, Guid CommandId, RequestKey? Request,
    TransactionType Type, FinancialInstant EffectiveTime, decimal WorldCalendarDays,
    ImmutableDictionary<string, string> Names, ImmutableArray<Posting> Postings,
    bool WasCapped = false, string? Reason = null, Guid? RelatedOperation = null, Certificate? Contract = null,
    int SchemaVersion = 1);

/// Customer cash projection with separate rusty and temporal balances and one shared last-materialized instant.
/// Values are nonnegative capped bank units.  Registry observation alone does not create this banking relationship.
public sealed record CashAccount(long RustyUnits, long TemporalUnits, FinancialInstant Checkpoint);
/// Fixed rusty CD contract retaining its original principal, rounded payoff, issue time, and contractual maturity.
/// Quote preserves the locked pricing inputs; a matured replacement record retains the same historical terms.
/// The contract contains an internal owner key and must be translated into CertificateView before client delivery.
public sealed record Certificate(Guid Id, string Player, long PrincipalUnits, long MaturityUnits,
    FinancialInstant Issued, FinancialInstant Matures, CdQuote Quote, bool Matured = false);
/// Server-side price and eligibility snapshot bound to one player and Banker conversation for thirty runtime seconds.
/// Spread components and Yield are annual continuous decimals; principal and payoff are rounded bank units.
/// Confirmation consumes this snapshot without repricing it when the current month or active minimum has changed.
public sealed record CdQuote(Guid Id, string Player, Guid Scope, long PrincipalUnits, int TenorMonths,
    decimal ExpiresAtSeconds, FinancialInstant PricedAt, long ConfigurationRevision, long MinimumPrincipalUnits,
    double RiskFreeYield, double BaseSpread, double LevelSpread, double SlopeSpread, double LiquiditySpread,
    double Yield, long MaturityUnits);
/// Short-lived transfer authorization binding a sender conversation to one observed recipient name mapping.
/// Null ExactUnits means AllAtCommit: transfer the exact six-decimal post-accrual balance, not a displayed estimate.
/// The epoch, entry version, current name, and uniqueness are revalidated before this intent can move money.
public sealed record TransferConfirmation(Guid Id, string Player, Guid Scope, Guid RegistryEpoch,
    string Recipient, long MappingVersion, string CanonicalName, long? ExactUnits, decimal ExpiresAtSeconds);
/// Latest authenticated canonical name and its mapping revision in the world's current registry epoch.
/// PreviousNames is immutable audit history; none of its entries is a selectable or resolvable transfer alias.
public sealed record NameEntry(string Name, long MappingVersion, ImmutableArray<string> PreviousNames);
/// Conversation ownership, last finalized mutation sequence, and display precision captured when the scope opened.
/// Failures advance HighWater too, preventing the same failed intent from becoming executable later under its old key.
public sealed record ScopeState(string Player, long HighWater, int DisplayPrecision);
/// Terminal coordinator response containing a safe error code, published revision, and correlated operation IDs.
/// An identical cached retry returns this original result rather than describing the bank's latest revision.
public sealed record BankResult(BankError Error, long Revision, ImmutableArray<Guid> Operations);
/// Original terminal result paired with its full request key and normalized command digest.
/// Response bodies have bounded retention; pruning a body does not authorize reuse of a committed request key.
public sealed record CachedResponse(RequestKey Key, string Digest, BankResult Result);
/// Durable notification intent for an individual event or an accumulating offline summary of one kind and currency.
/// Unsealed buckets may grow; sealing fixes the content offered for acknowledgment under a stable notice ID.
/// Delivery state is separate from journal money and contains an internal recipient identity, so use NoticeView on
/// wire.
public sealed record Notice(Guid Id, string Recipient, TransactionType Kind, Currency Currency,
    string? SenderName, decimal TotalUnits, long Count, long LastSequence, bool Sealed);
/// Host-produced inventory fingerprints and exact slot mutations for a preflighted physical settlement.
/// These facts support comparison and recovery investigation; this record itself is not an authenticated capsule.
public sealed record InventoryManifest(string FingerprintBefore, string FingerprintAfter,
    ImmutableArray<InventoryDelta> Deltas);
/// Before and after representations of one identified inventory slot under the host's serialization format.
/// The core retains the strings as evidence; decoding item stacks and validating inventory identity belong to the host.
public sealed record InventoryDelta(string InventoryId, int Slot, string BeforeSerialized, string AfterSerialized);
/// Physical deposit/withdrawal evidence connecting an inventory manifest to a request and its proposed journal batch.
/// PlannedRecords retains reconstruction inputs; Operations identifies the corresponding published records on success.
/// Finalized records remain replay evidence, while unfinished restored records quarantine the affected player's access.
public sealed record Settlement(Guid Id, RequestKey Request, CommandKind Direction, Currency Currency,
    long Units, InventoryManifest Manifest, SettlementPhase Phase, ImmutableArray<Guid> Operations,
    ImmutableArray<JournalRecord> PlannedRecords, SettlementResolution? Resolution = null);

/// Administrator's explicit finding for whether the physical side of an ambiguous settlement reached saved inventory.
public enum SettlementFinding { InventoryApplied, InventoryNotApplied }

/// Durable categories for privileged recovery actions that do not rewrite prior financial history.
public enum RecoveryAction { RegistryRestore, RegistryReset, SettlementResolution }

/// Trusted server-supplied administrator attribution required before any recovery candidate can be published.
public sealed record RecoveryContext(string Administrator, string Reason, string UtcTimestamp);

/// Immutable settlement-resolution evidence retained with the original manifest and planned journal records.
public sealed record SettlementResolution(SettlementFinding Finding, string Administrator, string Reason,
    string UtcTimestamp, long Revision);

/// Immutable world-level audit event for registry and inventory recovery administration.
public sealed record RecoveryAuditRecord(Guid Id, RecoveryAction Action, string Administrator, string Reason,
    string UtcTimestamp, Guid? Target, SettlementFinding? Finding, long PriorRevision, long ResultingRevision);

/// Portable, independently validated current-epoch registry snapshot used only by privileged recovery commands.
/// SourceSha256 covers the canonical registry section; ExportedUtc describes the evidence copy, not financial time.
public sealed record RegistryRecoverySnapshot(int Version, string WorldId, Guid Epoch, long Revision,
    ImmutableDictionary<string, NameEntry> Names, string ExportedUtc, string SourceSha256);

/// Recovery export containing either a healthy structured snapshot or exact quarantined registry payload bytes.
public sealed record RegistryRecoveryExport(string WorldId, RegistryRecoverySnapshot? Snapshot,
    ImmutableArray<byte> QuarantinedBytes, string Sha256);
/// Held CD liquidity-spread target and the observed adjustment at its last financial-time checkpoint.
/// Between checkpoints the observation decays toward Target using this stored half-life, without quote-driven writes.
public sealed record LiquidityState(FinancialInstant Checkpoint, double Observed, double Target, double HalfLifeMonths);
/// Non-economic display and interaction defaults owned by the coordinator.
/// Display precision affects entered transfer amounts, cooldown uses runtime seconds, and statement count is for the
/// host.
public sealed record CoreOptions(int RustyDisplayPrecision = 3, decimal TransferCooldownSeconds = 1,
    int PrintedRecentTransactions = 10);

/// Complete immutable in-memory state for one world, published exclusively by BankingCoordinator.
/// Financial helpers return modified candidates; readers retain coherent snapshots while subsequent revisions are
/// built.
/// Journal replay rebuilds monetary caches, but names, delivery facts, and settlement evidence have independent
/// authority.
/// The entire object is privileged server data and must never be serialized directly as a player-facing response.
public sealed record BankState
{
    // World identity, monotonic time, and realized rates give journal projections their economic interpretation.
    public required string WorldId { get; init; }
    public required FinancialClock Clock { get; init; }
    public required MarketHistory Market { get; init; }
    public required LiquidityState Liquidity { get; init; }
    public long Revision { get; init; }
    public CoreOptions Options { get; init; } = new();
    // Pending economics activates at the next boundary; current quotes and issued contracts retain their snapshots.
    public EconomicSettings? PendingEconomics { get; init; }
    // Registry and finance quarantine are separate so a broken name index need not disable healthy personal accounts.
    public Guid RegistryEpoch { get; init; } = Guid.NewGuid();
    public long RegistryRevision { get; init; }
    public bool RegistryQuarantined { get; init; }
    public bool FinanceQuarantined { get; init; }
    // Account/CD projections and the maturity priority index accompany the append-only source journal.
    public ImmutableDictionary<string, CashAccount> Accounts { get; init; } =
        ImmutableDictionary<string, CashAccount>.Empty;
    public ImmutableList<JournalRecord> Journal { get; init; } = [];
    public ImmutableDictionary<Guid, Certificate> Certificates { get; init; } =
        ImmutableDictionary<Guid, Certificate>.Empty;
    public ImmutableSortedSet<(decimal Time, Guid Id)> Maturities { get; init; } = [];
    // The reverse current-name index permits ambiguity; each matched identity must be considered during resolution.
    public ImmutableDictionary<string, NameEntry> Names { get; init; } = ImmutableDictionary<string, NameEntry>.Empty;
    public ImmutableDictionary<string, ImmutableHashSet<string>> NameIndex { get; init; } =
        ImmutableDictionary.Create<string, ImmutableHashSet<string>>(StringComparer.OrdinalIgnoreCase);
    public ImmutableDictionary<Guid, ScopeState> Scopes { get; init; } = ImmutableDictionary<Guid, ScopeState>.Empty;
    // Full response retention is bounded per player; committed request identities outlive their response bodies.
    public ImmutableDictionary<string, ImmutableQueue<CachedResponse>> Responses { get; init; } =
        ImmutableDictionary<string, ImmutableQueue<CachedResponse>>.Empty;
    public ImmutableHashSet<RequestKey> CommittedRequests { get; init; } = [];
    // Conversation tokens are internal authority, invalidated when their owning scope closes or the process restarts.
    public ImmutableDictionary<Guid, CdQuote> Quotes { get; init; } = ImmutableDictionary<Guid, CdQuote>.Empty;
    public ImmutableDictionary<Guid, TransferConfirmation> Confirmations { get; init; } =
        ImmutableDictionary<Guid, TransferConfirmation>.Empty;
    public ImmutableDictionary<string, decimal> Cooldowns { get; init; } = ImmutableDictionary<string, decimal>.Empty;
    // Acknowledgment and inventory progress are control facts that cannot be inferred from balance totals alone.
    public ImmutableDictionary<Guid, Notice> Notices { get; init; } = ImmutableDictionary<Guid, Notice>.Empty;
    public ImmutableDictionary<string, long> DeliveryWatermarks { get; init; } = ImmutableDictionary<string, long>.Empty;
    public ImmutableDictionary<Guid, Settlement> Settlements { get; init; } = ImmutableDictionary<Guid, Settlement>.Empty;
    public ImmutableList<RecoveryAuditRecord> RecoveryAudit { get; init; } = [];
    // History stores journal sequence numbers; total keys combine currency with an operation or transfer direction.
    public ImmutableDictionary<string, ImmutableList<long>> History { get; init; } =
        ImmutableDictionary<string, ImmutableList<long>>.Empty;
    public ImmutableDictionary<string, ImmutableDictionary<string, decimal>> Totals { get; init; } =
        ImmutableDictionary<string, ImmutableDictionary<string, decimal>>.Empty;
}

/// Normalized business intent submitted after the adapter has bounded and authenticated a client request.
/// Units is an explicit bank-unit amount; token-bearing operations instead read stored quote or recipient authority.
/// Reason and RelatedOperation are used for auditable administrative corrections, not arbitrary balance replacement.
public sealed record BankCommand(CommandKind Kind, Currency Currency = Currency.Rusty, long Units = 0,
    Guid Token = default, string? RecipientName = null, string? Reason = null, Guid? RelatedOperation = null);

/// Trusted execution identity and sequence derived by the server adapter from its authenticated conversation.
/// Permissions and Banker availability are still checked through the host when the queued operation executes.
public sealed record CommandContext(RequestKey Request);

/// Adapter boundary supplying current server authority without exposing Vintage Story objects to the banking core.
/// The coordinator invokes these methods while serializing requests; implementations must not dispatch nested
/// mutations.
/// Inventory preparation transfers a retained lock to IInventoryChange, while all authentication remains host-owned.
public interface IBankingHost
{



    //// Captures calendar position, current calendar settings, and the current-process monotonic runtime together.
    //// The coordinator uses this one sample for all participants in an operation.  Runtime speed must represent
    //// ordinary unslept game progression, and restart handling must not compare clocks from different processes.
    ////
    ClockSample SampleClock();



    //// Confirms that the authenticated player still owns a usable conversation with a reachable, available Banker.
    //// The check occurs when work executes, so a stale dialog cannot transact after departure or invalidation.
    ////
    bool IsSessionValid(string player, Guid scope);



    //// Supplies the trusted display name used when freezing journal and statement metadata.
    //// The host must support the named participant and keep internal identity out of this result, including when
    //// name-resolution services are quarantined but the player can still access their own account.
    ////
    string PlayerName(string player);



    //// Reports whether the recipient currently has an authenticated connection for immediate notification delivery.
    //// This affects outbox grouping only; connection state neither establishes account ownership nor changes money.
    ////
    bool IsOnline(string player);



    //// Checks current correction privileges for the authenticated server identity at the moment of execution.
    //// The result must come from server permission state, never from a boolean supplied in a client command.
    ////
    bool CanCorrectBalances(string player);



    //// Locks authoritative inventory and prepares the full requested deposit or withdrawal without applying it.
    //// The returned change must describe exact slot deltas and independently computed units, rejecting insufficient
    //// items, capacity, or invalid denominations.  Its lock remains held through Apply/Rollback until disposal.
    ////
    IInventoryChange PrepareInventory(string player, Currency currency, long units, bool withdrawal);



}

/// Host-owned, disposable inventory transaction prepared under a retained lock for one exact physical amount.
/// The coordinator compares VerifiedUnits to the request and retains Manifest as evidence before calling Apply.
/// Rollback must restore even a partially applied mutation; disposal releases resources and does not move money.
/// This contract covers live execution, not a durable transaction spanning inventory and the bank world-save blob.
public interface IInventoryChange : IDisposable
{
    InventoryManifest Manifest { get; }
    long VerifiedUnits { get; }



    //// Applies the complete preflighted slot changes while the inventory lock remains owned by this transaction.
    //// If application throws after partial work, Rollback must still be able to restore the captured before-state.
    ////
    void Apply();



    //// Restores the captured inventory before-state when live application fails before financial publication.
    //// A rollback failure leaves the settlement quarantined, preventing further banking until trusted reconciliation.
    ////
    void Rollback();



}

/// Supplies exact economically accrued rusty liabilities and remaining-tenor-weighted CD funding at a checkpoint.
/// The coordinator requires this dependency because rounding an aggregate cannot reproduce per-account rounding.
/// ScanningLiquidityIndex is an explicit linear-cost fallback; the specification's no-scan implementation is
/// unresolved.
public interface IExactLiquidityIndex
{



    //// Returns exact economically accrued rusty liabilities and weighted CD funding at the supplied instant.
    //// The implementation must preserve per-account rounding and may not mutate the supplied immutable revision.
    //// LiabilityUnits includes accrued rusty savings plus original active CD principal; temporal balances are
    //// excluded.
    //// WeightedFundingUnits weights each active principal by remaining financial months divided by twelve.
    //// Due maturities are coordinated by FinanceEngine before their corresponding checkpoint is evaluated.
    ////
    (decimal LiabilityUnits, double WeightedFundingUnits) Evaluate(BankState state, FinancialInstant instant);



}

/// One name-only, directional transaction row derived from a journal record for the requesting account holder.
/// ChangeUnits uses the customer's displayed-balance sign, and Sequence links pagination to immutable history.
public sealed record StatementRow(long Sequence, string Type, Currency Currency, long ChangeUnits,
    FinancialInstant EffectiveTime, ImmutableArray<string> Names, bool WasCapped);
/// Player-safe active-CD projection with a nonspendable informational value and the unchanged contractual endpoints.
/// The opaque CD ID can identify the contract in UI; no internal owner key or live account authority is included.
public sealed record CertificateView(Guid Id, long PrincipalUnits, long CurrentValueUnits, long MaturityUnits,
    FinancialInstant Issued, FinancialInstant Matures, double LockedYield);
/// Immutable account display snapshot produced after accrual and due maturities have been materialized.
/// It combines balances, rate displays, cumulative totals, active contracts, and one requested history page.
/// TimeBasis tells the adapter how to label financial dates; this object carries no player UID or printing entitlement.
public sealed record Statement(string HolderName, FinancialInstant AsOf, InterestTimeBasis TimeBasis,
    long RustyUnits, long TemporalUnits, double ContinuousRate, double MonthlyEffectiveRate,
    double AnnualizedEffectiveYield, ImmutableDictionary<string, decimal> Totals,
    ImmutableArray<CertificateView> Certificates, ImmutableArray<StatementRow> History, long TotalHistoryRows);
/// Player-safe notification content for acknowledgment under a stable notice ID.
/// A null sender name denotes an aggregate summary whose individual counterparties remain available in history.
public sealed record NoticeView(Guid Id, TransactionType Kind, Currency Currency, string? SenderName,
    decimal TotalUnits, long Count);
/// Displayable CD offer exposing its exact payoff, tenor, expiry, and annual continuous pricing components.
/// Confirmation submits only the opaque ID; authoritative owner, session, and eligibility remain in the stored CdQuote.
public sealed record CdQuoteView(Guid Id, long PrincipalUnits, int TenorMonths, decimal ExpiresAtSeconds,
    double RiskFreeYield, double BaseSpread, double LevelSpread, double SlopeSpread, double LiquiditySpread,
    double Yield, long MaturityUnits);
