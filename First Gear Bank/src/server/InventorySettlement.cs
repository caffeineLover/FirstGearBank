/*
 * Converts an exact six-decimal bank request into a preflighted mutation of the authenticated player's own inventory.
 * Only hotbar and backpack storage participate; open chests, equipment, creative slots, and the mouse cursor do not.
 * Accepted denominations are explicit registered item codes, never an item's display name or a client-supplied value.
 * Rusty withdrawals use whole gears and native quarters; temporal withdrawals require whole gears.
 * The Max query reuses the same slot capture and withdrawal planner under the same gate.  It examines the four possible
 * rusty quarter remainders separately because insertability is not monotonic when a fractional stack needs its own slot.
 *
 * Preparation retains a server-owned gate and captures cloned before/after stacks plus serialized evidence.  Apply
 * rechecks every participating slot before changing anything, then marks changed slots dirty for engine replication.
 * Rollback restores only slots this operation touched and refuses to overwrite an unexpected third-party mutation.
 * The gate serializes bank operations, not arbitrary mods: execution must remain synchronous on the game server thread.
 * Evidence supports live rollback; it is not proof of atomic inventory/world disk saves.
 */

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using FirstGearBank.Core;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace FirstGearBank.Server;

/// One authoritative slot snapshot and its independently prepared replacement, kept private to a retained transaction.
internal sealed record PlannedSlot(IInventory Inventory, int Index, ItemSlot Slot, ItemStack? Before, ItemStack? After);

/// Retained inventory transaction implementing the core's prepare/apply/rollback contract without partial deposits.
/// Disposal releases serialization only; it never commits money, silently rolls back success, or spawns loose items.
internal sealed class InventorySettlement : IInventoryChange
{
    public const string NativeQuarter = "firstgearbank:gear-quarter";
    private readonly object gate;
    private readonly List<PlannedSlot> slots;
    private readonly HashSet<int> touched = new();
    private bool disposed;
    public InventoryManifest Manifest { get; }
    public long VerifiedUnits { get; }



    //// Takes ownership of an already-entered gate and computes immutable evidence from the completed slot plan.
    //// The factory releases the gate itself if planning or evidence allocation fails before ownership transfers.
    ////
    private InventorySettlement(object gate, List<PlannedSlot> slots, long units)
    {
        this.gate = gate;
        this.slots = slots;
        VerifiedUnits = units;
        var deltas = slots.Where(slot => Serialize(slot.Before) != Serialize(slot.After))
            .Select(slot => new InventoryDelta(slot.Inventory.InventoryID, slot.Index,
                Serialize(slot.Before), Serialize(slot.After))).ToImmutableArray();
        Manifest = new(Fingerprint(slots, false), Fingerprint(slots, true), deltas);
    }



    //// Locks and snapshots eligible personal slots, then plans the entire requested amount without changing the game.
    //// Invalid denominations, unavailable registered items, insufficient holdings, or insufficient capacity reject.
    ////
    public static InventorySettlement Prepare(object gate, IServerPlayer player, IWorldAccessor world,
        Currency currency, long units, bool withdrawal)
    {
        var quantum = currency == Currency.Rusty ? Money.Scale / 4 : Money.Scale;
        if (!Enum.IsDefined(currency) || units <= 0 || units > Money.MaximumUnits || units % quantum != 0)
            throw new BankException(BankError.InvalidDenomination);
        Monitor.Enter(gate);
        try
        {
            var slots = Capture(player);
            if (withdrawal) PlanWithdrawal(slots, world, currency, units);
            else PlanDeposit(slots, currency, units);
            // Derive the amount from actual planned denomination differences, independently of the request.
            var delta = slots.Sum(slot => Value(slot.After, currency) - Value(slot.Before, currency));
            if (delta != (withdrawal ? units : -units)) throw new BankException(BankError.InventoryUnavailable);
            return new(gate, slots, Math.Abs(delta));
        }
        catch
        {
            Monitor.Exit(gate);
            throw;
        }
    }



    //// Finds the largest account-bounded withdrawal that the current personal inventory can receive exactly.
    //// The query holds the settlement gate but changes only cloned stacks.  Each possible fractional rusty remainder
    //// gets an independent monotonic whole-gear search so lack of a quarter slot cannot hide a larger whole result.
    ////
    public static long MaximumWithdrawalUnits(object gate, IServerPlayer player, IWorldAccessor world,
        Currency currency, long availableUnits)
    {
        if (!Enum.IsDefined(currency) || availableUnits <= 0) throw new BankException(BankError.InvalidAmount);
        var quantum = currency == Currency.Rusty ? Money.Scale / 4 : Money.Scale;
        availableUnits = Math.Min(Money.MaximumUnits, availableUnits / quantum * quantum);
        if (availableUnits <= 0) throw new BankException(BankError.InvalidAmount);
        lock (gate)
        {
            var captured = Capture(player);
            var best = 0L;
            var remainderCount = currency == Currency.Rusty ? 4 : 1;
            for (var remainder = 0; remainder < remainderCount; remainder++)
            {
                var fractional = checked(remainder * quantum);
                if (fractional > availableUnits) continue;
                var low = 0L;
                var high = (availableUnits - fractional) / Money.Scale;
                while (low <= high)
                {
                    var middle = low + (high - low) / 2;
                    var candidate = checked(middle * Money.Scale + fractional);
                    if (candidate > 0 && CanPlanWithdrawal(captured, world, currency, candidate))
                    {
                        best = Math.Max(best, candidate);
                        low = middle + 1;
                    }
                    else high = middle - 1;
                }
            }
            if (best <= 0) throw new BankException(BankError.InventoryUnavailable);
            return best;
        }
    }



