/*
 * Maintains the bank's append-only monetary journal and its account, contract, history, and total projections.
 * Every record balances independently within each currency using debit-positive posting signs.  Customer balances
 * use the opposite sign because they represent bank liabilities; signed system accounts remain distinct from capped,
 * nonnegative savings and vault balances.  BigInteger is used for posting sums before checked conversion to money.
 *
 * Append constructs and validates one new record, then applies it to an immutable candidate.  Replay validates an
 * existing ordered journal and rebuilds monetary projections with the same application rules.  Neither path publishes
 * the candidate: BankingCoordinator owns the atomic boundary for the entire command, including multi-record accrual.
 *
 * Account creation is restricted to deposits and incoming transfers.  CD purchase/maturity records maintain immutable
 * contract history and the due-time index.  Transfer direction and cumulative totals are derived per participant;
 * no second financial record is created merely to render the recipient's view of a transfer.
 *
 * Recipient observations, delivery acknowledgments, and physical inventory evidence have separate authority and are
 * not reconstructed from cash totals here.  This file performs no engine calls, persistence I/O, or automatic repair
 * of a corrupt monetary record; validation errors propagate to the coordinator or startup handling.
 */

using System.Collections.Immutable;
using System.Numerics;

namespace FirstGearBank.Core;

/// Pure candidate-state operations for journal validation, append, and monetary projection replay.
/// Record-level validity is checked here, while the coordinator controls which complete batch becomes visible.
/// No method edits committed records or supplies a privileged direct-balance replacement path.
internal static class Ledger
{



    //// Constructs the next immutable journal record and applies its checked effect to a private state candidate.
    ////
    //// Sequence is allocated from the current journal length and zero postings are omitted.  Operation identity is
    //// fresh, while CommandId correlates this record with other work in the same eventual commit.  Validation and
    //// projection application finish before the returned candidate receives the appended record; nothing is published.
    ////
    internal static BankState Append(BankState state, Guid command, RequestKey? request, TransactionType type,
        FinancialInstant time, decimal worldDays, ImmutableDictionary<string, string> names,
        IEnumerable<Posting> postings, bool capped = false, string? reason = null,
        Guid? related = null, Certificate? contract = null)
    {
        var record = new JournalRecord(state.Journal.Count + 1L, Guid.NewGuid(), command, request, type,
            time, worldDays, names, postings.Where(p => p.SignedUnits != 0).ToImmutableArray(),
            capped, reason, related, contract);
        Validate(record);
        return Apply(state, record) with { Journal = state.Journal.Add(record) };
    }



