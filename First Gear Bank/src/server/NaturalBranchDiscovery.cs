/*
 * Converts vanilla generated-structure metadata into canonical natural-branch source observations.  World generation
 * and ordinary loaded-column backfill both use the same bounded chunk scan and recognition allowlist; no trader entity,
 * offline player database, global structure scan, or force-load API participates.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace FirstGearBank.Server;

/// Distinguishes first-generation observation from opt-in existing-world loaded-column backfill.
internal enum DiscoveryMode { WorldGeneration, Backfill }

/// Public-API structure observer owned and disposed by the natural branch lifecycle.
internal sealed class NaturalBranchDiscovery : IDisposable
{
    private static readonly string[] VanillaTraderPrefixes =
    [
        "trader-forest", "trader-shallow", "trader-hotdry", "trader-repurposed", "trader-cold",
        "trader-plains", "trader-outpost", "trader-treehouse"
    ];
    private readonly ICoreServerAPI api;
    private readonly NaturalBranchLifecycle lifecycle;
    private readonly Dictionary<string, Vec2i> visited = new(StringComparer.Ordinal);
    private readonly long backfillTick;
    private bool disposed;



    //// Registers late worldgen and bounded player-column observation without requesting any chunk load.
    ////
    internal NaturalBranchDiscovery(ICoreServerAPI api, NaturalBranchLifecycle lifecycle)
    {
        this.api = api;
        this.lifecycle = lifecycle;
        api.Event.ChunkColumnGeneration(Worldgen, EnumWorldGenPass.Done, "standard");
        backfillTick = api.Event.RegisterGameTickListener(Backfill, 2000);
    }



    //// Queues worker-thread worldgen metadata inspection onto the authoritative server thread.
    ////
    private void Worldgen(IChunkColumnGenerateRequest request)
    {
        if (disposed) return;
        var x = request.ChunkX;
        var z = request.ChunkZ;
        api.Event.EnqueueMainThreadTask(() => Scan(x, z, DiscoveryMode.WorldGeneration),
            "firstgearbank-natural-worldgen");
    }



    //// Evaluates one already-loaded column only when an online player newly enters it and backfill is enabled.
    ////
    private void Backfill(float elapsedSeconds)
    {
        if (disposed) return;
        if (!lifecycle.BackfillEnabled) { visited.Clear(); return; }
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var player in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            present.Add(player.PlayerUID);
            var pos = player.Entity.Pos.AsBlockPos;
            if (pos.dimension != 0) { visited.Remove(player.PlayerUID); continue; }
            var column = new Vec2i(FloorChunk(pos.X), FloorChunk(pos.Z));
            if (visited.TryGetValue(player.PlayerUID, out var prior) && prior.Equals(column)) continue;
            visited[player.PlayerUID] = column;
            if (api.World.IsFullyLoadedChunk(pos)) Scan(column.X, column.Y, DiscoveryMode.Backfill);
        }
        foreach (var player in visited.Keys.Where(player => !present.Contains(player)).ToArray()) visited.Remove(player);
    }



    //// Walks generated structures intersecting one loaded column and deduplicates overlapping metadata callbacks.
    ////
    private void Scan(int chunkX, int chunkZ, DiscoveryMode mode)
    {
        if (disposed) return;
        var found = new Dictionary<string, NaturalStructureSource>(StringComparer.Ordinal);
        var from = new BlockPos(chunkX * 32, 0, chunkZ * 32, 0);
        var to = new BlockPos(chunkX * 32 + 31, api.WorldManager.MapSizeY - 1, chunkZ * 32 + 31, 0);
        api.World.BlockAccessor.WalkStructures(from, to, structure =>
        {
            if (!TryRecognize(structure, out var source)) return;
            found[source.Id] = source;
        });
        foreach (var source in found.Values.OrderBy(source => source.Id, StringComparer.Ordinal))
            lifecycle.Observe(source, mode);
        lifecycle.RetryAwaitingNear(chunkX * 32, chunkZ * 32, chunkX * 32 + 31, chunkZ * 32 + 31);
    }



    //// Accepts only supported vanilla trader group/code metadata with ordered nonempty bounds.
    ////
    internal bool TryRecognize(GeneratedStructure structure, out NaturalStructureSource source)
    {
        source = null!;
        var code = structure.Code?.Trim() ?? "";
        var bounds = structure.Location;
        if (structure.Group != "trader" || bounds is null || !code.StartsWith("game:", StringComparison.Ordinal) ||
            !VanillaTraderPrefixes.Any(prefix => code.Equals(prefix, StringComparison.Ordinal) ||
                code.EndsWith("/" + prefix, StringComparison.Ordinal)) ||
            bounds.X2 <= bounds.X1 || bounds.Y2 <= bounds.Y1 || bounds.Z2 <= bounds.Z1) return false;
        var geometry = new BranchBounds(0, bounds.X1, bounds.Y1, bounds.Z1,
            bounds.X2 - 1, bounds.Y2 - 1, bounds.Z2 - 1);
        var provisional = new NaturalStructureSource("", code, geometry);
        var id = NaturalBranchIdentity.SourceId(api.WorldManager.SaveGame.SavegameIdentifier, provisional);
        source = provisional with { Id = id };
        return true;
    }



    //// Stops player-column observation; registered worldgen delegates become harmless no-ops after disposal.
    ////
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        visited.Clear();
        api.Event.UnregisterGameTickListener(backfillTick);
    }



    //// Uses mathematical floor so negative block coordinates retain the engine's chunk-column convention.
    ////
    private static int FloorChunk(int coordinate)
    {
        return (int)Math.Floor(coordinate / 32d);
    }



}
