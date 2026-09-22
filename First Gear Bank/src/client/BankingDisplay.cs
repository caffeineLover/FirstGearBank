/*
 * Formats player-safe server snapshots as localized ledger text for BankingDialog and bank notifications.
 * This presentation-only boundary borrows immutable core view types, not the coordinator or saved account records.
 * Amount inputs are never parsed here: exact bank units divide by 1,000,000 only for display.  Rounded cash labels
 * explicitly retain the underlying six-decimal balance; exact confirmations and contractual payoffs are not rounded.
 *
 * Rates arrive as annual continuous or effective decimals and are labeled separately.  Contract timestamps remain
 * financial-month instants with their server-selected basis; remaining duration is informational, never a redemption
 * promise.  Each method formats a bounded page, and player names are stripped of markup/control characters before
 * reaching chat or widgets.  Localization assets own prose; this file owns no UI widgets, transport, or persistence.
 */

using System;
using System.Globalization;
using System.Linq;
using FirstGearBank.Core;
using FirstGearBank.Server;
using Vintagestory.API.Config;
using Vintagestory.API.Common;

namespace FirstGearBank.Client;

/// Pure display helpers shared by the ledger and notifications; none of their results authorizes a transaction.
internal static class BankingDisplay
{



    //// Resolves mod-local text with server-derived values passed as substitutions rather than localization keys.
    ////
    internal static string Text(string key, params object[] values)
    {
        return Lang.Get("firstgearbank:bank-" + key, values);
    }



    //// Removes markup delimiters and control characters from external names before plain text or chat rendering.
    //// Length is bounded to keep an unusual display name from expanding a row into an unbounded surface.
    ////
    internal static string Safe(string value)
    {
        return new string(value.Where(c => !char.IsControl(c) && c is not '<' and not '>' and not '&').Take(120).ToArray());
    }



    //// Shows every meaningful ledger decimal, including aggregate totals that exceed a single account's balance cap.
    ////
    internal static string Exact(decimal units)
    {
        return (units / Money.Scale).ToString("0.######", CultureInfo.InvariantCulture);
    }



    //// Converts a supplied fractional rate to percentage text without deriving or predicting another financial rate.
    ////
    private static string Percent(double rate)
    {
        return (rate * 100).ToString("0.####", CultureInfo.InvariantCulture) + "%";
    }



    //// Distinguishes known business failures while hiding unknown transport/server status strings behind safe wording.
    //// No arbitrary exception text, raw IDs, or unrecognized server content is interpolated into an error message.
    ////
    internal static string Error(string status)
    {
        var known = Enum.TryParse<BankError>(status, out var error) && Enum.IsDefined(error);
        return Text("error-" + (known || status is "InvalidRequest" or "ServiceUnavailable" or "Suspended" or
            "ResponseTooLarge" or "Starting" ? status : "ServiceUnavailable"));
    }



    //// Renders current cash, exact retained remainders, basis, and distinctly labeled floating-rate measures.
    //// Absence of a statement is not fabricated as an existing zero-balance account.
    ////
    internal static string Account(BankingStatementPage? page, int precision)
    {
        if (page is null) return Text("no-statement");
        var view = page.Statement;
        return Text("holder", Safe(view.HolderName)) + "\n\n" +
            Text("balance", Text("Rusty"), new Money(view.RustyUnits).Display(precision), Exact(view.RustyUnits)) + "\n" +
            Text("balance", Text("Temporal"), Exact(view.TemporalUnits), Exact(view.TemporalUnits)) + "\n\n" +
            Text("rate-continuous", Percent(view.ContinuousRate)) + "\n" +
            Text("rate-monthly", Percent(view.MonthlyEffectiveRate)) + "\n" +
            Text("rate-annual", Percent(view.AnnualizedEffectiveYield)) + "\n\n" +
            Text("as-of", Text(view.TimeBasis.ToString()), view.AsOf.Months.ToString("0.######", CultureInfo.InvariantCulture)) +
            "\n" + CalendarDate(page, page.WorldCalendarDays) +
            "\n\n" + Text("rounding");
    }



    //// Builds one short history page from the server's directional journal views, never from predicted local deltas.
    //// A capped row remains visible and names are transaction-time display snapshots rather than account identifiers.
    ////
    internal static string History(BankingStatementPage? page)
    {
        if (page is null) return Text("no-statement");
        return string.Join("\n\n", page.Statement.History.Select((row, index) =>
            CalendarDate(page, page.HistoryCalendarDays[index]) + " — " +
            Text("history-row", row.EffectiveTime.Months.ToString("0.###", CultureInfo.InvariantCulture),
                Text(row.Type), Text(row.Currency.ToString()), Exact(row.ChangeUnits)) + "\n" +
            string.Join(", ", row.Names.Select(Safe)) + (row.WasCapped ? " — " + Text("capped") : "")));
    }



