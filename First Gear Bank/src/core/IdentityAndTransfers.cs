/*
 * Implements recipient identity, rusty-savings transfers, administrative corrections, and their notification outbox.
 * This partial declaration uses the same gate and immutable BankState as BankingCoordinator's command entry point.
 * Registry observation and delivery acknowledgment publish control revisions; financial helpers return candidates
 * for Execute to publish together with request sequencing and their balanced journal records.
 *
 * The name registry includes only identities observed through authenticated activity in this world's current epoch.
 * Current names resolve case-insensitively, historical names remain audit-only, and collisions are valid but ambiguous.
 * A transfer confirmation binds one selected mapping to a sender conversation; unrelated registry changes do not
 * invalidate it, while a recipient rename or new collision does.  Account ownership always uses the hidden UID.
 *
 * Transfers accrue both participants to one instant in ordinal identity order.  ExactAmount retains the confirmed
 * bank units; AllAtCommit drains the exact post-accrual rusty balance.  Corrections require current host permission,
 * a nonblank audit reason, and a balanced new record instead of editing an earlier balance or journal operation.
 *
 * Successful transfers/corrections add notification intent to the same financial candidate.  Online recipients get
 * individual content; offline events can join unsealed summaries.  Login seals content under a stable notice ID,
 * and acknowledgment removes the retained intent.  Network delivery and client display deduplication belong to the
 * adapter; notification failure cannot reverse money.  Registry quarantine blocks name services independently of cash.
 */

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace FirstGearBank.Core;

/// Identity, transfer, correction, and notification operations of the world-scoped banking coordinator.
/// These methods share publication and request authority with the main declaration; there is no separate mutable
/// transfer service or name list outside BankState that can bypass its revision boundary.
// ReSharper disable once ClassCannotBeInstantiated -- Instances are created through coordinator factory paths.
public sealed partial class BankingCoordinator
{



    //// Commits an authenticated current-name observation supplied by the host's join or current-connection workflow.
    ////
    //// An unchanged identity/name pair is a no-op.  A rename moves only the current-name index membership, appends the
    //// displaced name to audit history, and increments both the registry revision and this entry's mapping version.
    //// Observation does not open an account and must never be driven by a server-wide historical player collection.
    ////
    public void ObservePlayer(string player, string canonicalName)
    {
        lock (gate)
        {
            ValidatePlayer(player);
            RequireRegistry(state);
            if (string.IsNullOrWhiteSpace(canonicalName) || canonicalName.Length > 256)
                throw new BankException(BankError.InvalidSession);
            // Idempotent observations consume no revision; effective renames preserve the displaced name only for
            // audit.
            var prior = state.Names.GetValueOrDefault(player);
            if (prior?.Name == canonicalName) return;
            var index = state.NameIndex;
            if (prior is not null)
            {
                var remaining = index[prior.Name].Remove(player);
                index = remaining.IsEmpty ? index.Remove(prior.Name) : index.SetItem(prior.Name, remaining);
            }
            index = index.SetItem(canonicalName, index.GetValueOrDefault(canonicalName, []).Add(player));
            var revision = checked(state.RegistryRevision + 1);
            var history = prior is null ? ImmutableArray<string>.Empty : prior.PreviousNames.Add(prior.Name);
            Publish(state with
            {
                RegistryRevision = revision,
                NameIndex = index,
                Names = state.Names.SetItem(player, new(canonicalName, revision, history))
            });
        }
    }



