/*
 * Combines immutable active/reserved branch footprints from independent lifecycle owners for spacing decisions.
 * Charter and natural registries remain authoritative for their own state; this index is rebuilt from those snapshots
 * and performs no persistence, world mutation, or recovery.  All calls occur on the server thread.
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace FirstGearBank.Server;

/// Identifies the lifecycle that owns a topology footprint.
internal enum BranchKind { Charter, Natural }

/// Inclusive dimension-bound block box used for overlap and horizontal spacing checks.
internal sealed record BranchBounds(int Dimension, int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ);

/// One branch reservation visible to all placement authorities without exposing lifecycle-specific state.
internal sealed record BranchFootprint(Guid Branch, BranchKind Kind, BranchBounds Bounds, bool Active);

/// Server-thread combined view of active and reserved branch geometry.
internal sealed class BranchTopologyIndex
{
    private readonly Dictionary<BranchKind, Dictionary<Guid, BranchFootprint>> byKind = new();



    //// Replaces one owner's complete view atomically so retired rows cannot remain as phantom conflicts.
    ////
    internal void Replace(BranchKind kind, IEnumerable<BranchFootprint> footprints)
    {
        var next = footprints.Where(entry => entry.Kind == kind && entry.Branch != Guid.Empty && entry.Active)
            .ToDictionary(entry => entry.Branch);
        byKind[kind] = next;
    }



    //// Rejects exact box overlap or horizontal edge distance smaller than the configured spacing.
    ////
    internal bool Conflicts(BranchBounds candidate, int spacing, Guid? exceptBranch = null)
    {
        foreach (var footprint in byKind.Values.SelectMany(entries => entries.Values))
        {
            if (footprint.Branch == exceptBranch || footprint.Bounds.Dimension != candidate.Dimension) continue;
            var bounds = footprint.Bounds;
            var dx = candidate.MaxX < bounds.MinX ? (long)bounds.MinX - candidate.MaxX :
                bounds.MaxX < candidate.MinX ? (long)candidate.MinX - bounds.MaxX : 0;
            var dz = candidate.MaxZ < bounds.MinZ ? (long)bounds.MinZ - candidate.MaxZ :
                bounds.MaxZ < candidate.MinZ ? (long)candidate.MinZ - bounds.MaxZ : 0;
            var verticalOverlap = candidate.MinY <= bounds.MaxY && candidate.MaxY >= bounds.MinY;
            if (dx == 0 && dz == 0 && verticalOverlap) return true;
            if (spacing > 0 && (decimal)dx * dx + (decimal)dz * dz < (decimal)spacing * spacing) return true;
        }
        return false;
    }



}