    //// Captures the only personal inventories eligible for banking while preserving stable slot order.
    ////
    private static List<PlannedSlot> Capture(IServerPlayer player)
    {
        var slots = new List<PlannedSlot>();
        foreach (var name in new[] { GlobalConstants.hotBarInvClassName, GlobalConstants.backpackInvClassName })
        {
            var inventory = player.InventoryManager.GetOwnInventory(name);
            if (inventory is null) continue;
            if (inventory.Count > 4096) throw new BankException(BankError.InventoryUnavailable);
            for (var index = 0; index < inventory.Count; index++)
            {
                var slot = inventory[index];
                if (slot is null) continue;
                var before = slot.Itemstack?.Clone();
                slots.Add(new(inventory, index, slot, before, before?.Clone()));
            }
        }
        return slots;
    }



    //// Applies one candidate only to fresh clones and converts ordinary capacity rejection into a false probe.
    ////
    private static bool CanPlanWithdrawal(List<PlannedSlot> captured, IWorldAccessor world, Currency currency, long units)
    {
        var probe = captured.Select(entry => entry with
        {
            Before = entry.Before?.Clone(),
            After = entry.Before?.Clone()
        }).ToList();
        try
        {
            PlanWithdrawal(probe, world, currency, units);
            return true;
        }
        catch (BankException error) when (error.Error == BankError.InventoryUnavailable)
        {
            return false;
        }
    }



    //// Removes exact existing denominations from clones, preferring whole rusty gears before quarters.
    //// Nothing is published if the requested amount cannot be represented by the player's actual item holdings.
    ////
    private static void PlanDeposit(List<PlannedSlot> slots, Currency currency, long units)
    {
        var remaining = units;
        foreach (var denomination in currency == Currency.Rusty ? new[] { Money.Scale, Money.Scale / 4 } : [Money.Scale])
        {
            for (var index = 0; index < slots.Count && remaining > 0; index++)
            {
                var entry = slots[index];
                var stack = entry.After;
                if (stack is null || UnitValue(stack, currency) != denomination || !entry.Slot.CanTake()) continue;
                var count = (int)Math.Min(stack.StackSize, remaining / denomination);
                if (count == 0) continue;
                stack.StackSize -= count;
                slots[index] = entry with { After = stack.StackSize == 0 ? null : stack };
                remaining -= count * denomination;
            }
        }
        if (remaining != 0) throw new BankException(BankError.InventoryUnavailable);
    }



    //// Plans canonical withdrawal stacks, reserving an empty quarter slot when fractional change requires it.
    //// Unfilled whole-gear value can use quarter capacity; no items are dropped when the inventory is full.
    ////
    private static void PlanWithdrawal(List<PlannedSlot> slots, IWorldAccessor world, Currency currency, long units)
    {
        var whole = world.GetItem(new AssetLocation(currency == Currency.Rusty ? "game:gear-rusty" : "game:gear-temporal"));
        if (whole is null) throw new BankException(BankError.InventoryUnavailable);
        var wholeStack = new ItemStack(whole, 1);
        ItemStack? quarter = null;
        var reserved = -1;
        if (currency == Currency.Rusty)
        {
            var item = world.GetItem(new AssetLocation(NativeQuarter));
            if (item is not null) quarter = new ItemStack(item, 1);
            if (units % Money.Scale != 0)
            {
                if (quarter is null) throw new BankException(BankError.InventoryUnavailable);
                var quarterRoom = slots.Where(slot => slot.After is not null)
                    .Sum(slot => Capacity(slot, quarter, world));
                if (quarterRoom < units % Money.Scale / (Money.Scale / 4))
                    reserved = slots.FindLastIndex(slot => slot.After is null && Capacity(slot, quarter, world) > 0);
            }
        }
        var wholeGiven = Fill(slots, wholeStack, units / Money.Scale, world, reserved);
        var remaining = units - wholeGiven * Money.Scale;
        if (remaining > 0 && quarter is not null)
            remaining -= Fill(slots, quarter, remaining / (Money.Scale / 4), world, -1) * (Money.Scale / 4);
        if (remaining != 0) throw new BankException(BankError.InventoryUnavailable);
    }



