/*
 * Defines the two noncraftable blocks embedded in the natural-branch schematic.  The floor-textured anchor stores only
 * branch and source identity for placement reconciliation; it has no financial authority.  The decorative strongbox
 * intentionally exposes no inventory, loot, or interaction behavior and exists only as branch furnishing.
 */

using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace FirstGearBank.Server;

/// Hidden floor anchor whose block entity provides crash-reconciliation evidence.
public sealed class NaturalBankAnchorBlock : Block
{
}

/// Decorative locked strongbox with no container or loot behavior.
public sealed class NaturalBankStrongboxBlock : Block
{
}

/// Persistent nonfinancial branch/source identity attached to the hidden natural floor anchor.
public sealed class NaturalBankAnchorBlockEntity : BlockEntity
{
    internal Guid Branch { get; private set; }
    internal string SourceId { get; private set; } = "";



    //// Sets immutable placement evidence once; identical reconciliation writes are idempotent.
    ////
    internal void Bind(Guid branch, string sourceId)
    {
        if (branch == Guid.Empty || string.IsNullOrWhiteSpace(sourceId))
            throw new ArgumentException("Natural branch anchor identity is required.");
        if (Branch != Guid.Empty && (Branch != branch || SourceId != sourceId))
            throw new InvalidOperationException("Natural branch anchor has conflicting identity.");
        Branch = branch;
        SourceId = sourceId;
        MarkDirty(true);
    }



    //// Writes only reconciliation identity; account or NPC state never enters this block entity.
    ////
    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetString("branch", Branch.ToString("N"));
        tree.SetString("source", SourceId);
    }



    //// Restores bounded identity without treating malformed values as a valid reservation.
    ////
    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        Branch = Guid.TryParse(tree.GetString("branch"), out var branch) ? branch : Guid.Empty;
        var source = tree.GetString("source") ?? "";
        SourceId = source.Length <= 64 ? source : "";
    }



}