    //// Validates record structure, balanced currency sums, and the implemented transaction-shape constraints.
    ////
    //// Unbounded integer summation prevents overflow while checking offsetting postings.  Customer movements require
    //// name snapshots, transfers require two distinct rusty liabilities, and ordinary operations require the matching
    //// system counterpart and sign.  CD records also check their retained contract and principal posting.
    //// Invalid structure is rejected; this method never changes or normalizes stored monetary facts.
    ////
    internal static void Validate(JournalRecord record)
    {
        record.EffectiveTime.Validate();
        if (record.SchemaVersion != 1 || record.Sequence <= 0 || record.OperationId == Guid.Empty ||
            record.CommandId == Guid.Empty || !Enum.IsDefined(record.Type) || record.Postings.IsDefault)
            throw new BankException(BankError.CorruptState);
        foreach (var posting in record.Postings)
            if (!Enum.IsDefined(posting.Currency) || !Enum.IsDefined(posting.Account) || posting.SignedUnits == 0 ||
                (posting.Account == LedgerKind.Customer && string.IsNullOrWhiteSpace(posting.Player)))
                throw new BankException(BankError.CorruptState);
        // Currency isolation prevents a rusty debit from concealing an unrelated temporal credit.
        foreach (var group in record.Postings.GroupBy(p => p.Currency))
            if (group.Aggregate(BigInteger.Zero, (sum, p) => sum + p.SignedUnits) != 0)
                throw new BankException(BankError.CorruptState);
        var customer = record.Postings.Where(p => p.Account == LedgerKind.Customer).ToArray();
        var system = record.Postings.Where(p => p.Account != LedgerKind.Customer).ToArray();
        if (customer.Any(p => !record.Names.ContainsKey(p.Player!)))
            throw new BankException(BankError.CorruptState);
        if (record.Type is TransactionType.CdPurchase or TransactionType.CdMaturity)
        {
            if (record.Contract is not { } cd || cd.Id == Guid.Empty || !double.IsFinite(cd.Quote.Yield) ||
                cd.PrincipalUnits != cd.Quote.PrincipalUnits || cd.MaturityUnits != cd.Quote.MaturityUnits ||
                cd.Quote.Player != cd.Player || cd.Quote.TenorMonths <= 0 ||
                cd.Matures.Months != cd.Issued.Months + cd.Quote.TenorMonths ||
                record.Postings.Any(p => p.Currency != Currency.Rusty))
                throw new BankException(BankError.CorruptState);
            var expectedPrincipal = record.Type == TransactionType.CdPurchase ? -cd.PrincipalUnits : cd.PrincipalUnits;
            if (system.Count(p => p.Account == LedgerKind.CdPrincipal && p.CdId == cd.Id &&
                    p.Player == cd.Player && p.SignedUnits == expectedPrincipal) != 1)
                throw new BankException(BankError.CorruptState);
            return;
        }
        if (record.Contract is not null) throw new BankException(BankError.CorruptState);
        if (record.Type == TransactionType.Transfer)
        {
            if (system.Length != 0 || customer.Length != 2 || customer[0].Player == customer[1].Player ||
                customer.Any(p => p.Currency != Currency.Rusty)) throw new BankException(BankError.CorruptState);
            return;
        }
        // Balance alone is insufficient: the counterpart category and direction must fit the claimed business action.
        var counterpart = record.Type switch
        {
            TransactionType.Deposit or TransactionType.Withdrawal => LedgerKind.Custody,
            TransactionType.Interest => LedgerKind.InterestExpense,
            TransactionType.Storage => LedgerKind.StorageIncome,
            TransactionType.AdminCorrection => LedgerKind.Correction,
            _ => throw new BankException(BankError.CorruptState)
        };
        if (customer.Length != 1 || system.Length != 1 || system[0].Account != counterpart ||
            customer[0].Currency != system[0].Currency ||
            (record.Type is TransactionType.Deposit or TransactionType.Interest && system[0].SignedUnits <= 0) ||
            (record.Type is TransactionType.Withdrawal or TransactionType.Storage && system[0].SignedUnits >= 0) ||
            (record.Type == TransactionType.Storage && customer[0].Currency != Currency.Temporal) ||
            (record.Type == TransactionType.AdminCorrection && string.IsNullOrWhiteSpace(record.Reason)))
            throw new BankException(BankError.CorruptState);
    }



    //// Reconstructs cash, CDs, history, cumulative totals, maturity ordering, and committed request keys from sequence
    //// one.
    ////
    //// Source records must have contiguous sequences, unique operation IDs, valid posting shapes, and effective times
    //// within the restored clock.  Applying the same monetary rules used for new records makes a cache subordinate
    //// to journal authority.  Nonfinancial delivery, registry, and settlement state stays on the supplied candidate.
    ////
    internal static BankState Replay(BankState state)
    {
        // Clear only projections rebuilt here; independent control facts must not be guessed from financial postings.
        var replay = state with
        {
            Accounts = ImmutableDictionary<string, CashAccount>.Empty,
            Certificates = ImmutableDictionary<Guid, Certificate>.Empty,
            History = ImmutableDictionary<string, ImmutableList<long>>.Empty,
            Totals = ImmutableDictionary<string, ImmutableDictionary<string, decimal>>.Empty,
            Maturities = ImmutableSortedSet<(decimal, Guid)>.Empty,
            CommittedRequests = []
        };
        var operations = new HashSet<Guid>();
        for (var i = 0; i < state.Journal.Count; i++)
        {
            var record = state.Journal[i];
            Validate(record);
            if (record.Sequence != i + 1L || !operations.Add(record.OperationId))
                throw new BankException(BankError.CorruptState);
            if (record.EffectiveTime.Months > state.Clock.Position.Months)
                throw new BankException(BankError.CorruptState);
            replay = Apply(replay, record);
            if (record.Request is { } request)
                replay = replay with { CommittedRequests = replay.CommittedRequests.Add(request) };
        }
        return replay;
    }



