/*
 * Maintains ephemeral conversation authority between authenticated players and server-registered Banker entities.
 * Content code registers a Banker only after validating its branch and supplies an execution-time availability check.
 * The registry permits one loaded Banker per branch and one conversation per player.  No client packet can register
 * a Banker, choose an account owner, bypass a branch cutoff, or revive a session from an earlier connection.
 *
 * Each use rechecks the entity instance, life state, player connection, dimension, distance, branch callback,
 * and idle lifetime.  The host closes expired/disconnected conversations in the core to invalidate associated tokens.
 * Branch topology, spawning, protection, and durable vacancy timers belong to content/lifecycle integration, not this
 * ephemeral access registry; registration itself is deliberately not proof that those systems have been implemented.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using FirstGearBank.Core;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace FirstGearBank.Server;

/// Trusted content registration tying one loaded Banker instance to a branch and a live availability predicate.
internal sealed record BankerRegistration(Guid Branch, Entity Entity, Func<bool> Available);

/// Short-lived conversation authority owned by the server, with a real-second idle deadline and captured connection.
internal sealed record BankerConversation(IServerPlayer Player, long EntityId, Guid Scope, decimal ExpiresAtSeconds,
    StatementPrintState? Print);

/// Server-thread access registry shared by request dispatch and the core's execution-time session validation callback.
/// Entries never persist across restarts; reconnecting players must obtain fresh conversation scopes.
internal sealed class BankerSessions
{
    private readonly ICoreServerAPI api;
    private readonly Action<IServerPlayer, Guid> closed;
    private readonly Dictionary<long, BankerRegistration> bankers = new();
    private readonly Dictionary<string, BankerConversation> conversations = new(StringComparer.Ordinal);



    //// Borrows the live server world and a close-notification callback; persistent branch ownership stays elsewhere.
    ////
    public BankerSessions(ICoreServerAPI api, Action<IServerPlayer, Guid> closed)
    {
        this.api = api;
        this.closed = closed;
    }



    //// Registers one already-validated loaded Banker from trusted content code, rejecting duplicate branch ownership.
    //// The predicate must be inexpensive, side-effect free, and include any branch damage or availability cutoff.
    ////
    public void Register(Guid branch, Entity banker, Func<bool> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        if (branch == Guid.Empty || banker.EntityId <= 0 || !banker.Alive ||
            !ReferenceEquals(api.World.GetEntityById(banker.EntityId), banker) ||
            bankers.Values.Any(entry => entry.Branch == branch && entry.Entity.EntityId != banker.EntityId))
            throw new BankException(BankError.InvalidSession);
        bankers[banker.EntityId] = new(branch, banker, available);
    }



    //// Removes an unloaded, dead, replaced, or otherwise unavailable Banker and invalidates its conversations.
    //// Content lifecycle code calls this before removing the corresponding loaded entity registration.
    ////
    public void Unregister(long entityId, BankingCoordinator bank)
    {
        bankers.Remove(entityId);
        foreach (var conversation in conversations.Values.Where(entry => entry.EntityId == entityId).ToArray())
            Close(conversation.Player.PlayerUID, bank);
    }



    //// Opens a scope only after authenticating a nearby registered Banker, closing the player's previous scope first.
    //// Each player receives an independent nonce even when several players use the same Banker simultaneously.
    ////
    public Guid Open(IServerPlayer player, long entityId, decimal now, BankingCoordinator bank)
    {
        if (!CanUse(player, entityId)) throw new BankException(BankError.InvalidSession);
        Close(player.PlayerUID, bank);
        var scope = bank.OpenConversation(player.PlayerUID);
        conversations[player.PlayerUID] = new(player, entityId, scope, now + 300, null);
        return scope;
    }



    //// Replaces any uncommitted print preview with a fresh token while preserving a consumed conversation allowance.
    //// The host supplies already-frozen player-safe data after its ordinary account and session checks.
    ////
    public StatementPrintState PreparePrint(string player, Guid scope, PrintedStatementData data)
    {
        if (!conversations.TryGetValue(player, out var conversation) || conversation.Scope != scope)
            throw new BankException(BankError.InvalidSession);
        if (conversation.Print?.Completed == true) throw new BankException(BankError.PrintAllowanceUsed);
        var prepared = new StatementPrintState(Guid.NewGuid(), data, false);
        conversations[player] = conversation with { Print = prepared };
        return prepared;
    }



    //// Returns the exact frozen preview owned by this conversation and token, including completed replay state.
    ////
    public StatementPrintState RequirePrint(string player, Guid scope, Guid token)
    {
        if (!conversations.TryGetValue(player, out var conversation) || conversation.Scope != scope ||
            conversation.Print is not { } print || print.Token != token)
            throw new BankException(BankError.ExpiredConfirmation);
        return print;
    }



    //// Consumes the conversation allowance only after the complete paper/output inventory exchange succeeds.
    ////
    public void CompletePrint(string player, Guid scope, Guid token)
    {
        var print = RequirePrint(player, scope, token);
        var conversation = conversations[player];
        conversations[player] = conversation with { Print = print with { Completed = true } };
    }



    //// Rechecks ownership, captured connection, timeout, and current world availability at execution time.
    //// Successful access extends only the idle lifetime; it does not change confirmation-token deadlines.
    ////
    public bool Valid(string player, Guid scope, decimal now)
    {
        if (!conversations.TryGetValue(player, out var entry) || entry.Scope != scope ||
            entry.ExpiresAtSeconds <= now || !CanUse(entry.Player, entry.EntityId)) return false;
        conversations[player] = entry with { ExpiresAtSeconds = now + 300 };
        return true;
    }



    //// Checks the authenticated connection and authoritative positions against the registered loaded entity instance.
    //// Six blocks is the adapter's conversation reach; different dimensions and nonfinite distances always reject.
    ////
    private bool CanUse(IServerPlayer player, long entityId)
    {
        if (player.ConnectionState != EnumClientState.Playing || player.Entity is null || !player.Entity.Alive ||
            !ReferenceEquals(api.World.PlayerByUid(player.PlayerUID), player) ||
            !bankers.TryGetValue(entityId, out var banker) || !banker.Entity.Alive ||
            !ReferenceEquals(api.World.GetEntityById(entityId), banker.Entity) ||
            player.Entity.Pos.Dimension != banker.Entity.Pos.Dimension) return false;
        var distance = player.Entity.Pos.SquareDistanceTo(banker.Entity.Pos);
        return double.IsFinite(distance) && distance <= 36 && banker.Available();
    }



    //// Closes the authenticated player's current conversation and lets the core discard its transient tokens.
    ////
    public void Close(string player, BankingCoordinator bank)
    {
        if (conversations.Remove(player, out var entry))
        {
            bank.CloseConversation(entry.Scope);
            closed(entry.Player, entry.Scope);
        }
    }



    //// Closes every ephemeral conversation after world-level recovery invalidates all core scopes and tokens.
    ////
    public void CloseAll(BankingCoordinator bank)
    {
        foreach (var player in conversations.Keys.ToArray()) Close(player, bank);
    }



    //// Removes invalid ephemeral conversations during the host's low-frequency maintenance callback.
    //// This examines only active sessions, never offline accounts or the world's entity population.
    ////
    public void Prune(decimal now, BankingCoordinator bank)
    {
        foreach (var entry in conversations.Values.ToArray())
            if (entry.ExpiresAtSeconds <= now || !CanUse(entry.Player, entry.EntityId)) Close(entry.Player.PlayerUID, bank);
    }



}
