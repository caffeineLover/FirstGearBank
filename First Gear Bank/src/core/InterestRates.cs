/*
 * Defines the bank's interest-rate models, economic parameters, and monthly rate history.
 * This is the numerical layer used by cash accrual and certificate-of-deposit pricing; it does not move money,
 * advance the game clock, or publish journal records.
 *
 * EconomicSettings converts the configured effective annual return into the continuously compounded target theta.
 * A target of 3.0 means 300 percent annual interest, giving theta = ln(4).  Constant mode holds the short rate at
 * theta.  CIR mode evolves it once per financial month using the Cox-Ingersoll-Ross transition distribution.
 * Annual continuous rates and durations in financial years are doubles; monetary conversion remains owned by Money.
 *
 * MarketHistory records each realized rate, the applicable parameters, and the month's shared CD spread shocks.
 * FinanceEngine requests new months after closing-interval events have been processed.  Cash accrual reads those
 * retained months, while CD quotation uses the published rate and curve without drawing additional randomness.
 * Persistence restores recorded history rather than recreating it from the seed.
 *
 * DomainRandom supplies independent, versioned streams for CIR transitions and each spread shock.  Its distribution
 * transforms are kept here because their draw order is part of reproducible financial behavior.  Changing a sampler,
 * domain label, or encoding requires an explicit versioning decision.  Invalid numerical results throw before the
 * coordinator can publish the candidate state; this file provides no configuration-file fallback or game integration.
 */

using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FirstGearBank.Core;

/// Selects the short-rate evolution rule.  CIR is stochastic; Constant follows the configured continuous target.
/// The selected rule also determines whether CD risk-free yields use the CIR curve or a flat term structure.
public enum RateModel { CIR, Constant }

/// Immutable economic settings retained with each financial month and validated before activation.
/// Rates and spreads are annual continuous decimals except TargetAnnualizedRiskFreeRate, which is an effective return.
/// CD principal is stored in bank units, tenors in whole financial months, and liquidity half-life in financial months.
/// Changes apply to future monthly state; existing CD contracts retain the price snapshot captured at issuance.
public sealed record EconomicSettings
{
    // The effective annual target is the single source for initial, long-run, and Constant-mode continuous rates.
    public RateModel Model { get; init; } = RateModel.CIR;
    public double TargetAnnualizedRiskFreeRate { get; init; } = 3;
    // CIR mean reversion is measured per financial year.  Volatility is the square-root diffusion coefficient.
    public double MeanReversionSpeed { get; init; } = 1;
    public double Volatility { get; init; } = 0.12;
    // CD eligibility is separate from pricing: eligible principals retain all six ledger decimals.
    public long MinimumCdPrincipalUnits { get; init; } = 250_000;
    public ImmutableArray<int> TenorsMonths { get; init; } = [1, 3, 6, 12];
    // Base spread is Scale * tenor^Exponent.  Shock variations are bounded amplitudes, not statistical variances.
    public double BaseSpreadScale { get; init; } = 0.30;
    public double BaseSpreadExponent { get; init; } = 0.395;
    public double LevelVariation { get; init; } = 0.05;
    public double SlopeVariation { get; init; } = 0.04;
    // The funding ratio selects a bounded target adjustment; the observed adjustment approaches it over time.
    public double TargetFundingRatio { get; init; } = 0.25;
    public double MaximumScarcityBoost { get; init; } = 0.15;
    public double MaximumExcessFundingReduction { get; init; } = 0.10;
    public double AdjustmentHalfLifeMonths { get; init; } = 1;
    public double ResponseWidth { get; init; } = 0.10;
    // log1p preserves small effective returns that ordinary log(1 + q) can lose to floating-point cancellation.
    public double Theta => double.LogP1(TargetAnnualizedRiskFreeRate);



