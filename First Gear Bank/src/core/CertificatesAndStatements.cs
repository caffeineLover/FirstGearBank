/*
 * Implements certificate-of-deposit offers, confirmed purchases, and account/contract display projections.
 * This partial declaration shares BankingCoordinator's gate, immutable state, and execution authority.  Quote creation
 * stores a short-lived server snapshot; Purchase adds the contract and balanced funding record to Execute's candidate.
 * Contract maturity processing belongs to FinanceEngine and uses the payoff stored here without recalculating its
 * yield.
 *
 * CD pricing combines the current risk-free curve with tenor base spread, the month's shared level/slope shocks,
 * and the observed liquidity adjustment.  A thirty-runtime-second offer retains its eligibility and component snapshot
 * across month/configuration changes.  Confirmation sets issue and maturity times from the commit's financial instant,
 * so the contract starts when purchased rather than when first quoted.  There is no early-redemption operation.
 *
 * Statement reads validate the conversation, materialize the account and due events, and publish that catch-up before
 * returning name-only balances, totals, active CDs, and a bounded history page.  Informational CD values are clamped
 * between both stored endpoints, permitting a negative-yield value below principal without making it spendable.
 *
 * The host advances global time before requesting a quote and owns calendar-date rendering, UI, printed-item creation,
 * paper consumption, and the one-successful-print conversation allowance.  These methods provide financial/display
 * content, not physical statements or client authority over account identities, rates, or quoted payoffs.
 */

using System.Collections.Immutable;

namespace FirstGearBank.Core;

/// CD issuance and statement-view portion of the world coordinator.
/// Pricing reads published rate/liquidity authority, purchase builds an atomic financial candidate, and statement
/// methods translate privileged records into display data without exposing customer identity keys.
// ReSharper disable once ClassCannotBeInstantiated -- Instances are created through coordinator factory paths.
public sealed partial class BankingCoordinator
{



    //// Creates a session-bound CD offer from the currently published interest-rate and liquidity state.
    ////
    //// Principal and tenor must satisfy the active eligibility rules.  The risk-free yield and spread components are
    //// frozen with the rounded maturity units and a thirty-runtime-second deadline.  Creating the offer replaces
    //// the prior quote for this scope but performs no cash movement, rate draw, shock draw, or liquidity checkpoint.
    //// The host must advance global financial time before quotation; the returned view contains no owner UID.
    ////
    public CdQuoteView QuoteCertificate(string player, Guid scope, Money principal, int tenorMonths)
    {
        lock (gate)
        {
            RequireFinance();
            RequireSession(player, scope);
            var market = state.Market.Current;
            var settings = market.Settings;
            if (principal.Units < settings.MinimumCdPrincipalUnits || principal.Units > Money.MaximumUnits ||
                !settings.TenorsMonths.Contains(tenorMonths)) throw new BankException(BankError.InvalidAmount);
            // Tenors are financial months; all yield components are annual continuous decimals before compounding.
            var years = tenorMonths / 12.0;
            var rfr = RateMath.Yield(settings, market.Rate, years);
            var baseSpread = settings.BaseSpreadScale * Math.Pow(tenorMonths, settings.BaseSpreadExponent);
            var slope = market.SlopeShock * Math.Log(tenorMonths) / Math.Log(12);
            var liquidity = FinanceEngine.LiquidityAt(state.Liquidity, state.Clock.Position);
            var yield = rfr + baseSpread + market.LevelShock + slope + liquidity;
            var maturity = RateMath.Payoff(principal.Units, yield * years);
            var quote = new CdQuote(Guid.NewGuid(), player, scope, principal.Units, tenorMonths,
                host.SampleClock().RuntimeSeconds + 30, state.Clock.Position, market.ConfigurationRevision,
                settings.MinimumCdPrincipalUnits, rfr, baseSpread, market.LevelShock, slope, liquidity, yield, maturity);
            // Store authoritative eligibility and pricing privately; confirmation later submits only the opaque ID.
            Publish(state with
            {
                Quotes = state.Quotes.RemoveRange(state.Quotes.Where(p => p.Value.Scope == scope)
                .Select(p => p.Key)).Add(quote.Id, quote)
            });
            return new(quote.Id, quote.PrincipalUnits, quote.TenorMonths, quote.ExpiresAtSeconds, quote.RiskFreeYield,
                quote.BaseSpread, quote.LevelSpread, quote.SlopeSpread, quote.LiquiditySpread, quote.Yield,
                quote.MaturityUnits);
        }
    }



