/*
 * Translates Vintage Story 1.22.7 calendar state and a process-local monotonic stopwatch into trusted core samples.
 * Calendar days include sleep acceleration; ordinary runtime conversion excludes the game's named sleeping modifier.
 * Both paths use the world's current days per month.  The stopwatch also supplies real-second token and cooldown age.
 *
 * The host checkpoints immediately before suspension and once while still suspended on resume.  That ordering lets
 * FinancialClock discard paused runtime without discarding active time.  Restore reanchors process-local seconds, so
 * shutdown time never earns interest.  Branch arrival and replacement timers must read ordinary WorldDays separately.
 * The concrete GameCalendar dependency is intentional: the public calendar interface does not expose speed modifiers.
 */

using System.Diagnostics;
using System.Linq;
using FirstGearBank.Core;
using Vintagestory.API.Server;
using Vintagestory.Common;

namespace FirstGearBank.Server;

/// Server-thread calendar adapter retaining only a monotonic process timer and the host's suspension flag.
/// It never changes the world's speed modifiers or substitutes hard-coded day/month lengths.
internal sealed class VintageStoryClock
{
    private readonly ICoreServerAPI api;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    public bool Suspended { get; set; }
    public decimal RealSeconds => (decimal)elapsed.Elapsed.TotalSeconds;



    //// Borrows the server API for the lifetime of one world; the timer starts before bank restoration reanchors it.
    ////
    public VintageStoryClock(ICoreServerAPI api)
    {
        this.api = api;
    }



    //// Samples raw clocks on the server thread and rejects unsupported calendars instead of guessing unslept speed.
    //// Runtime uses named ordinary modifiers; InGame still receives the complete calendar including sleep jumps.
    ////
    public ClockSample Sample()
    {
        var calendar = api.World.Calendar;
        if (calendar is not GameCalendar concrete || calendar.DaysPerMonth <= 0 || calendar.HoursPerDay <= 0)
            throw new BankException(BankError.InvalidTime);
        var speed = concrete.TimeSpeedModifiers.Where(pair => pair.Key != "sleeping").Sum(pair => (double)pair.Value);
        var daysPerSecond = speed * calendar.CalendarSpeedMul / (calendar.HoursPerDay * 3600.0);
        if (!double.IsFinite(daysPerSecond) || daysPerSecond < 0 || !double.IsFinite(calendar.TotalDays))
            throw new BankException(BankError.InvalidTime);
        return new((decimal)calendar.TotalDays, calendar.DaysPerMonth, RealSeconds,
            (decimal)daysPerSecond, !Suspended);
    }



}
