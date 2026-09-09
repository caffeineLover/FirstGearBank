/*
 * Coordinates financial-time events and cash accrual inside an unpublished bank-state candidate.
 * BankingCoordinator supplies one trusted clock sample and command identity; this helper processes the resulting
 * month boundaries and due CDs, then returns immutable state for the coordinator's eventual publication.
 * It does not authenticate requests, hold inventory locks, or write world-save data.
 *
 * Global time work follows the next month boundary or maturity queue entry.  Cash accounts are materialized lazily
 * when involved in an operation or maturity.  Each crossed month uses its stored interest-rate parameters and rounds
 * that interval separately, preserving the six-decimal posting behavior of chronological settlement.
 *
 * Rusty savings receives gross floating interest.  Temporal vaults separately post gross interest and the storage
 * charge needed to reach growth at rate minus theta.  All intermediate journal/projection work remains private to
 * the candidate; the coordinator publishes the resulting net cash balance with the complete record batch.
 *
 * CD maturity uses the contract's stored payoff and routes finite excess above the cash ceiling to CapOverflow.
 * Liquidity checkpoints first decay the previous observation, then obtain an exact accrued funding projection from
 * IExactLiquidityIndex.  The cost of that dependency is explicit; this helper must not substitute stale cash rows
 * or a rounded aggregate for independently rounded account liabilities.
 */

using System.Collections.Immutable;

namespace FirstGearBank.Core;

/// Stateless financial-event and accrual transformations used while the coordinator builds an immutable candidate.
/// Returned state includes necessary journal/projection changes, but this class never publishes a live revision itself.
/// Mathematical rate selection lives in InterestRates.cs; this class determines when those rates affect customer money.
internal static class FinanceEngine
{



    //// Advances the financial clock and processes every due month boundary or CD maturity through the target instant.
    ////
    //// The earliest event wins; maturity IDs provide stable ordering for simultaneous contracts.  Events at a boundary
    //// settle under the closing month's rates before pending economics is activated and new rates/shocks are selected.
    //// Uninvolved cash accounts retain lazy checkpoints.  Liquidity checkpoint cost is delegated to the supplied
    //// index.
    ////
    internal static BankState Advance(BankState state, ClockSample sample, IExactLiquidityIndex index, Guid command)
    {
        var clock = state.Clock.Advance(sample);
        var target = clock.Position;
        while (true)
        {
            // Taking the minimum permits part-month maturities without a per-account polling loop.
            var boundary = checked(state.Market.Current.Serial + 1);
            var maturity = state.Maturities.IsEmpty ? decimal.MaxValue : state.Maturities.Min.Time;
            var next = Math.Min(boundary, maturity);
            if (next > target.Months) break;
            var instant = new FinancialInstant(next);
            // Maturities at the boundary settle using the closing market before new configuration takes effect.
            while (!state.Maturities.IsEmpty && state.Maturities.Min.Time == next)
            {
                var cd = state.Certificates[state.Maturities.Min.Id];
                state = Accrue(state, cd.Player, instant, sample.WorldDays, command, null);
                state = Mature(state, cd, sample.WorldDays, command);
                state = CheckpointLiquidity(state, instant, index);
            }
            if (next == boundary)
            {
                state = state with { Market = state.Market.EnterMonth(state.PendingEconomics), PendingEconomics = null };
                state = CheckpointLiquidity(state, instant, index);
            }
        }
        return state with { Clock = clock };
    }



    //// Materializes one existing account's rusty and temporal cash to a supplied financial instant.
    ////
    //// Each interval ends at the next monthly boundary or the target, uses the stored month parameters, and begins
    //// from the previously posted balance.  Nonzero interest/storage becomes independently balanced journal records;
    //// zero-rounded intervals still advance the checkpoint.  Missing accounts remain absent, and backward target
    //// time rejects instead of reversing previous accrual.  Caller-owned candidate publication keeps the batch atomic.
    ////
    internal static BankState Accrue(BankState state, string player, FinancialInstant target, decimal worldDays,
        Guid command, RequestKey? request)
    {
        if (!state.Accounts.TryGetValue(player, out var account)) return state;
        if (target.Months < account.Checkpoint.Months) throw new BankException(BankError.InvalidTime);
        var time = account.Checkpoint;
        var names = ImmutableDictionary<string, string>.Empty.Add(player,
            state.Names.GetValueOrDefault(player)?.Name ?? "Account holder");
        while (time.Months < target.Months)
        {
            var end = new FinancialInstant(Math.Min(target.Months, time.Month + 1m));
            var market = state.Market.Months[time.Month];
            var years = (double)((end.Months - time.Months) / 12m);
            account = state.Accounts[player];
            // Both temporal endpoints start from the same opening cash, making the storage charge gross minus net.
            var rusty = Money.Compound(new(account.RustyUnits), market.Rate * years);
            var gross = Money.Compound(new(account.TemporalUnits), market.Rate * years);
            var net = Money.Compound(new(account.TemporalUnits), (market.Rate - market.Theta) * years);
            var rustyInterest = rusty.Value.Units - account.RustyUnits;
            var temporalInterest = gross.Value.Units - account.TemporalUnits;
            var storage = gross.Value.Units - net.Value.Units;
            // Omit zero-value journal operations while retaining representable interest, storage, and cap metadata.
            if (rustyInterest != 0)
                state = Ledger.Append(state, command, request, TransactionType.Interest, end, worldDays, names,
                    Pair(player, Currency.Rusty, LedgerKind.InterestExpense, rustyInterest), rusty.WasCapped);
            if (temporalInterest != 0)
                state = Ledger.Append(state, command, request, TransactionType.Interest, end, worldDays, names,
                    Pair(player, Currency.Temporal, LedgerKind.InterestExpense, temporalInterest), gross.WasCapped);
            if (storage != 0)
                state = Ledger.Append(state, command, request, TransactionType.Storage, end, worldDays, names,
                    Pair(player, Currency.Temporal, LedgerKind.StorageIncome, -storage), gross.WasCapped || net.WasCapped);
            state = state with
            {
                Accounts = state.Accounts.SetItem(player,
                state.Accounts[player] with { Checkpoint = end })
            };
            time = end;
        }
        return state;
    }