    //// Displays exact cumulative totals independently of the server's history/CD pagination offsets.
    //// Category values are already computed by the server; sign and currency are retained unchanged.
    ////
    internal static string Totals(BankingStatementPage? page, int offset)
    {
        if (page is null) return Text("no-statement");
        return string.Join("\n\n", page.Statement.Totals.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Skip(offset).Take(6).Select(pair => string.Join(" — ", pair.Key.Split(':').Select(TextKey)) +
                ": " + Exact(pair.Value)));
    }



    //// Adapts one known cumulative category to the localization helper without treating any player input as a key.
    ////
    private static string TextKey(string key)
    {
        return Text(key);
    }



    //// Shows each active contract's locked endpoints, annual continuous yield, and remaining financial months.
    //// Current value is labeled nonspendable and no early redemption control is offered.
    ////
    internal static string Certificates(BankingStatementPage? page)
    {
        if (page is null) return Text("no-statement");
        var view = page.Statement;
        return string.Join("\n\n", view.Certificates.Select(cd =>
            Text("cd-values", Exact(cd.PrincipalUnits), Exact(cd.CurrentValueUnits), Exact(cd.MaturityUnits)) + "\n" +
            Text("cd-term", cd.Issued.Months.ToString("0.###", CultureInfo.InvariantCulture),
                cd.Matures.Months.ToString("0.###", CultureInfo.InvariantCulture),
                Math.Max(0, cd.Matures.Months - view.AsOf.Months).ToString("0.###", CultureInfo.InvariantCulture)) +
            "\n" + Text("cd-yield", Percent(cd.LockedYield)) + "\n" +
            (view.TimeBasis == InterestTimeBasis.InGame ? Text("projected-maturity", CalendarDate(page,
                page.WorldCalendarDays + (cd.Matures.Months - view.AsOf.Months) * page.DaysPerMonth)) :
                Text("runtime-maturity"))));
    }



    //// Formats a server-supplied world-day value using the calendar's month/day lengths and native date localization.
    //// Projected maturity dates are display-only; runtime-based contracts deliberately never use this projection.
    ////
    private static string CalendarDate(BankingStatementPage page, decimal days)
    {
        if (days < 0 || page.DaysPerMonth <= 0 || page.HoursPerDay <= 0) return Text("date-unavailable");
        var month = decimal.Floor(days / page.DaysPerMonth);
        var year = decimal.Floor(month / 12);
        var day = decimal.Floor(days % page.DaysPerMonth) + 1;
        var hours = (days - decimal.Floor(days)) * (decimal)page.HoursPerDay;
        return Lang.Get("dateformat", day, Lang.Get("month-" + (EnumMonth)((int)(month % 12) + 1)),
            year.ToString("0", CultureInfo.InvariantCulture), decimal.Floor(hours).ToString("00", CultureInfo.InvariantCulture),
            decimal.Floor((hours % 1) * 60).ToString("00", CultureInfo.InvariantCulture));
    }



    //// Discloses the server quote's principal, payoff, pricing components, and irreversible lockup before purchase.
    //// Exact maturity can be below principal; this formatter never introduces a principal or yield floor.
    ////
    internal static string Quote(CdQuoteView quote)
    {
        return Text("confirm-cd", Exact(quote.PrincipalUnits), quote.TenorMonths, Exact(quote.MaturityUnits)) + "\n\n" +
            Text("quote-components", Percent(quote.RiskFreeYield), Percent(quote.BaseSpread),
                Percent(quote.LevelSpread), Percent(quote.SlopeSpread), Percent(quote.LiquiditySpread)) + "\n" +
            Text("cd-yield", Percent(quote.Yield)) + "\n\n" + Text("cd-lockup");
    }



    //// Summarizes the immutable print payload before the player approves the paper-for-statement exchange.
    ////
    internal static string PrintedStatementSummary(PrintedStatementData data)
    {
        return data.Heading + "\n" + data.BankName + "\n\n" + Text("holder", Safe(data.HolderName)) + "\n" +
            Text("statement-balances", Exact(data.RustyUnits), Exact(data.TemporalUnits)) + "\n" +
            Text("statement-rows", data.History.Length.ToString());
    }



    //// Formats stable-ID outbox content without exposing the ID itself or inventing counterparties for summaries.
    //// The caller acknowledges only after the engine accepts this text for visible chat delivery.
    ////
    internal static string Notice(NoticeView notice)
    {
        return Text("notice", Text(notice.Kind.ToString()), notice.Count, Exact(notice.TotalUnits),
            Text(notice.Currency.ToString()), notice.SenderName is null ? Text("notice-history") : Safe(notice.SenderName));
    }



}