    //// Applies a validated record to the affected account and contract projections in deterministic identity order.
    ////
    //// Customer balances subtract ledger signs and are checked for negative values or cap overflow before retention.
    //// History stores one sequence per participant, while totals derive the customer's direction and CD interest.
    //// A CD issue enters the due-time index; maturity marks the existing contract settled and removes its due entry.
    //// Every change remains local to the returned immutable candidate until its enclosing command is published.
    ////
    private static BankState Apply(BankState state, JournalRecord record)
    {
        var accounts = state.Accounts;
        var history = state.History;
        var totals = state.Totals;
        var players = record.Postings.Where(p => p.Account == LedgerKind.Customer).Select(p => p.Player!)
            .Concat(record.Contract is { } contract ? [contract.Player] : []).Distinct().Order(StringComparer.Ordinal);
        foreach (var player in players)
        {
            if (!accounts.TryGetValue(player, out var account))
            {
                if (record.Type is not (TransactionType.Deposit or TransactionType.Transfer))
                    throw new BankException(BankError.NoAccount);
                account = new(0, 0, record.EffectiveTime);
            }
            var playerTotals = totals.GetValueOrDefault(player, ImmutableDictionary<string, decimal>.Empty);
            foreach (var currency in Enum.GetValues<Currency>())
            {
                var signed = record.Postings.Where(p => p.Account == LedgerKind.Customer && p.Player == player &&
                    p.Currency == currency).Aggregate(BigInteger.Zero, (sum, p) => sum + p.SignedUnits);
                // Liability credits increase displayed cash; system-account signs are never used as customer balances.
                var units = checked((long)((currency == Currency.Rusty ? account.RustyUnits : account.TemporalUnits) -
                    signed));
                Money.RequireBalance(new(units));
                account = currency == Currency.Rusty ? account with { RustyUnits = units } :
                    account with { TemporalUnits = units };
                if (signed != 0)
                {
                    // Both transfer directions refer to this same journal record; totals are customer-facing views.
                    var label = record.Type == TransactionType.Transfer
                        ? signed > 0 ? "TransferOut" : "TransferIn" : record.Type.ToString();
                    var key = currency + ":" + label;
                    var delta = checked((decimal)BigInteger.Abs(signed));
                    if (record.Type == TransactionType.AdminCorrection) delta = -(decimal)signed;
                    playerTotals = playerTotals.SetItem(key, playerTotals.GetValueOrDefault(key) + delta);
                }
            }
            if (record.Type == TransactionType.CdMaturity && record.Contract is { } matured)
                playerTotals = playerTotals.SetItem("Rusty:CdInterest",
                    playerTotals.GetValueOrDefault("Rusty:CdInterest") + matured.MaturityUnits - matured.PrincipalUnits);
            accounts = accounts.SetItem(player, account with { Checkpoint = record.EffectiveTime });
            totals = totals.SetItem(player, playerTotals);
            history = history.SetItem(player, history.GetValueOrDefault(player, []).Add(record.Sequence));
        }
        state = state with { Accounts = accounts, History = history, Totals = totals };
        // Contract lifecycle is derived from issue/maturity facts rather than deleting paid contracts from history.
        if (record.Contract is { } cd)
        {
            var maturity = (cd.Matures.Months, cd.Id);
            if (record.Type == TransactionType.CdPurchase)
            {
                if (state.Certificates.ContainsKey(cd.Id) || cd.Matured || cd.PrincipalUnits <= 0 ||
                    cd.MaturityUnits < 0 || cd.MaturityUnits > Money.MaximumUnits ||
                    cd.Matures.Months <= cd.Issued.Months)
                    throw new BankException(BankError.CorruptState);
                state = state with
                {
                    Certificates = state.Certificates.Add(cd.Id, cd),
                    Maturities = state.Maturities.Add(maturity)
                };
            }
            else if (record.Type == TransactionType.CdMaturity)
            {
                if (!state.Certificates.TryGetValue(cd.Id, out var original) || original.Matured ||
                    original.PrincipalUnits != cd.PrincipalUnits || original.MaturityUnits != cd.MaturityUnits ||
                    original.Player != cd.Player || original.Matures != record.EffectiveTime)
                    throw new BankException(BankError.CorruptState);
                state = state with
                {
                    Certificates = state.Certificates.SetItem(cd.Id, original with { Matured = true }),
                    Maturities = state.Maturities.Remove(maturity)
                };
            }
        }
        return state;
    }



}
