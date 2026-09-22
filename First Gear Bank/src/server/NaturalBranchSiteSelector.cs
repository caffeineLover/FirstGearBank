/*
 * Searches deterministic 7x7 natural-branch candidates in the approved 20-to-50-block band around one trader
 * structure.  Candidate order is a stable hash order; every rejection uses loaded public world evidence, and missing
 * chunk data yields AwaitingData rather than a permanent skip.  The selector never loads or generates terrain.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace FirstGearBank.Server;

/// Site-search terminal state separating unavailable data from a fully exhausted deterministic search.
internal enum SiteSearchStatus { Ready, AwaitingData, Exhausted }

/// Exact selected geometry and rotation, or a non-placement search outcome.
internal sealed record SiteSearchResult(SiteSearchStatus Status, BranchBounds? Bounds = null,
    BankerCell? Anchor = null, IReadOnlyCollection<BankerCell>? Interior = null, int Rotation = 0,
    int NextCursor = 0);

/// Loaded-world deterministic candidate evaluator shared by new generation and visit-driven backfill.
internal sealed class NaturalBranchSiteSelector
{
    private readonly ICoreServerAPI api;
    private readonly FirstGearBankServer banking;
    private readonly BranchTopologyIndex topology;



    //// Borrows current loaded-world access and the combined branch-spacing view.
    ////
    internal NaturalBranchSiteSelector(ICoreServerAPI api, FirstGearBankServer banking, BranchTopologyIndex topology)
    {
        this.api = api;
        this.banking = banking;
        this.topology = topology;
    }



    //// Evaluates stable grid candidates without force-loading and returns the first wholly safe site.
    ////
    internal SiteSearchResult Find(NaturalStructureSource source, int startCursor)
    {
        var candidates = new List<(ulong Order, int X, int Z, int Rotation)>();
        for (var x = source.Bounds.MinX - 56; x <= source.Bounds.MaxX + 50; x += 4)
            for (var z = source.Bounds.MinZ - 56; z <= source.Bounds.MaxZ + 50; z += 4)
            {
                var maxX = x + 6;
                var maxZ = z + 6;
                var dx = maxX < source.Bounds.MinX ? source.Bounds.MinX - maxX :
                    source.Bounds.MaxX < x ? x - source.Bounds.MaxX : 0;
                var dz = maxZ < source.Bounds.MinZ ? source.Bounds.MinZ - maxZ :
                    source.Bounds.MaxZ < z ? z - source.Bounds.MaxZ : 0;
                var distance = Math.Sqrt((double)dx * dx + (double)dz * dz);
                if (distance is < 20 or > 50 || FloorChunk(x) != FloorChunk(maxX) || FloorChunk(z) != FloorChunk(maxZ))
                    continue;
                var rotation = FacingRotation(x + 3, z + 3, source.Bounds);
                candidates.Add((NaturalBranchIdentity.Candidate(api.WorldManager.SaveGame.Seed, source.Id,
                    x, z, rotation), x, z, rotation));
            }
        var ordered = candidates.OrderBy(row => row.Order).ThenBy(row => row.X).ThenBy(row => row.Z).ToArray();
        if (startCursor < 0 || startCursor > ordered.Length) throw new InvalidOperationException("Invalid site cursor.");
        for (var index = startCursor; index < ordered.Length; index++)
        {
            var candidate = ordered[index];
            var sample = new BlockPos(candidate.X + 3, 1, candidate.Z + 3, source.Bounds.Dimension);
            if (!api.World.IsFullyLoadedChunk(sample)) return new(SiteSearchStatus.AwaitingData, NextCursor: index);
            var y = api.World.BlockAccessor.GetTerrainMapheightAt(sample) + 1;
            var bounds = new BranchBounds(source.Bounds.Dimension, candidate.X, y, candidate.Z,
                candidate.X + 6, y + 4, candidate.Z + 6);
            if (topology.Conflicts(bounds, banking.MinimumBranchSpacing) || !Safe(bounds)) continue;
            var anchor = new BankerCell(candidate.X + 3, y, candidate.Z + 3, source.Bounds.Dimension);
            var interior = new List<BankerCell>(25);
            for (var x = candidate.X + 1; x <= candidate.X + 5; x++)
                for (var z = candidate.Z + 1; z <= candidate.Z + 5; z++)
                    interior.Add(new(x, y + 1, z, source.Bounds.Dimension));
            return new(SiteSearchStatus.Ready, bounds, anchor, interior, candidate.Rotation, index);
        }
        return new(SiteSearchStatus.Exhausted, NextCursor: ordered.Length);
    }



    //// Requires solid level support, replaceable clearance, no claims/block entities/liquids, and no structure overlap.
    ////
    private bool Safe(BranchBounds bounds)
    {
        var from = Position(bounds.MinX, bounds.MinY, bounds.MinZ, bounds.Dimension);
        var to = Position(bounds.MaxX, bounds.MaxY, bounds.MaxZ, bounds.Dimension);
        var structure = false;
        api.World.BlockAccessor.WalkStructures(from, to, _ => structure = true);
        if (structure) return false;
        for (var x = bounds.MinX; x <= bounds.MaxX; x++)
            for (var z = bounds.MinZ; z <= bounds.MaxZ; z++)
                for (var y = bounds.MinY - 1; y <= bounds.MaxY; y++)
                {
                    var pos = Position(x, y, z, bounds.Dimension);
                    if (api.World.Claims.Get(pos).Length > 0 || api.World.BlockAccessor.GetBlockEntity(pos) is not null)
                        return false;
                    var block = api.World.BlockAccessor.GetBlock(pos);
                    if (block.LiquidCode is not null) return false;
                    if (y == bounds.MinY - 1)
                    {
                        if (block.Replaceable >= 6000) return false;
                    }
                    else if (block.Replaceable < 6000) return false;
                }
        return true;
    }



    //// Chooses the base schematic rotation whose north-wall door faces the nearest source-bound direction.
    ////
    private static int FacingRotation(int centerX, int centerZ, BranchBounds source)
    {
        var targetX = Math.Clamp(centerX, source.MinX, source.MaxX);
        var targetZ = Math.Clamp(centerZ, source.MinZ, source.MaxZ);
        var dx = targetX - centerX;
        var dz = targetZ - centerZ;
        return Math.Abs(dx) > Math.Abs(dz) ? dx < 0 ? 3 : 1 : dz < 0 ? 0 : 2;
    }



    //// Uses mathematical floor so negative block coordinates map to the same chunk convention as the engine.
    ////
    private static int FloorChunk(int coordinate)
    {
        return (int)Math.Floor(coordinate / 32d);
    }



    //// Allocates a dimension-aware block position for public accessor calls.
    ////
    private static BlockPos Position(int x, int y, int z, int dimension)
    {
        return new(x, y, z, dimension);
    }



}
