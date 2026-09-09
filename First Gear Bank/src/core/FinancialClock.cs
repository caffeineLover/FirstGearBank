/*
 * Converts adapter-supplied calendar/runtime samples into the bank's monotonic financial timeline.
 * Financial position is measured in decimal months, with twelve months per financial year.  CD maturities store this
 * position instead of a mutable world-calendar date, so a raw calendar edit or clock-basis switch cannot move a
 * contract.
 *
 * InGame follows positive world-calendar movement, including sleep.  ServerRuntime follows positive monotonic runtime
 * while simulation is active, converted using the adapter's ordinary unslept game speed and current days per month.
 * Neither mode uses civil wall-clock time or assumes a fixed number of real seconds per game day.
 *
 * A backward source edit contributes no negative duration and immediately resets its anchor.  Reanchor is used when
 * a world starts or reloads to discard shutdown time and stale process clock values while preserving financial time.
 * Switch first advances the old basis, then selects the new basis without retroactive growth.
 *
 * These records are immutable calculations only.  FinanceEngine processes the resulting due events and the coordinator
 * publishes them; verifying Vintage Story calendar speed, pause state, and sample authenticity belongs to the host.
 */

namespace FirstGearBank.Core;

/// Chooses the source of financial elapsed time: world-calendar progression or active server simulation runtime.
/// This selection affects banking time only; branch timers and real-time conversation deadlines remain host concerns.
public enum InterestTimeBasis { InGame, ServerRuntime }

/// Nonnegative cumulative financial position stored as decimal months, including a fractional current month.
/// Month returns the completed whole-month serial used to select the active rate interval.
/// Explicit Validate calls enforce the supported range before time enters authoritative state.
public readonly record struct FinancialInstant(decimal Months)
{
    public long Month => checked((long)decimal.Floor(Months));



    //// Rejects negative financial positions and values whose whole-month serial exceeds signed Int64 range.
    //// Decimal fractions remain intact; this method does not round a contractual instant to a calendar boundary.
    ////
    public void Validate()
    {
        if (Months < 0 || Months > long.MaxValue) throw new BankException(BankError.InvalidTime);
    }



}

/// One trusted adapter reading of world days, calendar month length, and current-process monotonic runtime seconds.
/// OrdinaryGameDaysPerSecond excludes sleep acceleration.  SimulationRunning tells runtime accrual whether the sampled
/// interval is active; the host must sample/reanchor appropriately at pause transitions to avoid counting paused time.
public sealed record ClockSample(decimal WorldDays, decimal DaysPerMonth, decimal RuntimeSeconds,
    decimal OrdinaryGameDaysPerSecond, bool SimulationRunning);

/// Immutable association between accumulated financial months and the latest raw calendar/runtime anchors.
/// Advance returns a candidate clock without publishing state or processing interest; FinanceEngine supplies that work.
/// Restored instances must be reanchored to the new process before any elapsed-runtime comparison is made.
public sealed record FinancialClock(InterestTimeBasis Basis, FinancialInstant Position,
    decimal WorldAnchor, decimal RuntimeAnchor)
{



    //// Replaces both raw anchors with a validated host sample while preserving accumulated financial position.
    //// Used on creation and restart, this prevents shutdown duration or a new process's monotonic origin from being
    //// interpreted as earned interest.  It does not catch up calendar edits that occurred before the anchor was reset.
    ////
    public FinancialClock Reanchor(ClockSample sample)
    {
        Validate(sample);
        return this with { WorldAnchor = sample.WorldDays, RuntimeAnchor = sample.RuntimeSeconds };
    }



    //// Converts elapsed source movement into additional financial months and returns new anchors with the result.
    ////
    //// InGame divides positive world-day movement by current month length.  ServerRuntime counts active runtime at
    //// ordinary unslept speed; paused samples contribute zero.  Both raw anchors update even when the selected source
    //// moved backward, so later progress starts immediately rather than waiting to regain the old source value.
    ////
    public FinancialClock Advance(ClockSample sample)
    {
        Validate(sample);
        // Source deltas are clipped, not the accumulated financial position; established contract time never reverses.
        var elapsed = Basis == InterestTimeBasis.InGame
            ? Math.Max(0, sample.WorldDays - WorldAnchor) / sample.DaysPerMonth
            : sample.SimulationRunning
                ? Math.Max(0, sample.RuntimeSeconds - RuntimeAnchor) * sample.OrdinaryGameDaysPerSecond /
                    sample.DaysPerMonth : 0;
        var position = new FinancialInstant(Position.Months + elapsed);
        position.Validate();
        return new(Basis, position, sample.WorldDays, sample.RuntimeSeconds);
    }



    //// Advances to the supplied sample under the old basis before installing the new source selection.
    //// Both modes therefore begin from the same financial position and raw anchors; switching cannot replay an
    //// interval
    //// or reinterpret an existing CD maturity.  The caller publishes this returned clock with its configuration
    //// change.
    ////
    public FinancialClock Switch(InterestTimeBasis basis, ClockSample sample)
    {
        if (!Enum.IsDefined(basis)) throw new BankException(BankError.InvalidConfiguration);
        return Advance(sample) with { Basis = basis };
    }



    //// Checks the month-length, runtime-origin, and ordinary-speed domains before time conversion.
    //// Month length must be positive; runtime seconds and ordinary speed cannot be negative.  The host remains
    //// responsible for supplying genuine engine readings and correctly identifying active simulation intervals.
    ////
    private static void Validate(ClockSample sample)
    {
        if (sample.DaysPerMonth <= 0 || sample.RuntimeSeconds < 0 || sample.OrdinaryGameDaysPerSecond < 0)
            throw new BankException(BankError.InvalidTime);
    }



}