    //// Checks settings before initial creation, queuing, or entry into a new financial month.
    ////
    //// Field checks reject invalid domains first.  Each tenor is then priced at both the target and carried short
    //// rate,
    //// including the extreme permitted shocks and liquidity spreads.  This rejects combinations whose minimum CD
    //// principal would produce a nonrepresentable payoff.  The caller owns retention of the previous configuration;
    //// this method neither defaults individual values nor mutates active financial state.
    ////
    public void Validate(double? carriedRate = null)
    {
        // Reject simple domain violations before evaluating nonlinear curves or compound payoffs.
        double[] nonnegative = [TargetAnnualizedRiskFreeRate, LevelVariation, SlopeVariation,
            BaseSpreadScale, MaximumScarcityBoost, MaximumExcessFundingReduction];
        double[] positive = [MeanReversionSpeed, Volatility, AdjustmentHalfLifeMonths, ResponseWidth];
        if (!Enum.IsDefined(Model) || nonnegative.Any(x => !double.IsFinite(x) || x < 0) ||
            positive.Any(x => !double.IsFinite(x) || x <= 0) || !double.IsFinite(Theta) ||
            !double.IsFinite(TargetFundingRatio) || TargetFundingRatio is < 0 or > 1 ||
            !double.IsFinite(BaseSpreadExponent) || BaseSpreadExponent is <= 0 or > 1 ||
            MinimumCdPrincipalUnits is <= 0 or > Money.MaximumUnits || TenorsMonths.IsDefaultOrEmpty ||
            TenorsMonths.Any(t => t <= 0) || TenorsMonths.Distinct().Count() != TenorsMonths.Length)
            throw new BankException(BankError.InvalidConfiguration);
        // A valid field in isolation may still combine with another field to produce an impossible CD contract.
        foreach (var rate in new[] { Theta, carriedRate ?? Theta })
            foreach (var tenor in TenorsMonths)
                foreach (var sign in new[] { -1, 1 })
                {
                    var yield = RateMath.Yield(this, rate, tenor / 12.0) + BaseSpreadScale * Math.Pow(tenor,
                        BaseSpreadExponent) + sign * (LevelVariation + SlopeVariation * Math.Log(tenor) / Math.Log(12)) +
                        (sign > 0 ? MaximumScarcityBoost : -MaximumExcessFundingReduction);
                    RateMath.Payoff(MinimumCdPrincipalUnits, yield * (tenor / 12.0));
                }
    }



}

/// Persisted rate and CD-spread inputs for the half-open interval [Serial, Serial + 1) in financial months.
/// Rate and Theta are annual continuous rates; the level and slope shocks are shared by every quote in that month.
/// Keeping the settings snapshot allows overdue accounts to accrue under the parameters effective at the time.
public sealed record MarketMonth(long Serial, double Rate, double Theta, double LevelShock,
    double SlopeShock, EconomicSettings Settings, long ConfigurationRevision);

/// Immutable chronological authority for realized monthly interest rates and CD spread shocks.
/// The seed determines future domain streams, but persisted monthly records take precedence on reload.
/// New histories are returned to the coordinator as candidates; these methods do not publish or save them.
public sealed record MarketHistory(long Seed, ImmutableSortedDictionary<long, MarketMonth> Months)
{
    public MarketMonth Current => Months.Last().Value;



    //// Builds month zero for a confirmed new bank after validating its economic settings.
    ////
    //// Both models start at theta.  Initial CIR creation does not simulate a preceding month, but the initial level
    //// and slope shocks are drawn immediately so the first quote has complete pricing inputs.  Loading an existing
    //// bank must restore its saved history instead of calling this method and replacing realized financial state.
    ////
    public static MarketHistory Create(long seed, EconomicSettings settings)
    {
        settings.Validate();
        return new(seed, ImmutableSortedDictionary<long, MarketMonth>.Empty.Add(0,
            MakeMonth(seed, 0, settings.Theta, settings, 0)));
    }



    //// Returns the next monthly history after FinanceEngine has processed events belonging to the closing interval.
    ////
    //// An activated configuration replaces the prior settings for this transition.  Switching into CIR carries the
    //// closing realized rate into one new draw; switching into Constant sets theta and consumes no CIR draw.
    //// The method appends a new record without altering earlier months, preserving inputs needed for lazy accrual.
    ////
    public MarketHistory EnterMonth(EconomicSettings? activated = null)
    {
        var closing = Current;
        var settings = activated ?? closing.Settings;
        settings.Validate(closing.Rate);
        var serial = checked(closing.Serial + 1);
        // Use the closing rate for both CIR-to-CIR and Constant-to-CIR transitions; no dormant rate is resurrected.
        var rate = settings.Model == RateModel.Constant ? settings.Theta :
            RateMath.Transition(settings, closing.Rate, new DomainRandom(Seed, serial, "CIR/v1"));
        return this with
        {
            Months = Months.Add(serial, MakeMonth(Seed, serial, rate, settings,
            checked(closing.ConfigurationRevision + (activated is null ? 0 : 1))))
        };
    }