    //// Consumes a valid stored quote and funds one immutable CD from the caller's already accrued rusty savings.
    ////
    //// Identity, conversation, expiry, and the quote's own eligibility snapshot are checked at confirmation time.
    //// The original price remains locked; issue time is now and maturity is now plus its quoted whole-month tenor.
    //// Ledger validates the savings debit and installs the contract, then liquidity is checkpointed on the candidate.
    //// Execute publishes these changes together with quote consumption and request replay protection.
    ////
    private BankState Purchase(BankState candidate, RequestKey request, Guid quoteId, ClockSample sample, Guid command)
    {
        if (!candidate.Quotes.TryGetValue(quoteId, out var quote) || quote.Player != request.Player ||
            quote.Scope != request.Scope || quote.ExpiresAtSeconds <= sample.RuntimeSeconds ||
            quote.PrincipalUnits < quote.MinimumPrincipalUnits || quote.TenorMonths <= 0)
            throw new BankException(BankError.ExpiredConfirmation);
        if (!candidate.Accounts.ContainsKey(request.Player)) throw new BankException(BankError.NoAccount);
        var issue = candidate.Clock.Position;
        var maturity = new FinancialInstant(issue.Months + quote.TenorMonths);
        maturity.Validate();
        var cd = new Certificate(Guid.NewGuid(), request.Player, quote.PrincipalUnits, quote.MaturityUnits,
            issue, maturity, quote);
        Posting[] postings = [new(LedgerKind.Customer, Currency.Rusty, request.Player, null, cd.PrincipalUnits),
            new(LedgerKind.CdPrincipal, Currency.Rusty, request.Player, cd.Id, -cd.PrincipalUnits)];
        candidate = Ledger.Append(candidate, command, request, TransactionType.CdPurchase, issue, sample.WorldDays,
            Names(request.Player), postings, contract: cd);
        candidate = candidate with { Quotes = candidate.Quotes.Remove(quoteId) };
        return FinanceEngine.CheckpointLiquidity(candidate, issue, liquidityIndex);
    }



    //// Produces an immutable account statement after validating the conversation and materializing current finance.
    ////
    //// One host sample drives due global events and the account's cash accrual.  That catch-up is published even
    //// though the requested operation is a view.  The response contains name-only history, cumulative totals, active
    //// contract projections, and clearly distinguished continuous/effective rates.  Pagination selects journal
    //// records; physical printing, bearer item attributes, and paper/allowance validation are not performed here.
    ////
    public Statement GetStatement(string player, Guid scope, int offset = 0, int limit = 50)
    {
        lock (gate)
        {
            RequireFinance();
            RequireSession(player, scope);
            ValidatePage(offset, limit);
            var sample = host.SampleClock();
            var command = Guid.NewGuid();
            var candidate = FinanceEngine.Advance(state, sample, liquidityIndex, command);
            if (!candidate.Accounts.ContainsKey(player)) throw new BankException(BankError.NoAccount);
            candidate = FinanceEngine.Accrue(candidate, player, candidate.Clock.Position, sample.WorldDays, command, null);
            Publish(candidate);
            var account = state.Accounts[player];
            // Use the player's sequence index to select a bounded history page without exposing ledger account IDs.
            var sequence = state.History.GetValueOrDefault(player, []);
            var rows = sequence.Skip(offset).Take(limit).SelectMany(n => ToRows(state.Journal[checked((int)n - 1)], player))
                .ToImmutableArray();
            var cds = state.Certificates.Values.Where(c => c.Player == player && !c.Matured)
                .OrderBy(c => c.Matures.Months).ThenBy(c => c.Id).Select(c => new CertificateView(c.Id,
                    c.PrincipalUnits, CertificateValue(c, state.Clock.Position), c.MaturityUnits, c.Issued, c.Matures,
                    c.Quote.Yield)).ToImmutableArray();
            var rate = state.Market.Current.Rate;
            return new(host.PlayerName(player), state.Clock.Position, state.Clock.Basis, account.RustyUnits,
                account.TemporalUnits, rate, double.ExpM1(rate / 12), double.ExpM1(rate),
                state.Totals.GetValueOrDefault(player, ImmutableDictionary<string, decimal>.Empty), cds, rows,
                sequence.Count);
        }
    }



    //// Calculates a display-only CD value at a financial instant using the contract's locked yield.
    ////
    //// At or before issue it returns exact principal; at or after maturity it returns the stored rounded payoff.
    //// Intermediate values compound the original principal and clamp between both endpoints to prevent rounding
    //// overshoot.  The lower endpoint may be maturity below principal; this is not a principal guarantee or
    //// redemption.
    ////
    public static long CertificateValue(Certificate certificate, FinancialInstant time)
    {
        time.Validate();
        if (time.Months <= certificate.Issued.Months) return certificate.PrincipalUnits;
        if (time.Months >= certificate.Matures.Months) return certificate.MaturityUnits;
        var exponent = certificate.Quote.Yield * (double)((time.Months - certificate.Issued.Months) / 12m);
        var value = Money.Compound(new(certificate.PrincipalUnits), exponent).Value.Units;
        return Math.Clamp(value, Math.Min(certificate.PrincipalUnits, certificate.MaturityUnits),
            Math.Max(certificate.PrincipalUnits, certificate.MaturityUnits));
    }



    //// Converts the requesting player's customer postings into display-sign rows grouped by currency.
    ////
    //// A transfer becomes TransferIn or TransferOut for this holder while retaining its one original sequence.
    //// Fully capped CD maturity still receives a row even when credited cash is zero.  Only frozen display names
    //// and safe metadata are emitted; internal player keys are used for filtering and do not enter the result.
    ////
    private static IEnumerable<StatementRow> ToRows(JournalRecord record, string player)
    {
        foreach (var currency in Enum.GetValues<Currency>())
        {
            var units = record.Postings.Where(p => p.Account == LedgerKind.Customer && p.Player == player &&
                p.Currency == currency).Aggregate(0L, (sum, p) => checked(sum - p.SignedUnits));
            if (units == 0 && !(currency == Currency.Rusty && record.Type == TransactionType.CdMaturity &&
                    record.Contract?.Player == player)) continue;
            var type = record.Type == TransactionType.Transfer ? units < 0 ? "TransferOut" : "TransferIn" :
                record.Type.ToString();
            yield return new(record.Sequence, type, currency, units, record.EffectiveTime,
                record.Names.Values.ToImmutableArray(), record.WasCapped);
        }
    }



}
