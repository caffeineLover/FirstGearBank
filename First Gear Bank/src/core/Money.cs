/*
 * Defines the monetary representation shared by every banking operation and journal posting.
 * One gear is exactly 1,000,000 signed integer bank units.  The type itself permits negative values for system
 * accounts and corrections; RequireBalance separately enforces the nonnegative, int.MaxValue-gear customer cap.
 * Keeping those concerns separate prevents future signed ledger categories from implicitly allowing cash overdrafts.
 *
 * Decimal input and binary floating-point rate results enter the ledger through centralized ties-to-even conversion.
 * Parse additionally enforces the caller's allowed textual precision, rejecting excess decimals rather than silently
 * rounding a requested transfer.  Display rounds only the rendered value and never changes the stored remainder.
 *
 * Compound computes a cash endpoint from an opening balance and a continuous growth exponent.  Logarithmic thresholds
 * detect cap saturation and sub-unit underflow before evaluating an unsafe exponential.  Automatic interest can
 * saturate a balance; CD quotation uses a separate rejection check before making a promised payoff.
 *
 * The shared BankError and BankException types provide identity-free domain failures for coordinator responses.
 * This file performs no journal publication, inventory conversion, logging, or persistence I/O.
 */

using System.Globalization;

namespace FirstGearBank.Core;

/// Independent ledger currencies for rusty gears and temporal gears.
/// Both use the same bank-unit scale, but their balances never offset one another and no conversion is implied.
public enum Currency { Rusty, Temporal }

