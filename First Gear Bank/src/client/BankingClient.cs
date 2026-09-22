/*
 * Owns the local player's disposable banking conversation and the firstgearbank-v1 transport receiver.
 * Native dialogs submit intent through this controller; only server replies provide balances, rates, names, and
 * confirmation values.  No coordinator, account identity, inventory mutation, or saved financial authority lives here.
 * Money in display DTOs is six-decimal integer bank units; UI deadlines use local monotonic milliseconds only.
 *
 * One outstanding request prevents overlapping confirmations and double-click submissions.  Read requests use negative
 * correlation numbers; financial commands use the server's positive conversation sequence.  An uncertain mutation
 * retains its original serialized packet for explicit retry, never a fresh financial request key.  Closing the dialog
 * does not cancel money already sent; its response is still handled before the server conversation is released.
 *
 * A watched entity marker is merely an interaction hint set by trusted server registration.  The server independently
 * validates every Banker and session.  This file does not spawn NPCs, manage branches, or calculate Max locally.
 * Notification IDs use a bounded persisted world/player display cache; acknowledgment follows chat submission and
 * cache persistence, not proof the player read it.  Reconnecting never replays a financial operation automatically.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FirstGearBank.Core;
using FirstGearBank.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace FirstGearBank.Client;

/// Bounded server reply with an untyped display body interpreted only after matching the outstanding request.
internal sealed record ClientBankingReply(Guid Scope, long Sequence, string Status, JsonElement Body);

/// Session settings captured by the server; the client uses these for labels and choices, never monetary eligibility.
internal sealed record BankingSessionView(int DisplayPrecision, long NextSequence, string RecipientMode,
    int[] TenorsMonths, long MinimumCdPrincipalUnits);

/// Server-selected statement page and independent CD count; both lists share the requested record offset.
internal sealed record BankingStatementPage(Statement Statement, int TotalCertificates, decimal WorldCalendarDays,
    int DaysPerMonth, float HoursPerDay, decimal[] HistoryCalendarDays);

/// Exact server-normalized cash intent shown before submitting the ordinary deposit or withdrawal command.
internal sealed record PhysicalBankingPreview(string Action, string Currency, string Amount, long ExactUnits);

/// Name-safe transfer confirmation; null ExactUnits explicitly means the full balance determined at commit.
internal sealed record TransferBankingPreview(Guid Token, string Name, long? ExactUnits, decimal ExpiresInSeconds);

/// Frozen server CD quote paired with a relative display deadline, not the server's private runtime origin.
internal sealed record CertificateBankingPreview(CdQuoteView Quote, decimal ExpiresInSeconds);

/// Frozen physical-statement preview paired with its conversation-owned print token.
internal sealed record StatementBankingPreview(Guid Token, PrintedStatementData Data);

/// Bounded notice delivery with an opaque world/player cache namespace, never the recipient's internal identity.
internal sealed record BankingNoticeDelivery(string ScopeHash, NoticeView[] Notices);

/// Immutable pending intent and wire bytes, retained together so retry cannot accidentally change a mutation payload.
internal sealed record PendingBankingRequest(BankingRequest Request, byte[] Bytes, long SentAt);

/// Locally displayed confirmation binding the subsequent intent to the server-returned values and opaque token.
internal sealed record BankingConfirmation(BankingRequest Request, string Text, long Deadline,
    bool UsesFinancialSequence = true);

/// Client-thread controller owned by the mod system, exposing only interaction entry and deterministic disposal.
/// Internal state is borrowed by the ledger dialog and never persisted or used to calculate financial outcomes.
public sealed class BankingClient : IDisposable
{
    internal const int PageSize = 2;
    private readonly ICoreClientAPI api;
    private readonly IClientNetworkChannel channel;
    private readonly BankingDialog dialog;
    private readonly BankingNoticeReceipts receipts;
    private readonly Queue<Guid> acknowledgments = new();
    private readonly HashSet<Guid> queuedAcknowledgments = new();
    private readonly long tickListener;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 12 };
    private PendingBankingRequest? pending;
    private long readSequence;
    private long nextSequence = 1;
    private long bankerEntity;
    private bool closeRequested;
    private bool disposed;
    private bool refreshQueued;
    private int generation;
    internal Guid Scope { get; private set; }
    internal BankingSessionView? Session { get; private set; }
    internal BankingStatementPage? Display { get; private set; }
    internal BankingConfirmation? Confirmation { get; private set; }
    internal string[] Names { get; private set; } = [];
    internal string Status { get; private set; } = BankingDisplay.Text("welcome");
    internal bool Busy => pending is not null;
    internal bool CanRetry => pending is not null && Environment.TickCount64 - pending.SentAt >= 5000;



    //// Registers the identical envelope type and main-thread lifecycle callbacks once during client startup.
    //// The engine owns the channel; this controller owns its dialog, retry state, and callback registrations.
    ////
    public BankingClient(ICoreClientAPI api)
    {
        this.api = api;
        channel = api.Network.RegisterChannel("firstgearbank-v1").RegisterMessageType<BankingPacket>();
        channel.SetMessageHandler<BankingPacket>(Receive);
        receipts = new(api);
        dialog = new(api, this);
        api.Event.MouseDown += MouseDown;
        api.Event.LeaveWorld += LeaveWorld;
        tickListener = api.Event.RegisterGameTickListener(Tick, 500);
    }



    //// Opens the ledger for a content-selected entity, requesting fresh server authority before enabling operations.
    //// Existing unresolved work is shown instead of opening a second scope that could obscure its outcome.
    ////
    public void OpenBanker(long entityId)
    {
        if (disposed || entityId <= 0 || !channel.Connected) return;
        closeRequested = false;
        dialog.TryOpen();
        if (pending is not null) { Refresh(); return; }
        ReleaseConversation();
        bankerEntity = entityId;
        dialog.Reset();
        Read(new("open", BankerEntity: entityId));
    }



    //// Intercepts right-click only for a server-marked Banker when no other dialog is consuming mouse input.
    //// Forging the visual marker cannot confer authority because OpenBanker still sends an untrusted entity ID.
    ////
    private void MouseDown(MouseEvent mouse)
    {
        if (disposed || mouse.Handled || mouse.Button != EnumMouseButton.Right) return;
        // OpenedGuis also includes permanent HUD elements, which must not block ordinary world interaction.
        if (api.Gui.OpenedGuis.Any(gui => gui.PrefersUngrabbedMouse)) return;
        var entity = api.World.Player?.CurrentEntitySelection?.Entity;
        if (entity?.WatchedAttributes.GetBool("firstgearbank:banker") != true) return;
        mouse.Handled = true;
        OpenBanker(entity.EntityId);
    }



    //// Sends a read or preview with a fresh negative correlation number; it never consumes a core mutation sequence.
    //// Replacing the displayed confirmation prevents edits or a later quote from reusing stale approval.
    ////
    internal void Read(BankingRequest request)
    {
        if (disposed || Busy || !channel.Connected) return;
        Confirmation = null;
        Send(request with { Scope = Scope, Sequence = --readSequence });
    }



    //// Submits exactly the currently displayed server-resolved intent once, after a deliberate confirmation click.
    //// Local expiry only disables a stale screen; the server independently enforces its token deadline.
    ////
    internal void Confirm()
    {
        if (Busy || Confirmation is not { } confirmation) return;
        if (Environment.TickCount64 >= confirmation.Deadline)
        {
            CancelConfirmation();
            Status = BankingDisplay.Error("ExpiredConfirmation");
            Refresh();
            return;
        }
        Confirmation = null;
        Send(confirmation.Request with
        {
            Scope = Scope,
            Sequence = confirmation.UsesFinancialSequence ? nextSequence : --readSequence,
            Limit = PageSize
        });
    }



    //// Clears unsubmitted approval without sending a financial command or altering the source form's entered intent.
    ////
    internal void CancelConfirmation()
    {
        Confirmation = null;
        Refresh();
    }



    //// Serializes under the existing byte limit and retains ownership until an attributable reply arrives.
    //// A synchronous transport failure is treated as uncertain delivery, especially for financial mutations.
    ////
    private void Send(BankingRequest request)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
        if (bytes.Length > 4096) { Status = BankingDisplay.Error("InvalidRequest"); Refresh(); return; }
        pending = new(request, bytes, Environment.TickCount64);
        Status = BankingDisplay.Text("waiting");
        try { channel.SendPacket(new BankingPacket { Data = bytes }); }
        catch (Exception) { Status = BankingDisplay.Text("uncertain"); }
        Refresh();
    }



    //// Retransmits financial bytes unchanged; read retries receive new correlation IDs to reject late old replies.
    //// In particular, an open retry must not accept the scope from an earlier open that the server has superseded.
    ////
    internal void Retry()
    {
        if (!CanRetry || pending is not { } previous || !channel.Connected) return;
        if (previous.Request.Sequence > 0)
        {
            pending = previous with { SentAt = Environment.TickCount64 };
            try { channel.SendPacket(new BankingPacket { Data = previous.Bytes }); }
            catch (Exception) { Status = BankingDisplay.Text("uncertain"); }
            Refresh();
        }
        else
        {
            pending = null;
            Read(previous.Request);
        }
    }



    //// Copies only bounded reply bytes before entering the client thread; world generation rejects queued old traffic.
    //// Malformed server display data is not interpreted as proof that an outstanding transaction failed.
    ////
    private void Receive(BankingPacket packet)
    {
        if (disposed || packet?.Data is not { Length: > 0 and <= 131_072 }) return;
        // ReSharper disable once HeapView.ClosureAllocation
        var bytes = (byte[])packet.Data.Clone();
        var receivedGeneration = generation;
        api.Event.EnqueueMainThreadTask(() =>
        {
            if (disposed || receivedGeneration != generation) return;
            try
            {
                var reply = JsonSerializer.Deserialize<ClientBankingReply>(bytes, JsonOptions);
                if (reply is not null) HandleReply(reply);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
            {
                Status = BankingDisplay.Text("bad-reply");
                Refresh();
            }
        }, "firstgearbank-client-reply");
    }



    //// Matches correlation before updating UI state, keeping unsolicited notices separate from requested responses.
    //// Only a mutation result carrying Revision proves terminal admission; top-level failures do not advance sequence.
    ////
    private void HandleReply(ClientBankingReply reply)
    {
        if (reply.Status == "Notices") { ShowNotices(reply.Body); return; }
        if (reply.Status == "ConversationClosed" && reply.Scope == Scope && Scope != Guid.Empty)
        {
            Status = BankingDisplay.Error("InvalidSession");
            if (pending?.Request.Sequence > 0) Status += " " + BankingDisplay.Text("check-history");
            pending = null;
            ReleaseConversation();
            api.ShowChatMessage(Status);
            dialog.TryClose();
            return;
        }
        if (pending is not { } current || reply.Sequence != current.Request.Sequence ||
            (current.Request.Action != "open" && reply.Scope != Scope)) return;
        var terminal = reply.Body.ValueKind == JsonValueKind.Object && reply.Body.TryGetProperty("Revision", out _);
        if (current.Request.Sequence > 0 && reply.Status == "None" && !terminal) throw new JsonException();
        if (current.Request.Sequence > 0 && terminal) nextSequence = checked(current.Request.Sequence + 1);
        // Decode before clearing the request, preserving retry evidence if a malformed body cannot be rendered.
        if (reply.Status == "None") AcceptBody(current.Request, reply);
        pending = null;
        if (reply.Status != "None")
        {
            Status = BankingDisplay.Error(reply.Status);
            if (reply.Status is "InvalidSession" or "InvalidSequence" or "PayloadMismatch" or
                "AlreadyProcessedResponseExpired" or "SettlementQuarantined" or "CorruptState" or "ServiceUnavailable")
            {
                if (current.Request.Sequence > 0 && !terminal) Status += " " + BankingDisplay.Text("check-history");
                ReleaseConversation();
            }
        }
        else Status = BankingDisplay.Text(current.Request.Sequence > 0 ? "committed" : "ready");
        if (closeRequested)
        {
            if (current.Request.Sequence > 0) api.ShowChatMessage(Status);
            ReleaseConversation();
        }
        else if (reply.Status == "None" && current.Request.Action == "open")
            Read(new("statement", Limit: PageSize));
        Refresh();
    }



    //// Converts known player-safe bodies into read-only display state and explicit confirmation text.
    //// Relative deadlines subtract elapsed request time conservatively; financial dates remain server-provided values.
    ////
    private void AcceptBody(BankingRequest request, ClientBankingReply reply)
    {
        switch (request.Action)
        {
            case "open":
                var session = reply.Body.Deserialize<BankingSessionView>(JsonOptions) ?? throw new JsonException();
                if (reply.Scope == Guid.Empty || session.DisplayPrecision is < 0 or > 6 ||
                    session.TenorsMonths is not { Length: > 0 and <= 64 } || session.NextSequence < 1)
                    throw new JsonException();
                Session = session;
                Scope = reply.Scope;
                nextSequence = session.NextSequence;
                break;
            case "statement":
                Display = reply.Body.Deserialize<BankingStatementPage>(JsonOptions) ?? throw new JsonException();
                break;
            case "names":
                Names = reply.Body.Deserialize<string[]>(JsonOptions) ?? throw new JsonException();
                break;
            case "previewDeposit":
            case "previewWithdraw":
            case "previewWithdrawMax":
                var cash = reply.Body.Deserialize<PhysicalBankingPreview>(JsonOptions) ?? throw new JsonException();
                if (cash.Action != (request.Action == "previewDeposit" ? "deposit" : "withdraw") ||
                    cash.Currency != request.Currency) throw new JsonException();
                Confirmation = new(new(cash.Action, Amount: cash.Amount, Currency: cash.Currency),
                    BankingDisplay.Text("confirm-cash", BankingDisplay.Text(cash.Action),
                        BankingDisplay.Exact(cash.ExactUnits), BankingDisplay.Text(cash.Currency)) + "\n\n" +
                    BankingDisplay.Text("cash-recheck"), long.MaxValue);
                break;
            case "confirmTransfer":
                var transfer = reply.Body.Deserialize<TransferBankingPreview>(JsonOptions) ?? throw new JsonException();
                Confirmation = new(new("transfer", Token: transfer.Token), BankingDisplay.Text("confirm-transfer",
                    BankingDisplay.Safe(transfer.Name), transfer.ExactUnits is { } units ? BankingDisplay.Exact(units) :
                    BankingDisplay.Text("all-at-commit")) + "\n\n" + BankingDisplay.Text("irreversible"),
                    Deadline(transfer.ExpiresInSeconds));
                break;
            case "quoteCd":
                var cd = reply.Body.Deserialize<CertificateBankingPreview>(JsonOptions) ?? throw new JsonException();
                Confirmation = new(new("buyCd", Token: cd.Quote.Id), BankingDisplay.Quote(cd.Quote),
                    Deadline(cd.ExpiresInSeconds));
                break;
            case "previewPrintStatement":
                var print = reply.Body.Deserialize<StatementBankingPreview>(JsonOptions) ?? throw new JsonException();
                if (print.Token == Guid.Empty || print.Data is not { Version: 1 }) throw new JsonException();
                Confirmation = new(new("printStatement", Token: print.Token),
                    BankingDisplay.PrintedStatementSummary(print.Data) + "\n\n" +
                    BankingDisplay.Text("confirm-print"), long.MaxValue, false);
                break;
            case "printStatement":
                break;
            default:
                // A committed operation invalidates the old snapshot even if a fresh display could not be generated.
                Display = null;
                if (reply.Body.TryGetProperty("Display", out var display) && display.ValueKind == JsonValueKind.Object)
                    Display = display.Deserialize<BankingStatementPage>(JsonOptions);
                dialog.StatementOffset = 0;
                break;
        }
    }



    //// Converts a bounded relative server duration to a conservative local deadline without comparing clock origins.
    //// The original send instant accounts for response latency and never extends a server-issued confirmation.
    ////
    private long Deadline(decimal seconds)
    {
        return (pending?.SentAt ?? Environment.TickCount64) + (long)(Math.Clamp(seconds, 0, 30) * 1000);
    }



    //// Displays unseen notices, saves bounded display receipts, then queues acknowledgments at the client tick rate.
    //// A crash between rendering and saving may repeat a notice; no exactly-once visible-delivery claim is made.
    ////
    private void ShowNotices(JsonElement body)
    {
        var delivery = body.Deserialize<BankingNoticeDelivery>(JsonOptions) ?? throw new JsonException();
        var notices = delivery.Notices;
        if (notices is null || notices.Length > 50 || delivery.ScopeHash is null) throw new JsonException();
        receipts.Select(delivery.ScopeHash);
        foreach (var notice in notices)
        {
            if (!receipts.Contains(notice.Id))
            {
                api.ShowChatMessage(BankingDisplay.Notice(notice));
                receipts.RecordDisplayed(notice.Id);
            }
        }
        if (!receipts.Save()) return;
        foreach (var notice in notices)
        {
            if (acknowledgments.Count < 256 && queuedAcknowledgments.Add(notice.Id)) acknowledgments.Enqueue(notice.Id);
        }
    }



    //// Updates expiry feedback and sends at most two notice acknowledgments per second, below admission limits.
    //// A disappearing Banker closes idle UI; pending mutations remain visible as unresolved until a reply arrives.
    ////
    private void Tick(float elapsedSeconds)
    {
        if (disposed) return;
        if (channel.Connected && acknowledgments.TryDequeue(out var notice))
        {
            queuedAcknowledgments.Remove(notice);
            try
            {
                channel.SendPacket(new BankingPacket
                {
                    Data = JsonSerializer.SerializeToUtf8Bytes(new BankingRequest("acknowledge", Token: notice))
                });
            }
            catch (Exception)
            {
                // The durable server outbox will offer an unacknowledged notice again.
            }
        }
        if (CanRetry)
        {
            var timeout = BankingDisplay.Text(pending!.Request.Sequence > 0 ? "uncertain" : "timeout");
            if (Status != timeout) { Status = timeout; Refresh(); }
        }
        if (Confirmation is { } confirmation && Environment.TickCount64 >= confirmation.Deadline)
        {
            Confirmation = null;
            Status = BankingDisplay.Error("ExpiredConfirmation");
            Refresh();
        }
        if (Scope != Guid.Empty && !Busy)
        {
            var banker = api.World.GetEntityById(bankerEntity);
            var player = api.World.Player?.Entity;
            if (banker is null || !banker.Alive || banker.WatchedAttributes.GetBool("firstgearbank:banker") != true ||
                player is null || !player.Alive || player.Pos.Dimension != banker.Pos.Dimension ||
                player.Pos.XYZ.SquareDistanceTo(banker.Pos.XYZ) > 36)
            {
                Status = BankingDisplay.Error("InvalidSession");
                dialog.TryClose();
                ReleaseConversation();
            }
        }
    }



    //// Defers rebuilding widgets until the active mouse callback has finished using the old composer.
    //// Closed dialogs retain data without composing GPU resources; the next open rebuilds from current state.
    ////
    internal void Refresh()
    {
        if (refreshQueued || disposed) return;
        refreshQueued = true;
        api.Event.EnqueueMainThreadTask(() =>
        {
            refreshQueued = false;
            if (!disposed && dialog.IsOpened()) dialog.Rebuild();
        },
            "firstgearbank-client-display");
    }



    //// Treats window closure as loss of UI interest, never cancellation of a command that may already have committed.
    //// Pending work keeps its original scope until a correlated reply or world exit resolves local ownership.
    ////
    internal void Close()
    {
        closeRequested = true;
        Confirmation = null;
        if (!Busy) ReleaseConversation();
    }



    //// Retires client authority and asks the server to close the old scope without awaiting an irrelevant close reply.
    //// Display snapshots are cleared so reopening cannot show another conversation's stale money as a fresh result.
    ////
    private void ReleaseConversation()
    {
        if (Scope != Guid.Empty && channel.Connected)
        {
            try
            {
                channel.SendPacket(new BankingPacket { Data = JsonSerializer.SerializeToUtf8Bytes(
                    new BankingRequest("close", Scope, --readSequence)) });
            }
            catch (Exception)
            {
                // Server idle expiry also retires abandoned conversations.
            }
        }
        Scope = Guid.Empty;
        Session = null;
        Display = null;
        Confirmation = null;
        Names = [];
    }



    //// Drops connection-local data on world exit, invalidating queued callbacks and never retrying money on reconnect.
    ////
    private void LeaveWorld()
    {
        generation++;
        pending = null;
        ReleaseConversation();
        receipts.Reset();
        acknowledgments.Clear();
        queuedAcknowledgments.Clear();
        dialog.TryClose();
        dialog.Reset();
    }



    //// Removes engine callbacks and disposes the native dialog without claiming to cancel previously sent money.
    ////
    public void Dispose()
    {
        if (disposed) return;
        LeaveWorld();
        disposed = true;
        api.Event.MouseDown -= MouseDown;
        api.Event.LeaveWorld -= LeaveWorld;
        api.Event.UnregisterGameTickListener(tickListener);
        dialog.Dispose();
    }



}