    //// Returns one bounded, sorted page of matching unambiguous current names from the healthy registry.
    //// The result reveals no account existence, balance, UID, or online status.  The adapter decides whether its
    //// configured recipient-selection mode exposes this listing and applies packet/rate limits before calling it.
    ////
    public ImmutableArray<string> KnownNames(string search = "", int offset = 0, int limit = 50)
    {
        lock (gate)
        {
            RequireRegistry(state);
            ValidatePage(offset, limit);
            return state.NameIndex.Where(p => p.Value.Count == 1 &&
                p.Key.Contains(search, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key)
                .Order(StringComparer.OrdinalIgnoreCase).Skip(offset).Take(limit).ToImmutableArray();
        }
    }



    //// Creates a thirty-runtime-second recipient token after validating the sender's current Banker conversation.
    ////
    //// A supplied amount is parsed with the conversation's captured display precision and stored as exact bank units.
    //// Null means AllAtCommit and binds the rule rather than an estimated balance.  The selected epoch, current name,
    //// mapping version, and recipient identity are retained internally; only the opaque token is returned.
    //// Creating a token replaces the previous recipient confirmation for this scope and does not move money.
    ////
    public Guid ConfirmRecipient(string player, Guid scope, string name, string? exactAmount)
    {
        lock (gate)
        {
            RequireRegistry(state);
            var session = RequireSession(player, scope);
            var recipient = Resolve(state, name);
            if (recipient == player) throw new BankException(BankError.SelfTransfer);
            long? units = exactAmount is null ? null : Money.Parse(exactAmount, session.DisplayPrecision).Units;
            if (units is <= 0) throw new BankException(BankError.InvalidAmount);
            var entry = state.Names[recipient];
            var token = new TransferConfirmation(Guid.NewGuid(), player, scope, state.RegistryEpoch, recipient,
                entry.MappingVersion, entry.Name, units, host.SampleClock().RuntimeSeconds + 30);
            var confirmations = state.Confirmations.RemoveRange(state.Confirmations.Where(p => p.Value.Scope == scope)
                .Select(p => p.Key)).Add(token.Id, token);
            Publish(state with { Confirmations = confirmations });
            return token.Id;
        }
    }



    //// Builds a confirmed rusty transfer candidate after rechecking token ownership, expiry, and recipient identity.
    ////
    //// The selected mapping must still resolve uniquely in the same epoch; unrelated registry revisions are harmless.
    //// Both accounts accrue at the operation's single financial instant before funds/cap validation.  One balanced
    //// record moves the amount, then a durable notice and the sender's new cooldown enter the same candidate.
    //// Execute publishes success; any earlier rejection leaves all candidate financial changes unpublished.
    ////
    private BankState Transfer(BankState candidate, RequestKey request, Guid tokenId, ClockSample sample, Guid command)
    {
        RequireRegistry(candidate);
        if (!candidate.Confirmations.TryGetValue(tokenId, out var token) || token.Player != request.Player ||
            token.Scope != request.Scope || token.ExpiresAtSeconds <= sample.RuntimeSeconds ||
            token.RegistryEpoch != candidate.RegistryEpoch ||
            !candidate.Names.TryGetValue(token.Recipient, out var entry) || entry.MappingVersion != token.MappingVersion ||
            entry.Name != token.CanonicalName || Resolve(candidate, entry.Name) != token.Recipient)
            throw new BankException(BankError.ExpiredConfirmation);
        if (token.Recipient == request.Player) throw new BankException(BankError.SelfTransfer);
        if (candidate.Cooldowns.GetValueOrDefault(request.Player) > sample.RuntimeSeconds)
            throw new BankException(BankError.Cooldown);
        // A fixed participant order remains deterministic even if a later implementation introduces account locks.
        foreach (var player in new[] { request.Player, token.Recipient }.Order(StringComparer.Ordinal))
            candidate = FinanceEngine.Accrue(candidate, player, candidate.Clock.Position, sample.WorldDays, command, request);
        if (!candidate.Accounts.TryGetValue(request.Player, out var sender)) throw new BankException(BankError.NoAccount);
        // AllAtCommit uses the materialized ledger balance, including fractions hidden by display precision.
        var units = token.ExactUnits ?? sender.RustyUnits;
        if (units <= 0) throw new BankException(BankError.InvalidAmount);
        Posting[] postings = [new(LedgerKind.Customer, Currency.Rusty, request.Player, null, units),
            new(LedgerKind.Customer, Currency.Rusty, token.Recipient, null, -units)];
        candidate = Ledger.Append(candidate, command, request, TransactionType.Transfer, candidate.Clock.Position,
            sample.WorldDays, Names(request.Player, token.Recipient), postings);
        candidate = AddNotice(candidate, token.Recipient, TransactionType.Transfer, Currency.Rusty,
            host.PlayerName(request.Player), units);
        return candidate with
        {
            Confirmations = candidate.Confirmations.Remove(tokenId),
            Cooldowns = candidate.Cooldowns.SetItem(request.Player,
                sample.RuntimeSeconds + candidate.Options.TransferCooldownSeconds)
        };
    }



    //// Builds an audited signed balance correction for an existing account resolved through the current-name registry.
    ////
    //// Current administrative permission comes from the host at execution time.  A referenced undo must oppose the
    //// original correction's amount, currency, and target; neither ordinary corrections nor undo edits prior records.
    //// Target accrual precedes the new balanced posting, and notice/funding changes accompany it in the candidate.
    //// Missing accounts, malformed reasons, insufficient resulting cash, and cap violations reject the whole action.
    ////
    private BankState Correct(BankState candidate, CommandContext context, BankCommand command,
        ClockSample sample, Guid commandId)
    {
        if (!host.CanCorrectBalances(context.Request.Player)) throw new BankException(BankError.PermissionDenied);
        if (string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Length > 2048 || command.Units == 0)
            throw new BankException(BankError.InvalidAmount);
        var recipient = Resolve(candidate, command.RecipientName ?? "");
        if (!candidate.Accounts.ContainsKey(recipient)) throw new BankException(BankError.NoAccount);
        // Undo is an explicit opposite posting for the same target/currency, not a journal deletion or snapshot
        // restore.
        if (command.RelatedOperation is { } originalId)
        {
            var original = candidate.Journal.FirstOrDefault(r => r.OperationId == originalId);
            if (original is null || original.Type != TransactionType.AdminCorrection ||
                !original.Postings.Any(p => p.Account == LedgerKind.Customer && p.Player == recipient &&
                    p.Currency == command.Currency && p.SignedUnits == command.Units))
                throw new BankException(BankError.InvalidAmount);
        }
        candidate = FinanceEngine.Accrue(candidate, recipient, candidate.Clock.Position, sample.WorldDays,
            commandId, context.Request);
        candidate = Ledger.Append(candidate, commandId, context.Request, TransactionType.AdminCorrection,
            candidate.Clock.Position, sample.WorldDays, Names(context.Request.Player, recipient),
            FinanceEngine.Pair(recipient, command.Currency, LedgerKind.Correction, command.Units),
            reason: command.Reason, related: command.RelatedOperation);
        candidate = AddNotice(candidate, recipient, TransactionType.AdminCorrection, command.Currency,
            host.PlayerName(context.Request.Player), command.Units);
        return command.Currency == Currency.Rusty ?
            FinanceEngine.CheckpointLiquidity(candidate, candidate.Clock.Position, liquidityIndex) : candidate;
    }



    //// Adds notification intent for the candidate's most recently appended transfer or correction record.
    ////
    //// Online recipients receive an individual sealed notice with the sender's transaction-time name.  Offline events
    //// merge only into an unsealed bucket of the same recipient, kind, and currency; otherwise a stable ID derives
    //// from this operation and its recipient.  Amount/count aggregation is part of the financial candidate, while
    //// actual dispatch occurs later and cannot cause the journal action to be executed again.
    ////
    private BankState AddNotice(BankState candidate, string recipient, TransactionType kind, Currency currency,
        string sender, long units)
    {
        var record = candidate.Journal[^1];
        var online = host.IsOnline(recipient);
        // Sealed or initially individual notices keep their identity and contents even across a later disconnect.
        var bucket = online ? null : candidate.Notices.Values.FirstOrDefault(n =>
            n.Recipient == recipient && n.Kind == kind && n.Currency == currency && !n.Sealed);
        if (bucket is not null)
            return candidate with
            {
                Notices = candidate.Notices.SetItem(bucket.Id, bucket with
                {
                    Count = checked(bucket.Count + 1),
                    TotalUnits = bucket.TotalUnits + units,
                    LastSequence = record.Sequence
                })
            };
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"FGB/notice/v1:{record.OperationId:N}:{recipient.Length}:{recipient}:{kind}:{currency}")).AsSpan(0, 16));
        return candidate with
        {
            Notices = candidate.Notices.Add(id,
            new(id, recipient, kind, currency, online ? sender : null, units, 1, record.Sequence, online))
        };
    }



    //// Seals the authenticated recipient's open summaries and returns a bounded sequence-ordered delivery page.
    ////
    //// The host invokes this during login or notice dispatch.  Sealing freezes the content under its stable ID so
    //// later offline events can start another bucket; returning a notice does not acknowledge that it was displayed.
    //// Only NoticeView leaves this method, keeping recipient UIDs out of the player-facing payload.
    ////
    public ImmutableArray<NoticeView> PendingNotices(string authenticatedPlayer, int limit = 100)
    {
        lock (gate)
        {
            ValidatePage(0, limit);
            var notices = state.Notices;
            foreach (var notice in notices.Values.Where(n => n.Recipient == authenticatedPlayer && !n.Sealed))
                notices = notices.SetItem(notice.Id, notice with { Sealed = true });
            if (!ReferenceEquals(notices, state.Notices)) Publish(state with { Notices = notices });
            return notices.Values.Where(n => n.Recipient == authenticatedPlayer).OrderBy(n => n.LastSequence)
                .Take(limit).Select(n => new NoticeView(n.Id, n.Kind, n.Currency, n.SenderName, n.TotalUnits, n.Count))
                .ToImmutableArray();
        }
    }



    //// Commits acknowledgment of a sealed notice by its authenticated recipient and advances the delivery watermark.
    //// A repeated acknowledgment of an already removed notice is a no-op.  Wrong-recipient or unsealed acknowledgments
    //// reject; the adapter must authenticate the packet and retain client-side display deduplication independently.
    ////
    public void AcknowledgeNotice(string authenticatedPlayer, Guid noticeId)
    {
        lock (gate)
        {
            if (!state.Notices.TryGetValue(noticeId, out var notice)) return;
            if (notice.Recipient != authenticatedPlayer || !notice.Sealed)
                throw new BankException(BankError.PermissionDenied);
            Publish(state with
            {
                Notices = state.Notices.Remove(noticeId),
                DeliveryWatermarks = state.DeliveryWatermarks.SetItem(authenticatedPlayer,
                    Math.Max(state.DeliveryWatermarks.GetValueOrDefault(authenticatedPlayer), notice.LastSequence))
            });
        }
    }



    //// Resolves trimmed input through the current-name index and returns its one internal recipient identity.
    //// Empty/unknown matches and collisions have distinct safe errors.  Historical aliases and other-world players
    //// are absent from this index, and registry quarantine takes precedence over all name resolution.
    ////
    private static string Resolve(BankState candidate, string name)
    {
        RequireRegistry(candidate);
        if (!candidate.NameIndex.TryGetValue(name.Trim(), out var players) || players.Count == 0)
            throw new BankException(BankError.UnknownRecipient);
        if (players.Count != 1) throw new BankException(BankError.AmbiguousRecipient);
        return players.Single();
    }



    //// Enforces isolated recipient-service quarantine before observation, lookup, transfer, or name-based correction.
    //// The finance gate is separate, allowing other authenticated personal-account operations when their state is
    //// sound.
    ////
    private static void RequireRegistry(BankState candidate)
    {
        if (candidate.RegistryQuarantined) throw new BankException(BankError.RecipientServiceUnavailable);
    }



    //// Verifies scope ownership and the host's current Banker-session validity before exposing an account operation.
    //// A persisted or client-presented nonce alone is insufficient.  The returned scope supplies captured precision
    //// and sequencing metadata, not a transferable authorization for another identity.
    ////
    private ScopeState RequireSession(string player, Guid scope)
    {
        if (!state.Scopes.TryGetValue(scope, out var session) || session.Player != player ||
            !host.IsSessionValid(player, scope)) throw new BankException(BankError.InvalidSession);
        return session;
    }



    //// Requires a nonnegative offset and a page size from one through one hundred before materializing responses.
    //// This bounds each returned page; search indexing and transport-frequency limits remain separate concerns.
    ////
    private static void ValidatePage(int offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
    }



}
