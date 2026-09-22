/*
 * Defines the version-one transport boundary and bounded admission queue for authenticated banking requests.
 * The engine supplies the sender; request bodies contain only action intent, an opaque conversation nonce, sequence,
 * amount/name selection, and optional confirmation tokens.  No player UID, balance, interest rate, or permission bit
 * is accepted from the client.  The server host normalizes intent into core commands after execution-time checks.
 *
 * One protobuf envelope carries bounded UTF-8 JSON.  Both sides register this exact type on firstgearbank-v1; the
 * client ledger uses it without constructing core authority.  Limits are checked before JSON parsing or queueing,
 * with per-connection token buckets and a global queued-work cap.  The engine deserializes the outer envelope before
 * this handler, so these limits do not replace Vintage Story's transport-level packet protections.
 * Responses must be explicitly player-safe DTOs; snapshots and persisted core sections must never enter Send.
 */

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProtoBuf;
using Vintagestory.API.Server;

namespace FirstGearBank.Server;

/// Shared version-one transport envelope containing UTF-8 JSON, registered identically on client and server.
/// The server permits at most four KiB of incoming JSON and 128 KiB of outgoing display data per response.
[ProtoContract]
public sealed class BankingPacket
{
    [ProtoMember(1)]
    public byte[] Data { get; set; } = [];
}

/// Disposable UI intent with no account identity or financial authority; numeric money is submitted as decimal text.
internal sealed record BankingRequest(string Action, Guid Scope = default, long Sequence = 0, long BankerEntity = 0,
    string? Amount = null, string? Name = null, string Currency = "Rusty", Guid Token = default,
    int TenorMonths = 0, int Offset = 0, int Limit = 25);

/// Player-safe reply echoing conversation and sequence, with named status and an explicitly selected view body.
internal sealed record BankingReply(Guid Scope, long Sequence, string Status, object? Body = null);

/// Mutable network admission bucket, isolated behind the protocol gate and discarded when the connection closes.
internal sealed record AdmissionBucket(IServerPlayer Player, double Tokens, long Timestamp, int Pending);

/// Network adapter that bounds work before moving it onto the authoritative game thread.
/// It owns no account state and delegates all business decisions to a borrowed server-thread request handler.
internal sealed class BankingProtocol : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, AdmissionBucket> admission = new(StringComparer.Ordinal);
    private readonly ICoreServerAPI api;
    private readonly IServerNetworkChannel channel;
    private readonly Action<IServerPlayer, BankingRequest> dispatch;
    private readonly ServerDiagnostics log;
    private int pending;
    private volatile bool disposed;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 12, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };



    //// Registers the shared packet contract and captures the host's server-thread dispatcher.
    //// Construction occurs once in StartServerSide; disposal turns already-queued callbacks into no-ops.
    ////
    public BankingProtocol(ICoreServerAPI api, Action<IServerPlayer, BankingRequest> dispatch, ServerDiagnostics log)
    {
        this.api = api;
        this.dispatch = dispatch;
        this.log = log;
        channel = api.Network.RegisterChannel("firstgearbank-v1").RegisterMessageType<BankingPacket>();
        channel.SetMessageHandler<BankingPacket>(Receive);
    }



    //// Applies cheap byte and frequency bounds before queueing work; repeated floods are dropped without log spam.
    //// Each connection receives a burst of twelve requests and six more per real second, with four outstanding tasks.
    ////
    private void Receive(IServerPlayer player, BankingPacket packet)
    {
        if (packet.Data is not { Length: > 0 and <= 4096 }) return;
        lock (gate)
        {
            if (disposed || pending >= 256) return;
            var now = Environment.TickCount64;
            var bucket = admission.GetValueOrDefault(player.PlayerUID);
            if (bucket is null || !ReferenceEquals(bucket.Player, player))
            {
                if (admission.Count >= 4096) return;
                bucket = new(player, 12, now, 0);
            }
            var tokens = Math.Min(12, bucket.Tokens + Math.Max(0, now - bucket.Timestamp) * .006);
            if (tokens < 1 || bucket.Pending >= 4) return;
            admission[player.PlayerUID] = bucket with { Tokens = tokens - 1, Timestamp = now, Pending = bucket.Pending + 1 };
            pending++;
        }
        // Copy the bounded payload before leaving the engine callback; transport buffers are not trusted ownership.
        var bytes = (byte[])packet.Data.Clone();
        api.Event.EnqueueMainThreadTask(() => DispatchQueued(player, bytes), "firstgearbank-request");
    }



    //// Reauthenticates the queued connection, parses strict JSON, and releases admission accounting on every path.
    //// Malformed requests receive only a generic status; raw input and internal exception details never reach clients.
    ////
    private void DispatchQueued(IServerPlayer player, byte[] bytes)
    {
        try
        {
            if (disposed || player.ConnectionState != EnumClientState.Playing ||
                !ReferenceEquals(api.World.PlayerByUid(player.PlayerUID), player)) return;
            var request = JsonSerializer.Deserialize<BankingRequest>(bytes, JsonOptions);
            if (request is null || request.Action is null || request.Action.Length > 24 ||
                request.Amount?.Length > 32 || request.Name?.Length > 64 || request.Currency is null ||
                request.Currency.Length > 16 || request.Offset < 0 || request.Limit is < 1 or > 50)
                throw new JsonException();
            dispatch(player, request);
        }
        catch (JsonException)
        {
            Send(player, new(Guid.Empty, 0, "InvalidRequest"));
        }
        catch (Exception error)
        {
            log.Write("CRIT", "network", $"Request dispatch failed ({error.GetType().Name}).");
            Send(player, new(Guid.Empty, 0, "ServiceUnavailable"));
        }
        finally
        {
            lock (gate)
            {
                pending--;
                if (admission.TryGetValue(player.PlayerUID, out var bucket) && ReferenceEquals(bucket.Player, player))
                    admission[player.PlayerUID] = bucket with { Pending = Math.Max(0, bucket.Pending - 1) };
            }
        }
    }



    //// Serializes a host-selected safe reply and rejects oversized display bodies without truncating money fields.
    //// A disconnected client does not acknowledge a notice; durable outbox state stays in the coordinator.
    ////
    public void Send(IServerPlayer player, BankingReply reply)
    {
        if (disposed || player.ConnectionState != EnumClientState.Playing) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(reply);
        if (bytes.Length > 131_072)
            bytes = JsonSerializer.SerializeToUtf8Bytes(new BankingReply(reply.Scope, reply.Sequence, "ResponseTooLarge"));
        channel.SendPacket(new BankingPacket { Data = bytes }, player);
    }



    //// Drops disconnected admission metadata so previous connection tokens cannot authorize a replacement connection.
    ////
    public void Disconnect(IServerPlayer player)
    {
        lock (gate) admission.Remove(player.PlayerUID);
    }



    //// Stops accepting or dispatching work during shutdown; the game owns the registered channel itself.
    ////
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            admission.Clear();
        }
    }



}
