/*
 * Provides the explicit scan-based implementation of the CD liquidity projection contract.
 * At each requested checkpoint it projects every cash account's rusty balance using its own stored checkpoint and
 * monthly rounding, then adds the original principal and remaining-tenor funding contribution of every active CD.
 * Temporal cash is excluded.  The supplied immutable bank state is never materialized or changed by this calculation.
 *
 * Correctness of the monetary denominator requires per-account rounding: projecting one summed opening balance is
 * not equivalent.  This implementation preserves that rule through FinanceEngine.ProjectRusty, but its work grows
 * with account count, pending account-month intervals, and retained certificate count.  It provides no sublinear index.
 *
 * BankingCoordinator requires an IExactLiquidityIndex dependency and does not select this implementation implicitly.
 * A host must explicitly accept the specification's performance exception before choosing it.  FinanceEngine uses
 * the dependency at funding checkpoints, not during a mere quote; due maturities must be processed chronologically.
 */

namespace FirstGearBank.Core;

/// Stateless, exact account-by-account liquidity projection with explicit linear scan cost.
/// It is a selectable fallback behind IExactLiquidityIndex, not a claimed solution to the spec's no-account-scan gate.
/// All values are calculated from the supplied immutable candidate rather than from mutable global cached totals.
public sealed class ScanningLiquidityIndex : IExactLiquidityIndex
{



    //// Returns the accrued rusty liability denominator and remaining-tenor-weighted active CD funding at an instant.
    ////
    //// Each account is projected separately with monthly rounding.  Active principal contributes once to liabilities
    //// and contributes principal * remainingMonths / 12 to funding; matured contracts contribute neither here.
    //// An overdue active contract rejects because it needs chronological settlement before this projection is usable.
    //// Checked decimal totals and finite funding checks reject arithmetic failure without modifying any bank state.
    ////
    public (decimal LiabilityUnits, double WeightedFundingUnits) Evaluate(BankState state, FinancialInstant instant)
    {
        instant.Validate();
        decimal liabilities = 0;
        double funding = 0;
        // Independent rounding is the economic requirement that prevents replacing this loop with aggregate rounding.
        foreach (var account in state.Accounts.Values)
            liabilities = checked(liabilities + FinanceEngine.ProjectRusty(state, account, instant));
        foreach (var cd in state.Certificates.Values)
        {
            if (cd.Matured) continue;
            if (cd.Matures.Months < instant.Months) throw new BankException(BankError.InvalidTime);
            liabilities = checked(liabilities + cd.PrincipalUnits);
            funding += cd.PrincipalUnits * (double)((cd.Matures.Months - instant.Months) / 12m);
        }
        if (!double.IsFinite(funding)) throw new BankException(BankError.ArithmeticFault);
        return (liabilities, funding);
    }



}