    //// Fills compatible existing stacks before empty slots, honoring both collectible and slot stack limits.
    //// Only clone contents change; the returned item count is used to prove the eventual monetary delta.
    ////
    private static long Fill(List<PlannedSlot> slots, ItemStack item, long count, IWorldAccessor world, int reserved)
    {
        var remaining = count;
        foreach (var emptyPass in new[] { false, true })
            for (var index = 0; index < slots.Count && remaining > 0; index++)
            {
                var entry = slots[index];
                if (index == reserved || (entry.After is null) != emptyPass) continue;
                var quantity = (int)Math.Min(remaining, Capacity(entry, item, world));
                if (quantity <= 0) continue;
                var stack = entry.After?.Clone() ?? item.Clone();
                stack.StackSize = (entry.After?.StackSize ?? 0) + quantity;
                slots[index] = entry with { After = stack };
                remaining -= quantity;
            }
        return count - remaining;
    }



    //// Computes safe room for a canonical stack without merging differently attributed currency items.
    //// Engine slot acceptance is evaluated against the real slot while clone counts account for earlier plan steps.
    ////
    private static int Capacity(PlannedSlot entry, ItemStack item, IWorldAccessor world)
    {
        if (entry.Inventory.PutLocked || !entry.Slot.CanHold(new DummySlot(item)) ||
            (entry.After is not null && !entry.After.Equals(world, item))) return 0;
        var limit = Math.Min(item.Collectible.MaxStackSize, entry.Slot.MaxSlotStackSize);
        return Math.Max(0, Math.Min(limit - (entry.After?.StackSize ?? 0),
            entry.Slot.GetRemainingSlotSpace(item) - ((entry.After?.StackSize ?? 0) - (entry.Before?.StackSize ?? 0))));
    }



    //// Assigns only explicitly recognized collectible codes to banking denominations, ignoring arbitrary attributes.
    //// Native quarters remain accepted permanently; unverified external gear fragments have no monetary value here.
    ////
    private static long UnitValue(ItemStack stack, Currency currency)
    {
        var code = stack.Collectible.Code.ToString();
        if (currency == Currency.Temporal) return code == "game:gear-temporal" ? Money.Scale : 0;
        return code == "game:gear-rusty" ? Money.Scale : code == NativeQuarter ? Money.Scale / 4 : 0;
    }



    //// Values a captured slot in fixed-point units using checked multiplication, never floating-point item counts.
    ////
    private static long Value(ItemStack? stack, Currency currency)
    {
        return stack is null ? 0 : checked(UnitValue(stack, currency) * stack.StackSize);
    }



    //// Produces opaque server-only stack evidence including persistent attributes; an empty slot uses an empty string.
    ////
    private static string Serialize(ItemStack? stack)
    {
        return stack is null ? "" : Convert.ToBase64String(stack.ToBytes());
    }



    //// Hashes ordered inventory identities, slot numbers, and stack bytes to detect slot substitution or drift.
    ////
    private static string Fingerprint(List<PlannedSlot> slots, bool after)
    {
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(slots.Select(slot =>
            new InventoryDelta(slot.Inventory.InventoryID, slot.Index, Serialize(after ? slot.After : slot.Before), "")))));
    }



    //// Rechecks the entire prepared inventory view, then publishes the approved replacements and sync notifications.
    //// Touched positions are recorded before assignment so rollback can recover an exception in a slot setter.
    ////
    public void Apply()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        foreach (var entry in slots)
            if (!ReferenceEquals(entry.Inventory[entry.Index], entry.Slot) ||
                Serialize(entry.Slot.Itemstack) != Serialize(entry.Before))
                throw new BankException(BankError.InventoryUnavailable);
        for (var index = 0; index < slots.Count; index++)
        {
            var entry = slots[index];
            if (Serialize(entry.Before) == Serialize(entry.After)) continue;
            touched.Add(index);
            entry.Slot.Itemstack = entry.After?.Clone();
            entry.Slot.MarkDirty();
        }
        foreach (var entry in slots)
            if (Serialize(entry.Slot.Itemstack) != Serialize(entry.After))
                throw new BankException(BankError.InventoryUnavailable);
    }



    //// Restores touched slots only when they still match this operation's before or after evidence.
    //// Unexpected callback-side changes reject rollback and let the coordinator report an ordinary inventory error.
    ////
    public void Rollback()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        foreach (var index in touched)
        {
            var entry = slots[index];
            var current = Serialize(entry.Slot.Itemstack);
            if (!ReferenceEquals(entry.Inventory[entry.Index], entry.Slot) ||
                (current != Serialize(entry.Before) && current != Serialize(entry.After)))
                throw new BankException(BankError.InventoryUnavailable);
        }
        foreach (var index in touched)
        {
            var entry = slots[index];
            entry.Slot.Itemstack = entry.Before?.Clone();
            entry.Slot.MarkDirty();
        }
        foreach (var index in touched)
            if (Serialize(slots[index].Slot.Itemstack) != Serialize(slots[index].Before))
                throw new BankException(BankError.InventoryUnavailable);
    }



    //// Releases the retained bank-inventory gate exactly once without undoing the coordinator's chosen outcome.
    ////
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Monitor.Exit(gate);
    }



}
