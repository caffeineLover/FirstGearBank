/*
 * Detects split-save disagreements between committed bank settlements and the player's saved physical inventory.
 * Vintage Story 1.22.7 serializes server player ModData and personal inventories in the same ServerWorldPlayerData
 * record.  A server-only 32-byte receipt chain is therefore advanced alongside each successful inventory mutation.
 * The bank's retained settlement tombstones independently reconstruct the expected chain on authenticated login.
 *
 * Each link binds world/player ownership through its predecessor, the exact request key, currency, direction, amount,
 * and before/after inventory manifest.  Client packets cannot provide or acknowledge these receipts.  The digest is a
 * consistency check on server-owned data, not a substitute for authentication or protection against edited save files.
 * Any bank-ahead, player-ahead, missing, malformed, or unresolved evidence closes that player's banking access without
 * guessing what to replay.  Permission-gated recovery later records an explicit physical finding before releasing
 * quarantine; this login path never makes that finding automatically.
 */

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using FirstGearBank.Core;
using Vintagestory.API.Server;

namespace FirstGearBank.Server;

/// Server-only receipt consistency operations shared by login reconciliation and retained inventory changes.
/// Receipt bytes stay in player ModData, while independent expected links come only from bank settlement authority.
internal static class InventoryRecoveryReceipts
{
    internal const string Key = "firstgearbank:settlement-receipt-v1";



    //// Reconstructs one player's expected receipt from successful settlement tombstones and compares saved evidence.
    //// Work follows that player's settlement history at login, never a per-tick scan of customer balances.
    ////
    public static bool Matches(IServerPlayer player, BankState state)
    {
        var entries = state.Settlements.Values.Where(entry => entry.Request.Player == player.PlayerUID).ToArray();
        if (entries.Any(entry => entry.Phase != SettlementPhase.Finalized)) return false;
        var successful = entries.Where(entry => !entry.Operations.IsEmpty)
            .OrderBy(entry => entry.PlannedRecords.IsEmpty ? long.MaxValue : entry.PlannedRecords[^1].Sequence).ToArray();
        var receipt = player.GetModdata(Key);
        if (successful.Length == 0) return receipt is null;
        var expected = Initial(state.WorldId, player.PlayerUID);
        foreach (var entry in successful)
        {
            if (entry.PlannedRecords.IsEmpty) return false;
            expected = Next(expected, entry.Request, entry.Currency, entry.Direction, entry.Units, entry.Manifest);
        }
        return receipt is { Length: 32 } && CryptographicOperations.FixedTimeEquals(receipt, expected);
    }



    //// Creates a domain-separated initial link bound to the exact world and authenticated player identity.
    //// Length-delimited JSON prevents ambiguous concatenation of identifiers.
    ////
    internal static byte[] Initial(string world, string player)
    {
        return SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { "firstgearbank/receipt/v1", world, player }));
    }



    //// Advances the digest with normalized settlement identity and exact slot evidence from the retained transaction.
    //// Both application and login reconstruction use this identical representation.
    ////
    internal static byte[] Next(byte[] previous, RequestKey request, Currency currency, CommandKind direction,
        long units, InventoryManifest manifest)
    {
        return SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Previous = Convert.ToHexString(previous), Request = request, Currency = currency,
            Direction = direction, Units = units, Manifest = manifest
        }));
    }



}

/// Retained transaction decorator coupling item mutation to a server-owned player receipt before bank publication.
/// The inner transaction continues to own the inventory gate until this wrapper is disposed by the coordinator.
internal sealed class ReceiptedInventoryChange : IInventoryChange
{
    private readonly IInventoryChange inner;
    private readonly IServerPlayer player;
    private readonly byte[]? previous;
    private readonly byte[] next;
    private bool receiptTouched;
    public InventoryManifest Manifest => inner.Manifest;
    public long VerifiedUnits => inner.VerifiedUnits;



    //// Captures the previous receipt and allocates the next link before any inventory or receipt mutation can occur.
    //// The host has already reconciled this connection; a malformed runtime receipt still rejects defensively.
    ////
    public ReceiptedInventoryChange(IInventoryChange inner, IServerPlayer player, string world, RequestKey request,
        Currency currency, long units, bool withdrawal)
    {
        this.inner = inner;
        this.player = player;
        previous = player.GetModdata(InventoryRecoveryReceipts.Key)?.ToArray();
        if (previous is not null && previous.Length != 32) throw new BankException(BankError.SettlementQuarantined);
        next = InventoryRecoveryReceipts.Next(previous ?? InventoryRecoveryReceipts.Initial(world, player.PlayerUID),
            request, currency, withdrawal ? CommandKind.Withdraw : CommandKind.Deposit, units, inner.Manifest);
    }



    //// Applies all preflighted items and then advances co-serialized receipt evidence before financial publication.
    //// A callback-side receipt change rejects instead of overwriting another operation's evidence.
    ////
    public void Apply()
    {
        var current = player.GetModdata(InventoryRecoveryReceipts.Key);
        if (!Same(current, previous)) throw new BankException(BankError.SettlementQuarantined);
        inner.Apply();
        receiptTouched = true;
        player.SetModdata(InventoryRecoveryReceipts.Key, next);
    }



    //// Restores item and receipt before-states on live failure, refusing any unexpected third-party receipt changes.
    //// Rollback uncertainty propagates so the core preserves the settlement as quarantined.
    ////
    public void Rollback()
    {
        var current = player.GetModdata(InventoryRecoveryReceipts.Key);
        if (receiptTouched && !Same(current, previous) && !Same(current, next))
            throw new BankException(BankError.SettlementQuarantined);
        inner.Rollback();
        if (!receiptTouched) return;
        if (previous is null) player.RemoveModdata(InventoryRecoveryReceipts.Key);
        else player.SetModdata(InventoryRecoveryReceipts.Key, previous);
    }



    //// Compares optional opaque receipt bytes without treating an absent receipt as a valid empty receipt.
    ////
    private static bool Same(byte[]? left, byte[]? right)
    {
        return left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
    }



    //// Releases the inner inventory gate after the coordinator has chosen success, rollback, or quarantine.
    ////
    public void Dispose()
    {
        inner.Dispose();
    }



}