/// Signed six-decimal quantity used for customer cash, contract values, and system-account postings.
/// Constructing from Units preserves the exact integer; currency and customer-balance restrictions belong to callers.
/// Monetary conversion, cash compounding, and display rounding are centralized here to keep their rules consistent.
public readonly record struct Money(long Units)
{
    public const long Scale = 1_000_000;
    public const long MaximumUnits = int.MaxValue * Scale;
    public decimal Gears => (decimal)Units / Scale;



    //// Converts a decimal number of gears into signed bank units using nearest-integer, midpoint-to-even rounding.
    //// Checked decimal scaling and integer conversion reject nonrepresentable values.  This general conversion can
    //// round extra decimal places; use Parse when a user-entered amount must reject excess textual precision.
    ////
    public static Money FromGears(decimal gears)
    {
        return new(checked((long)decimal.Round(gears * Scale, 0, MidpointRounding.ToEven)));
    }



    //// Parses a culture-independent user-entered gear amount under a caller-selected precision limit from zero to six.
    ////
    //// Outer whitespace and a leading sign are accepted.  Only ASCII digits and one decimal point are allowed;
    //// exponents, group separators, and excess entered decimal places reject before monetary conversion.
    //// Positivity and operation-specific denominations remain the business caller's responsibility.
    ////
    public static Money Parse(string text, int precision = 6)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(precision);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(precision, 6);
        var value = text.Trim();
        // Inspect written precision rather than the parsed decimal's numerical value, so even excess zeroes reject.
        var digits = value.StartsWith('-') || value.StartsWith('+') ? value[1..] : value;
        if (digits.Length == 0 || !digits.Any(char.IsAsciiDigit) ||
            digits.Any(c => !char.IsAsciiDigit(c) && c != '.') || digits.Count(c => c == '.') > 1 ||
            (digits.IndexOf('.') is var point && point >= 0 && digits.Length - point - 1 > precision) ||
            !decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var parsed))
            throw new BankException(BankError.InvalidAmount);
        return FromGears(parsed);
    }



    //// Rounds a finite floating-point result already expressed in bank units, not whole gears.
    //// Rate calculations use this boundary after exponentiation; midpoint ties go to the even integer.
    //// Nonfinite input or a value outside signed Int64 representation fails before it can enter a ledger record.
    ////
    public static Money RoundUnits(double units)
    {
        if (!double.IsFinite(units)) throw new BankException(BankError.ArithmeticFault);
        return new(checked((long)Math.Round(units, MidpointRounding.ToEven)));
    }



    //// Evaluates opening * exp(exponent) as a capped nonnegative customer balance and reports saturation metadata.
    ////
    //// Exponent is an annual continuous rate multiplied by elapsed financial years.  Zero opening cash stays zero;
    //// zero elapsed growth preserves its exact units.  Log thresholds identify values beyond the cap or below half
    //// a bank unit before exponentiation, and the remaining finite result uses centralized ties-to-even rounding.
    //// The returned cap flag is audit metadata, not an amount owed or a deferred interest entitlement.
    ////
    public static (Money Value, bool WasCapped) Compound(Money opening, double exponent)
    {
        RequireBalance(opening);
        if (!double.IsFinite(exponent)) throw new BankException(BankError.ArithmeticFault);
        if (opening.Units == 0) return (opening, false);
        if (exponent == 0) return (opening, false);
        // Compare in log space first; constructing an above-cap mathematical balance could overflow needlessly.
        var upper = Math.Log((double)MaximumUnits / opening.Units);
        if (exponent >= upper) return (new(MaximumUnits), true);
        if (exponent < Math.Log(0.5 / opening.Units)) return (new(0), false);
        var units = RoundUnits(opening.Units * Math.Exp(exponent));
        return (new(Math.Min(MaximumUnits, units.Units)), units.Units >= MaximumUnits);
    }



    //// Enforces the cash-account range on an exact bank-unit value after projection or before compounding.
    //// Negative cash reports insufficient funds, while excess positive cash reports the customer balance ceiling.
    //// Signed system accounts must not use this customer-specific validator.
    ////
    public static void RequireBalance(Money value)
    {
        if (value.Units < 0) throw new BankException(BankError.InsufficientFunds);
        if (value.Units > MaximumUnits) throw new BankException(BankError.BalanceCap);
    }



    //// Renders gears at a selected display precision using invariant culture and midpoint-to-even rounding.
    //// Fixed precision supports rusty account displays; trimmed precision supports temporal fractional balances.
    //// The stored six-decimal amount remains unchanged, so callers must not interpret this string as a spendable
    //// total.
    ////
    public string Display(int precision = 3, bool trimZeroes = false)
    {
        if (precision is < 0 or > 6) throw new ArgumentOutOfRangeException(nameof(precision));
        var rounded = decimal.Round(Gears, precision, MidpointRounding.ToEven);
        return rounded.ToString(trimZeroes ? "0" + (precision == 0 ? "" : "." + new string('#', precision))
            : "F" + precision, CultureInfo.InvariantCulture);
    }



}

/// Stable domain status codes used by command results, validation failures, and startup quarantine outcomes.
/// Codes distinguish recoverable business rejection from unavailable authority without embedding names, balances, or
/// UIDs.
public enum BankError
{
    None, InvalidAmount, InvalidCurrency, InsufficientFunds, BalanceCap, ArithmeticFault, InvalidTime,
    NoAccount, UnknownRecipient, AmbiguousRecipient, RecipientServiceUnavailable, SelfTransfer,
    InvalidSession, InvalidSequence, PayloadMismatch, AlreadyProcessedResponseExpired, ExpiredConfirmation,
    Cooldown, InvalidDenomination, InventoryUnavailable, SettlementQuarantined, PermissionDenied,
    InvalidConfiguration, CorruptState, LiquidityIndexUnavailable, PrintAllowanceUsed
}

/// Carries an identity-free banking failure through candidate construction to the coordinator's terminal-result
/// boundary.
/// The supplied code is also the exception message; callers retain recovery evidence separately from ordinary errors.
/// Throwing this exception does not itself publish, roll back inventory, or change quarantine state.
public sealed class BankException(BankError error) : Exception(error.ToString())
{
    public BankError Error { get; } = error;
}