    //// Projects rusty savings to a target using the same month-by-month cash endpoint rule as materialization.
    //// The liquidity index uses this read-only calculation for accounts whose stored balances have not caught up.
    //// It does not settle CDs, modify checkpoints, or append records; global due maturities are handled separately.
    ////
    internal static long ProjectRusty(BankState state, CashAccount account, FinancialInstant target)
    {
        if (target.Months < account.Checkpoint.Months) throw new BankException(BankError.InvalidTime);
        var balance = new Money(account.RustyUnits);
        var time = account.Checkpoint;
        while (time.Months < target.Months)
        {
            var end = Math.Min(target.Months, time.Month + 1m);
            balance = Money.Compound(balance, state.Market.Months[time.Month].Rate *
                (double)((end - time.Months) / 12m)).Value;
            time = new(end);
        }
        return balance.Units;
    }



    //// Builds the balanced system/customer postings corresponding to a signed change in displayed customer cash.
    //// Positive delta debits the system account and credits the customer liability; negative delta reverses both.
    //// The selected system category explains the business counterpart, and Ledger removes zero postings on append.
    ////
    internal static ImmutableArray<Posting> Pair(string player, Currency currency, LedgerKind system, long delta)
    {
        return [new(system, currency, null, null, delta), new(LedgerKind.Customer, currency, player, null, -delta)];
    }



    //// Settles one due CD after its owner's cash has been accrued to the exact contractual maturity instant.
    ////
    //// Principal liability is extinguished and the stored payoff's difference from principal becomes CD interest.
    //// Savings receives only the representable credit; the remaining finite payoff is credited to CapOverflow and
    //// is never retried.  Ledger retains the matured contract and removes its due entry when applying this record.
    ////
    private static BankState Mature(BankState state, Certificate cd, decimal worldDays, Guid command)
    {
        var available = Money.MaximumUnits - state.Accounts[cd.Player].RustyUnits;
        var credited = Math.Min(available, cd.MaturityUnits);
        Posting[] postings = [new(LedgerKind.CdPrincipal, Currency.Rusty, cd.Player, cd.Id, cd.PrincipalUnits),
            new(LedgerKind.CdInterestExpense, Currency.Rusty, null, cd.Id, cd.MaturityUnits - cd.PrincipalUnits),
            new(LedgerKind.Customer, Currency.Rusty, cd.Player, null, -credited),
            new(LedgerKind.CapOverflow, Currency.Rusty, null, cd.Id, -(cd.MaturityUnits - credited))];
        return Ledger.Append(state, command, null, TransactionType.CdMaturity, cd.Matures, worldDays,
            ImmutableDictionary<string, string>.Empty.Add(cd.Player,
                state.Names.GetValueOrDefault(cd.Player)?.Name ?? "Account holder"), postings,
            credited != cd.MaturityUnits, contract: cd);
    }



    //// Replaces the held liquidity checkpoint after a funding-changing operation or a financial-month boundary.
    ////
    //// Observation first decays under the old target and half-life.  The exact index then supplies accrued rusty
    //// liabilities and weighted active-CD funding at this instant; an empty denominator uses ratio zero.
    //// The new target and active half-life apply only after this checkpoint, so quote frequency cannot alter prices.
    ////
    internal static BankState CheckpointLiquidity(BankState state, FinancialInstant instant, IExactLiquidityIndex index)
    {
        var observed = LiquidityAt(state.Liquidity, instant);
        var (liabilities, funding) = index.Evaluate(state, instant);
        if (liabilities < 0 || !double.IsFinite(funding) || funding < 0)
            throw new BankException(BankError.ArithmeticFault);
        var ratio = liabilities == 0 ? 0 : funding / (double)liabilities;
        var settings = state.Market.Current.Settings;
        return state with
        {
            Liquidity = new(instant, observed, LiquidityTarget(settings, ratio),
            settings.AdjustmentHalfLifeMonths)
        };
    }



    //// Reads the liquidity-spread observation at a later financial instant without changing its held target.
    //// Each half-life halves the distance from Observed to Target.  Earlier instants reject because this checkpoint
    //// cannot reconstruct a previously superseded target; callers retain history or process events chronologically.
    ////
    internal static double LiquidityAt(LiquidityState state, FinancialInstant instant)
    {
        if (instant.Months < state.Checkpoint.Months) throw new BankException(BankError.InvalidTime);
        return state.Target + (state.Observed - state.Target) *
            Math.Pow(2, -(double)(instant.Months - state.Checkpoint.Months) / state.HalfLifeMonths);
    }



    //// Maps an exact funding ratio to the target annual continuous CD spread adjustment.
    //// Shortfalls below the configured target use the scarcity boost; excess funding uses the reduction bound.
    //// The tanh response is continuous at the target and approaches each configured bound without a hard step.
    ////
    internal static double LiquidityTarget(EconomicSettings settings, double ratio)
    {
        if (!double.IsFinite(ratio) || ratio < 0) throw new BankException(BankError.ArithmeticFault);
        var difference = settings.TargetFundingRatio - ratio;
        return (difference >= 0 ? settings.MaximumScarcityBoost : settings.MaximumExcessFundingReduction) *
            Math.Tanh(difference / settings.ResponseWidth);
    }



}
