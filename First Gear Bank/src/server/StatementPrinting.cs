/*
 * Freezes player-safe statement projections into immutable item data and inserts printed statements into inventory.
 * The formatter consumes only the core's public Statement view plus calendar display facts supplied by the
 * host; internal player identities, conversations, and balances outside the view never enter items.
 *
 * Printing holds the same host inventory gate used by monetary settlement.  Statement insertion is preflighted
 * against cloned hotbar/backpack stacks, then applied and rolled back on a live failure.  This
 * is an in-process atomicity guarantee, not a claim about ordering between separate engine save blobs.
 */

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using FirstGearBank.Core;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace FirstGearBank.Server;

/// One frozen certificate row included in a physical statement without account-owner authority.
internal sealed record PrintedCertificateData(long PrincipalUnits, long CurrentValueUnits, long MaturityUnits,
    decimal IssuedMonths, decimal MaturesMonths, double LockedYield);

/// One frozen history row whose names were already reduced to transaction-time display names by the core.
internal sealed record PrintedHistoryData(string Type, string Currency, long ChangeUnits, decimal EffectiveMonths,
    ImmutableArray<string> Names, bool WasCapped);

/// One frozen cumulative category represented as display text and an exact six-decimal-unit quantity.
internal sealed record PrintedTotalData(string Category, decimal Units);

/// Versioned bearer-readable statement payload stored in item attributes and safe to send to the owning client.
internal sealed record PrintedStatementData(int Version, string Heading, string BankName, string HolderName,
    decimal IssuedWorldDays, decimal AsOfMonths, string TimeBasis, long RustyUnits, long TemporalUnits,
    double ContinuousRate, double MonthlyEffectiveRate, double AnnualizedEffectiveYield,
    ImmutableArray<PrintedTotalData> Totals, ImmutableArray<PrintedCertificateData> Certificates,
    ImmutableArray<PrintedHistoryData> History, string Notice);

/// Session-owned preview token and frozen payload; completion makes retries observable without printing twice.
internal sealed record StatementPrintState(Guid Token, PrintedStatementData Data, bool Completed);

/// One captured inventory slot participating in the statement insertion plan.
internal sealed record StatementPrintSlot(IInventory Inventory, int Index, ItemSlot Slot, ItemStack? Before,
    ItemStack? After);

/// Retained inventory plan used only for physical statement printing.
internal sealed class StatementPrintTransaction : IDisposable
{
    private readonly object gate;
    private readonly List<StatementPrintSlot> slots;
    private readonly HashSet<int> touched = new();
    private bool disposed;



    //// Takes ownership of an entered inventory gate after the complete plan has been validated.
    ////
    private StatementPrintTransaction(object gate, List<StatementPrintSlot> slots)
    {
        this.gate = gate;
        this.slots = slots;
    }



    //// Plans insertion of one nonstackable statement into an empty personal slot without changing live slots.
    ////
    internal static StatementPrintTransaction Prepare(object gate, IServerPlayer player, IWorldAccessor world,
        PrintedStatementData data)
    {
        System.Threading.Monitor.Enter(gate);
        try
        {
            var slots = Capture(player);
            var item = world.GetItem(new AssetLocation("firstgearbank:bank-statement"));
            if (item is null) throw new BankException(BankError.InventoryUnavailable);
            var output = new ItemStack(item, 1);
            output.Attributes.SetString(PrintedStatementItem.DataAttribute, JsonSerializer.Serialize(data));
            var destination = slots.FindIndex(entry => entry.After is null && !entry.Inventory.PutLocked &&
                entry.Slot.CanHold(new DummySlot(output)) && entry.Slot.MaxSlotStackSize > 0);
            if (destination < 0) throw new BankException(BankError.InventoryUnavailable);
            slots[destination] = slots[destination] with { After = output };
            if (slots[destination].Slot is ItemSlotBagContent content)
            {
                var bagIndex = slots.FindIndex(slot => ReferenceEquals(slot.Inventory, slots[destination].Inventory) &&
                    slot.Index == content.BagIndex);
                if (bagIndex < 0) throw new BankException(BankError.InventoryUnavailable);
                var storedSlots = slots[bagIndex].After?.Attributes.GetTreeAttribute("backpack")?.GetTreeAttribute("slots");
                if (storedSlots is null) throw new BankException(BankError.InventoryUnavailable);
                storedSlots["slot-" + content.SlotIndex] = new ItemstackAttribute(output.Clone());
            }
            return new(gate, slots);
        }
        catch
        {
            System.Threading.Monitor.Exit(gate);
            throw;
        }
    }