    //// Captures the complete pricing inputs for one month after its short rate has been selected.
    ////
    //// Separate level and slope domains prevent rejection sampling in CIR, or changes in another shock's draw count,
    //// from shifting this month's spread values.  Each result is global to the bank and stored with its parameters.
    ////
    private static MarketMonth MakeMonth(long seed, long serial, double rate, EconomicSettings settings, long revision)
    {
        return new(serial, rate, settings.Theta,
            new DomainRandom(seed, serial, "CD/level/v1").Shock(settings.LevelVariation),
            new DomainRandom(seed, serial, "CD/slope/v1").Shock(settings.SlopeVariation), settings, revision);
    }



}

/// Deterministic random stream owned by one financial feature for one world seed and month.
/// HMAC counter expansion provides reproducible uniforms without shared process state; the key is seed material,
/// not a secret authentication credential.  Instances are local to monthly generation and are not shared concurrently.
/// Distribution routines reject invalid parameters and never use the engine's mutable global random generator.
internal sealed class DomainRandom
{
    private readonly byte[] key;
    private ulong counter;



    //// Initializes a fresh stream for a versioned domain, world seed, and financial month.
    ////
    //// Fixed-width little-endian seed/month fields and a length-prefixed UTF-8 label avoid ambiguous concatenations.
    //// The counter begins at zero and is private to this instance; another feature's sampler cannot consume its draws.
    ////
    public DomainRandom(long seed, long month, string domain)
    {
        var label = Encoding.UTF8.GetBytes(domain);
        key = new byte[20 + label.Length];
        BinaryPrimitives.WriteInt64LittleEndian(key, seed);
        BinaryPrimitives.WriteInt64LittleEndian(key.AsSpan(8), month);
        BinaryPrimitives.WriteInt32LittleEndian(key.AsSpan(16), label.Length);
        label.CopyTo(key, 20);
    }



    //// Expands the next counter into a uniform strictly between zero and one.
    ////
    //// A 52-bit integer plus a half-step excludes both endpoints, making logarithmic transforms safe from log(0).
    //// The byte encoding and selected digest bits are reproducibility contracts, not interchangeable implementation
    //// details.  Counter exhaustion throws through checked arithmetic rather than repeating the stream.
    ////
    public double Uniform()
    {
        Span<byte> input = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(input, counter++);
        var digest = HMACSHA256.HashData(key, input);
        var bits = BinaryPrimitives.ReadUInt64LittleEndian(digest) >> 12;
        return (bits + 0.5) / 4503599627370496.0;
    }



    //// Produces one bounded spread shock with the specification's Vintage Story NatFloat Gaussian semantics.
    ////
    //// The average of three uniforms is centered at one half and scaled to the requested support.  Variation is the
    //// maximum absolute deviation, not variance or standard deviation; replacing this with Normal changes pricing.
    ////
    public double Shock(double variation)
    {
        return 2 * variation * ((Uniform() + Uniform() + Uniform()) / 3 - 0.5);
    }



    //// Produces a standard normal variate with the Box-Muller transform for gamma and CIR sampling.
    ////
    //// Both uniforms come from this feature's stream.  The companion normal is deliberately not cached, keeping the
    //// stream's consumption explicit and independent of an extra mutable spare-value state.
    ////
    public double Normal()
    {
        return Math.Sqrt(-2 * Math.Log(Uniform())) * Math.Cos(2 * Math.PI * Uniform());
    }



    //// Samples a gamma variate with the supplied positive shape and unit scale.
    ////
    //// Shapes below one use gamma(shape + 1) * uniform^(1 / shape).  Larger shapes use Marsaglia-Tsang rejection;
    //// invalid cube proposals are discarded and a cheap acceptance check precedes the logarithmic check.
    //// CIR handles the zero-shape point mass separately, so zero must never be passed to this routine.
    ////
    public double Gamma(double shape)
    {
        if (!double.IsFinite(shape) || shape <= 0) throw new BankException(BankError.ArithmeticFault);
        // Shape augmentation extends the rejection algorithm to the positive interval below one.
        if (shape < 1) return Gamma(shape + 1) * Math.Pow(Uniform(), 1 / shape);
        var d = shape - 1.0 / 3;
        var c = 1 / Math.Sqrt(9 * d);
        while (true)
        {
            var x = Normal();
            var v = 1 + c * x;
            // The transformed proposal must be positive before cubing and evaluating its log density.
            if (v <= 0) continue;
            v = v * v * v;
            var u = Uniform();
            if (u < 1 - 0.0331 * x * x * x * x ||
                Math.Log(u) < 0.5 * x * x + d * (1 - v + Math.Log(v))) return d * v;
        }
    }



    //// Samples the Poisson count used in the low-degree CIR noncentral-chi-square mixture.
    ////
    //// Small means count exponential arrivals, where work is inexpensive.  Large means use transformed rejection
    //// instead of work proportional to the expected count.  Its fast acceptance region and logarithmic density check
    //// share the same proposal distribution; returned counts must fit the signed integer representation.
    ////
    public long Poisson(double mean)
    {
        if (!double.IsFinite(mean) || mean < 0 || mean >= long.MaxValue)
            throw new BankException(BankError.ArithmeticFault);
        // Summed unit-rate exponential arrival times give a Poisson count in an interval of length mean.
        if (mean < 10)
        {
            var sum = 0.0;
            long count = 0;
            while ((sum -= Math.Log(Uniform())) <= mean) count++;
            return count;
        }
        // Transformed-rejection envelope constants depend on the mean and are reused across rejected proposals.
        var b = 0.931 + 2.53 * Math.Sqrt(mean);
        var a = -0.059 + 0.02483 * b;
        var inverseAlpha = 1.1239 + 1.1328 / (b - 3.4);
        var fastRegion = 0.9277 - 3.6224 / (b - 2);
        while (true)
        {
            var u = Uniform() - 0.5;
            var v = Uniform();
            var us = 0.5 - Math.Abs(u);
            var proposal = Math.Floor((2 * a / us + b) * u + mean + 0.43);
            if (proposal < 0 || proposal >= long.MaxValue || (us < 0.013 && v > us)) continue;
            if (us >= 0.07 && v <= fastRegion) return checked((long)proposal);
            // Compare log probabilities to avoid constructing a factorial or an underflowing Poisson mass.
            var logProbability = -mean + proposal * Math.Log(mean) - LogGamma(proposal + 1);
            if (Math.Log(v * inverseAlpha / (a / (us * us) + b)) <= logProbability)
                return checked((long)proposal);
        }
    }



    //// Evaluates log(Gamma(value)) for the large-mean Poisson acceptance calculation.
    ////
    //// This private Lanczos approximation receives proposal + 1, so its input is at least one and no reflection
    //// branch is needed.  The logarithmic result avoids forming the factorial of a potentially large proposal.
    ////
    private static double LogGamma(double value)
    {
        ReadOnlySpan<double> coefficients = [676.5203681218851, -1259.1392167224028, 771.32342877765313,
            -176.61502916214059, 12.507343278686905, -0.13857109526572012,
            9.9843695780195716e-6, 1.5056327351493116e-7];
        var z = value - 1;
        var x = 0.99999999999980993;
        for (var i = 0; i < coefficients.Length; i++) x += coefficients[i] / (z + i + 1);
        var t = z + 7.5;
        return 0.9189385332046727 + (z + 0.5) * Math.Log(t) - t + Math.Log(x);
    }



}

/// Numerical interest-rate operations used by monthly CIR evolution, economic validation, and CD quotation.
/// Durations are financial years and rates are annual continuous decimals.  Transition is the only stochastic
/// operation; curve and payoff functions are pure calculations with no clock, journal, or account side effects.
public static class RateMath
{