    //// Captures eligible player-owned storage in stable inventory and slot order.
    ////
    private static List<StatementPrintSlot> Capture(IServerPlayer player)
    {
        var result = new List<StatementPrintSlot>();
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
                result.Add(new(inventory, index, slot, before, before?.Clone()));
            }
        }
        return result;
    }



    //// Rechecks the captured view before inserting the statement.
    ////
    internal void Apply()
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
            // Marking a bag-content slot dirty updates its equipped bag item without reloading the content slots.
            if (entry.Slot is ItemSlotBackpack) continue;
            touched.Add(index);
            entry.Slot.Itemstack = entry.After?.Clone();
            entry.Slot.MarkDirty();
        }
        foreach (var entry in slots)
            if (Serialize(entry.Slot.Itemstack) != Serialize(entry.After))
                throw new BankException(BankError.InventoryUnavailable);
    }



    //// Restores only slots still matching this operation's captured before/after evidence.
    ////
    internal void Rollback()
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
    }



    //// Serializes one stack including persistent attributes for exact callback-drift comparisons.
    ////
    private static string Serialize(ItemStack? stack)
    {
        return stack is null ? "" : Convert.ToBase64String(stack.ToBytes());
    }



    //// Releases the shared inventory gate exactly once without implicitly applying or reverting the plan.
    ////
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        System.Threading.Monitor.Exit(gate);
    }



}

/// Custom statement item that validates and renders its frozen bearer-readable payload without server access.
public sealed class PrintedStatementItem : Item
{
    internal const string DataAttribute = "firstgearbank:statement-v1";



    //// Reads a bounded version-one payload; malformed attributes remain nonauthoritative and display as unreadable.
    ////
    internal static PrintedStatementData? Read(ItemStack stack)
    {
        var json = stack.Attributes.GetString(DataAttribute);
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > 65_536) return null;
        try
        {
            var data = JsonSerializer.Deserialize<PrintedStatementData>(json);
            return data is { Version: 1 } && !string.IsNullOrWhiteSpace(data.HolderName) &&
                data.HolderName.Length <= 256 && data.History.Length <= 100 && data.Certificates.Length <= 1024 &&
                data.Totals.Length <= 256 ? data : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }



    //// Appends the complete immutable snapshot to the ordinary held-item help surface for any bearer.
    ////
    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        var data = inSlot.Itemstack is null ? null : Read(inSlot.Itemstack);
        if (data is null)
        {
            dsc.AppendLine(Lang.Get("firstgearbank:bank-statement-unreadable"));
            return;
        }
        dsc.AppendLine();
        dsc.AppendLine(data.Heading);
        dsc.AppendLine(data.BankName);
        dsc.AppendLine(Lang.Get("firstgearbank:bank-holder", data.HolderName));
        dsc.AppendLine(Lang.Get("firstgearbank:bank-statement-issued", data.IssuedWorldDays));
        dsc.AppendLine(Lang.Get("firstgearbank:bank-statement-balances",
            Format(data.RustyUnits), Format(data.TemporalUnits)));
        dsc.AppendLine(Lang.Get("firstgearbank:bank-statement-period", data.TimeBasis, data.AsOfMonths));
        dsc.AppendLine(Lang.Get("firstgearbank:bank-statement-rates", data.ContinuousRate,
            data.MonthlyEffectiveRate, data.AnnualizedEffectiveYield));
        foreach (var total in data.Totals)
            dsc.AppendLine(Lang.Get("firstgearbank:bank-statement-total", total.Category, Format(total.Units)));
        foreach (var certificate in data.Certificates)
            dsc.AppendLine(Lang.Get("firstgearbank:bank-statement-cd", Format(certificate.PrincipalUnits),
                Format(certificate.CurrentValueUnits), Format(certificate.MaturityUnits), certificate.MaturesMonths,
                certificate.LockedYield));
        foreach (var row in data.History)
            dsc.AppendLine(Lang.Get("firstgearbank:bank-statement-row", row.EffectiveMonths, row.Type,
                row.Currency, Format(row.ChangeUnits), string.Join(", ", row.Names)));
        dsc.AppendLine(data.Notice);
    }



    //// Formats exact bank units without applying account display rounding.
    ////
    private static string Format(decimal units)
    {
        return (units / Money.Scale).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
    }



}