    //// Draws the next monthly CIR short rate from the noncentral-chi-square transition law.
    ////
    //// The step is exactly 1/12 financial year.  Here k is mean reversion, variance is sigma squared, c is the
    //// transition scale, df is the degrees of freedom, and lambda is noncentrality.  Degrees above one use a shifted
    //// normal square plus a gamma draw; smaller degrees use the Poisson/gamma mixture, including its zero point mass.
    //// This is distribution sampling with floating-point arithmetic, not an Euler step followed by a nonnegative
    //// clamp.
    ////
    internal static double Transition(EconomicSettings settings, double rate, DomainRandom random)
    {
        if (!double.IsFinite(rate) || rate < 0) throw new BankException(BankError.ArithmeticFault);
        var k = settings.MeanReversionSpeed;
        var variance = settings.Volatility * settings.Volatility;
        // expm1 retains precision when monthly mean reversion is small and exp(-k / 12) is close to one.
        var oneMinusE = -double.ExpM1(-k / 12);
        var c = variance * oneMinusE / (4 * k);
        var df = 4 * k * settings.Theta / variance;
        var lambda = Math.Exp(-k / 12) * rate / c;
        if (!double.IsFinite(c) || c <= 0 || !double.IsFinite(df) || !double.IsFinite(lambda))
            throw new BankException(BankError.ArithmeticFault);
        double sample;
        // The decomposition moves noncentrality into one normal draw and avoids a potentially large Poisson count.
        if (df > 1)
        {
            var normal = random.Normal() + Math.Sqrt(lambda);
            sample = normal * normal + 2 * random.Gamma((df - 1) / 2);
        }
        else
        {
            var count = random.Poisson(lambda / 2);
            var shape = df / 2 + count;
            // With theta = 0, the mixture includes an exact point mass at zero; gamma has no zero-shape call.
            sample = shape == 0 ? 0 : 2 * random.Gamma(shape);
        }
        var next = c * sample;
        if (!double.IsFinite(next) || next < 0) throw new BankException(BankError.ArithmeticFault);
        return next;
    }



    //// Returns the continuously compounded annual risk-free yield for a nonnegative financial-year tenor.
    ////
    //// Constant mode and the zero-tenor limit return the supplied short rate.  CIR uses the same kappa, theta,
    //// and sigma as realized evolution, with zero market price of rate risk.  The denominator is scaled by
    //// exp(-gamma * years), and log(A) is retained directly, avoiding the original formula's large positive
    //// exponential.
    ////
    public static double Yield(EconomicSettings settings, double rate, double years)
    {
        if (!double.IsFinite(years) || years < 0 || !double.IsFinite(rate) || rate < 0)
            throw new BankException(BankError.ArithmeticFault);
        if (years == 0 || settings.Model == RateModel.Constant) return rate;
        var k = settings.MeanReversionSpeed;
        var variance = settings.Volatility * settings.Volatility;
        var gamma = Math.Sqrt(k * k + 2 * variance);
        var z = gamma * years;
        var e = Math.Exp(-z);
        var oneMinusE = -double.ExpM1(-z);
        // This denominator is the standard CIR denominator multiplied by exp(-gamma * years).
        var denominator = (gamma + k) * oneMinusE + 2 * gamma * e;
        var b = 2 * oneMinusE / denominator;
        var logA = 2 * k * settings.Theta / variance *
            (Math.Log(2 * gamma / denominator) + (k - gamma) * years / 2);
        var result = (b * rate - logA) / years;
        if (!double.IsFinite(result)) throw new BankException(BankError.ArithmeticFault);
        return result;
    }



    //// Converts the curve yield into the price of one unit payable after the supplied financial-year tenor.
    ////
    //// At zero tenor, multiplying by zero gives the identity discount of one.  Positive-tenor discounts must remain
    //// finite and strictly positive; underflow is rejected rather than supplied to later logarithmic pricing steps.
    ////
    public static double Discount(EconomicSettings settings, double rate, double years)
    {
        var value = Math.Exp(-Yield(settings, rate, years) * years);
        if (!double.IsFinite(value) || value <= 0) throw new BankException(BankError.ArithmeticFault);
        return value;
    }



    //// Computes a CD's stored maturity units from principal units and yield multiplied by financial years.
    ////
    //// Unlike automatic cash interest, a newly quoted promise cannot be silently saturated to the balance cap.
    //// A logarithmic threshold rejects an above-cap payoff before exponentiation.  Representable payoffs use Money's
    //// centralized ties-to-even rounding; negative yields may legitimately produce less than the original principal.
    ////
    public static long Payoff(long principal, double exponent)
    {
        if (principal <= 0 || principal > Money.MaximumUnits || !double.IsFinite(exponent))
            throw new BankException(BankError.InvalidAmount);
        if (exponent > Math.Log((double)Money.MaximumUnits / principal))
            throw new BankException(BankError.BalanceCap);
        return Money.Compound(new(principal), exponent).Value.Units;
    }



}
