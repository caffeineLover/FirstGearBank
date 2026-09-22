/*
 * Owns deterministic natural-branch source decisions, reservation-before-mutation, schematic/anchor reconciliation,
 * public-use protection claims, immediate Banker homes, optional loaded-column backfill, and administrative retirement.
 * Source identity comes only from vanilla generated-structure metadata; trader entities never create or own branches.
 *
 * Each side effect follows a staged phase.  Matching evidence advances idempotently, missing loaded evidence resumes the
 * next step, and contradictory evidence quarantines the row without deleting blocks, claims, entities, or finance.
 */

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace FirstGearBank.Server;

/// Server-thread natural branch authority coordinated with topology and the existing Banker roster.
public sealed class NaturalBranchLifecycle : IDisposable
{
    private readonly ICoreServerAPI api;
    private readonly FirstGearBankServer banking;
    private readonly BankerLifecycle bankers;
    private readonly BranchTopologyIndex topology;
    private readonly ServerDiagnostics log;
    private readonly NaturalBranchDiscovery discovery;
    private readonly Dictionary<string, NaturalStructureSource> pending = new(StringComparer.Ordinal);
    private Dictionary<string, NaturalBranchSource> sources = new(StringComparer.Ordinal);
    private NaturalBranchStorage? storage;
    private NaturalBranchSiteSelector? selector;
    private NaturalBranchSchematic? schematic;
    private long tickListener;
    private int cursor;
    private bool loaded;
    private bool failed;
    private bool disposed;
    internal bool BackfillEnabled => banking.NaturalBackfillEnabled;



    //// Registers discovery before terrain generation and defers durable authority loading until RunGame.
    ////
    internal NaturalBranchLifecycle(ICoreServerAPI api, FirstGearBankServer banking, BankerLifecycle bankers,
        BranchTopologyIndex topology)
    {
        this.api = api;
        this.banking = banking;
        this.bankers = bankers;
        this.topology = topology;
        log = new(api);
        discovery = new(api, this);
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, Load);
        api.Event.GameWorldSave += Save;
        tickListener = api.Event.RegisterGameTickListener(Tick, 2000);
    }



    //// Restores source authority, prepares schematic services, publishes reservations, and replays pending observations.
    ////
    private void Load()
    {
        if (loaded || disposed) return;
        loaded = true;
        try
        {
            storage = new(api.WorldManager.SaveGame, log);
            var restored = storage.Load();
            if (restored is null) { failed = true; return; }
            sources = restored;
            selector = new(api, banking, topology);
            schematic = new(api);
            PublishTopology();
            foreach (var row in sources.Values.Where(source =>
                         source.Disposition == NaturalSourceDisposition.Decommissioned).ToArray())
                CleanupDecommissioned(row);
            foreach (var source in pending.Values.OrderBy(source => source.Id, StringComparer.Ordinal).ToArray())
                Observe(source, DiscoveryMode.WorldGeneration);
            pending.Clear();
            ReconcileOne();
            log.Write("INFO", "natural-lifecycle", "Natural branch registry loaded; deterministic discovery is active.");
        }
        catch (Exception error) { Disable(error); }
    }



    //// Records one source's permanent first roll, then searches only currently loaded candidate data.
    ////
    internal void Observe(NaturalStructureSource source, DiscoveryMode mode)
    {
        if (disposed || failed) return;
        if (!loaded)
        {
            pending[source.Id] = source;
            return;
        }
        try
        {
            if (!sources.TryGetValue(source.Id, out var row))
            {
                var probability = banking.NaturalBranchProbability;
                var roll = NaturalBranchIdentity.Roll(api.WorldManager.SaveGame.Seed, source.Id);
                var disposition = roll < probability ? NaturalSourceDisposition.AwaitingData :
                    NaturalSourceDisposition.NotSelected;
                row = new(source.Id, source.Code, source.Bounds, probability, roll, disposition, Guid.Empty,
                    null, null, [], 0, 0, 0, NaturalPlacementPhase.None);
                sources.Add(source.Id, row);
                Stage();
            }
            if (row.Disposition == NaturalSourceDisposition.AwaitingData) Evaluate(source);
        }
        catch (Exception error) { Disable(error); }
    }



    //// Reconsiders deferred sources only when an ordinary observed column lies within their bounded search neighborhood.
    //// This lets newly visited candidate chunks supply evidence without scanning all sources or loading terrain.
    ////
    internal void RetryAwaitingNear(int minX, int minZ, int maxX, int maxZ)
    {
        if (!loaded || failed || disposed) return;
        try
        {
            foreach (var row in sources.Values.Where(row => row.Disposition == NaturalSourceDisposition.AwaitingData &&
                         row.StructureBounds.MaxX + 64 >= minX && row.StructureBounds.MinX - 64 <= maxX &&
                         row.StructureBounds.MaxZ + 64 >= minZ && row.StructureBounds.MinZ - 64 <= maxZ)
                     .OrderBy(row => row.Id, StringComparer.Ordinal).ToArray())
                Evaluate(new(row.Id, row.StructureCode, row.StructureBounds));
        }
        catch (Exception error) { Disable(error); }
    }



    //// Converts loaded search evidence into a durable reservation, permanent skip, or retained deferral.
    ////
    private void Evaluate(NaturalStructureSource source)
    {
        var current = sources[source.Id];
        var result = selector!.Find(source, current.SearchCursor);
        if (result.Status == SiteSearchStatus.AwaitingData)
        {
            if (result.NextCursor != current.SearchCursor)
            {
                sources[source.Id] = current with { SearchCursor = result.NextCursor };
                Stage();
            }
            return;
        }
        if (result.Status == SiteSearchStatus.Exhausted)
        {
            sources[source.Id] = current with
            {
                Disposition = NaturalSourceDisposition.Skipped,
                SearchCursor = result.NextCursor
            };
            Stage();
            return;
        }
        var branch = NaturalBranchIdentity.Branch(api.WorldManager.SaveGame.SavegameIdentifier, source.Id);
        sources[source.Id] = sources[source.Id] with
        {
            Disposition = NaturalSourceDisposition.Reserved,
            Branch = branch,
            BranchBounds = result.Bounds,
            Anchor = result.Anchor,
            Interior = result.Interior!.ToImmutableArray(),
            SearchCursor = result.NextCursor,
            Rotation = result.Rotation,
            SchematicVersion = NaturalBranchSchematic.Version,
            Phase = NaturalPlacementPhase.Reserved,
            ClaimDescription = ClaimDescription(branch)
        };
        Stage();
        Reconcile(sources[source.Id]);
    }



    //// Advances at most one reserved row per tick so reconciliation remains bounded in worlds with many traders.
    ////
    private void Tick(float elapsedSeconds)
    {
        if (!loaded || failed || disposed) return;
        try { ReconcileOne(); }
        catch (Exception error) { Disable(error); }
    }



    //// Selects one nonterminal reservation in stable source order and attempts its next idempotent phase.
    ////
    private void ReconcileOne()
    {
        var rows = sources.Values.Where(row => row.Disposition is NaturalSourceDisposition.Reserved or
                NaturalSourceDisposition.Placed)
            .OrderBy(row => row.Id, StringComparer.Ordinal).ToArray();
        if (rows.Length == 0) return;
        if (cursor >= rows.Length) cursor = 0;
        Reconcile(rows[cursor++]);
    }



    //// Reconciles schematic, anchor, claim, home, and completion phases from public matching evidence only.
    ////
    private void Reconcile(NaturalBranchSource row)
    {
        var bounds = row.BranchBounds;
        if (bounds is null || row.Anchor is null) { Quarantine(row, "missing reservation geometry"); return; }
        var center = BankerRosterStorage.Position(row.Anchor);
        if (!api.World.IsFullyLoadedChunk(center)) return;
        if (row.Disposition == NaturalSourceDisposition.Placed)
        {
            var anchor = FindAnchor(row);
            if (anchor is null || anchor.Branch != row.Branch || anchor.SourceId != row.Id)
            { Quarantine(row, "placed branch anchor conflict"); return; }
            var claims = Claims(row).ToArray();
            if (claims.Length > 1) { Quarantine(row, "placed branch claim conflict"); return; }
            if (claims.Length == 0)
            {
                sources[row.Id] = row with
                {
                    Disposition = NaturalSourceDisposition.Reserved,
                    Phase = NaturalPlacementPhase.BlocksPlaced
                };
                Stage();
                Reconcile(sources[row.Id]);
                return;
            }
            bankers.EstablishHome(row.Branch, center,
                row.Interior.Select(BankerRosterStorage.Position).ToArray());
            return;
        }
        if (row.Phase == NaturalPlacementPhase.Reserved)
        {
            var anchor = FindAnchor(row);
            if (anchor is null)
            {
                if (!FootprintClear(bounds)) { Quarantine(row, "partial placement without anchor"); return; }
                if (schematic!.Place(row) <= 0) { Quarantine(row, "schematic placed no blocks"); return; }
                anchor = FindAnchor(row);
                if (anchor is null) { Quarantine(row, "schematic anchor missing"); return; }
            }
            else if (anchor.Branch != Guid.Empty && (anchor.Branch != row.Branch || anchor.SourceId != row.Id))
            { Quarantine(row, "anchor identity conflict"); return; }
            row = Advance(row, NaturalPlacementPhase.BlocksPlaced);
        }
        if (row.Phase == NaturalPlacementPhase.BlocksPlaced)
        {
            var anchor = FindAnchor(row);
            if (anchor is null) { Quarantine(row, "schematic anchor missing after placement"); return; }
            if (anchor.Branch == Guid.Empty) anchor.Bind(row.Branch, row.Id);
            else if (anchor.Branch != row.Branch || anchor.SourceId != row.Id)
            { Quarantine(row, "anchor identity conflict"); return; }
            row = Advance(row, NaturalPlacementPhase.AnchorBound);
        }
        if (row.Phase == NaturalPlacementPhase.AnchorBound)
        {
            var matching = Claims(row).ToArray();
            if (matching.Length > 1) { Quarantine(row, "duplicate natural claims"); return; }
            if (matching.Length == 0)
            {
                var claim = new LandClaim
                {
                    Areas = [new Cuboidi(bounds.MinX, bounds.MinY, bounds.MinZ,
                        checked(bounds.MaxX + 1), checked(bounds.MaxY + 1), checked(bounds.MaxZ + 1))],
                    ProtectionLevel = (int)EnumProtectionLevel.Pedantic,
                    OwnedByPlayerUid = "firstgearbank:natural:" + row.Branch.ToString("N"),
                    LastKnownOwnerName = "First Gear Bank",
                    Description = row.ClaimDescription,
                    AllowUseEveryone = true,
                    AllowTraverseEveryone = true
                };
                api.World.Claims.Add(claim);
                matching = Claims(row).ToArray();
                if (matching.Length != 1) { Quarantine(row, "natural claim creation conflict"); return; }
            }
            row = Advance(row, NaturalPlacementPhase.ClaimPlaced);
        }
        if (row.Phase == NaturalPlacementPhase.ClaimPlaced)
        {
            bankers.EstablishHome(row.Branch, center,
                row.Interior.Select(BankerRosterStorage.Position).ToArray());
            row = Advance(row, NaturalPlacementPhase.HomeEstablished);
        }
        if (row.Phase == NaturalPlacementPhase.HomeEstablished)
        {
            row = row with { Disposition = NaturalSourceDisposition.Placed, Phase = NaturalPlacementPhase.Complete };
            sources[row.Id] = row;
            Stage();
            log.Write("INFO", "natural-lifecycle", "Natural branch placement completed: " + row.Branch.ToString("N"));
        }
    }



    //// Persists one phase before any later world side effect may begin.
    ////
    private NaturalBranchSource Advance(NaturalBranchSource row, NaturalPlacementPhase phase)
    {
        row = row with { Phase = phase };
        sources[row.Id] = row;
        Stage();
        return row;
    }



    //// Finds the single natural anchor block entity inside the reserved 7x7 footprint.
    ////
    private NaturalBankAnchorBlockEntity? FindAnchor(NaturalBranchSource row)
    {
        NaturalBankAnchorBlockEntity? found = null;
        var bounds = row.BranchBounds!;
        for (var x = bounds.MinX; x <= bounds.MaxX; x++)
            for (var z = bounds.MinZ; z <= bounds.MaxZ; z++)
                if (api.World.BlockAccessor.GetBlockEntity(new BlockPos(x, bounds.MinY, z, bounds.Dimension)) is
                    NaturalBankAnchorBlockEntity anchor)
                {
                    if (found is not null) return null;
                    found = anchor;
                }
        return found;
    }



    //// Distinguishes a pristine reserved footprint from ambiguous partial blocks after an interrupted placement.
    ////
    private bool FootprintClear(BranchBounds bounds)
    {
        for (var x = bounds.MinX; x <= bounds.MaxX; x++)
            for (var z = bounds.MinZ; z <= bounds.MaxZ; z++)
                for (var y = bounds.MinY; y <= bounds.MaxY; y++)
                    if (api.World.BlockAccessor.GetBlock(new BlockPos(x, y, z, bounds.Dimension)).Replaceable < 6000)
                        return false;
        return true;
    }



    //// Returns only claims bearing the exact durable description and synthetic mod owner for this branch.
    ////
    private IEnumerable<LandClaim> Claims(NaturalBranchSource row)
    {
        var owner = "firstgearbank:natural:" + row.Branch.ToString("N");
        return api.World.Claims.All.Where(claim => claim.Description == row.ClaimDescription &&
            claim.OwnedByPlayerUid == owner);
    }



    //// Permanently isolates contradictory placement evidence while retaining its topology reservation.
    ////
    private void Quarantine(NaturalBranchSource row, string reason)
    {
        sources[row.Id] = row with { Disposition = NaturalSourceDisposition.Quarantined };
        Stage();
        log.Write("CRIT", "natural-lifecycle", "Natural branch evidence quarantined: " + reason + ".");
    }



    //// Reports whether initial NPC staffing is permitted for a natural home or unrelated/manual branch.
    ////
    internal bool CanInitiallyStaff(Guid branch)
    {
        var row = sources.Values.FirstOrDefault(source => source.Branch == branch);
        return row is null || row.Disposition == NaturalSourceDisposition.Placed ||
            (int)row.Phase >= (int)NaturalPlacementPhase.HomeEstablished;
    }



    //// Reports active natural ownership for protected administrator routing.
    ////
    internal bool IsActiveBranch(Guid branch)
    {
        return sources.Values.Any(source => source.Branch == branch && source.Disposition is
            NaturalSourceDisposition.Reserved or NaturalSourceDisposition.Placed or NaturalSourceDisposition.Quarantined);
    }



    //// Counts placed natural footprints containing one position so unified administration can reject ambiguity.
    ////
    internal int CountContaining(BlockPos position)
    {
        return sources.Values.Count(source => source.Disposition == NaturalSourceDisposition.Placed &&
            source.BranchBounds is { } bounds && Contains(bounds, position));
    }



    //// Decommissions exactly one containing natural branch while leaving its building and all finance untouched.
    ////
    internal TextCommandResult TryDecommission(IServerPlayer player, string reason)
    {
        var pos = player.Entity.Pos.AsBlockPos;
        var matches = sources.Values.Where(source => source.Disposition == NaturalSourceDisposition.Placed &&
            source.BranchBounds is { } bounds && Contains(bounds, pos)).ToArray();
        if (matches.Length == 0) return TextCommandResult.Error("No active branch contains this position.");
        if (matches.Length != 1) return TextCommandResult.Error("The branch at this position is ambiguous; nothing changed.");
        var row = matches[0] with
        {
            Disposition = NaturalSourceDisposition.Decommissioned,
            DecommissionReason = reason
        };
        sources[row.Id] = row;
        Stage();
        CleanupDecommissioned(row);
        log.Write("WARN", "natural-administration", "Natural branch decommissioned: " + row.Branch.ToString("N"));
        return TextCommandResult.Success("Natural branch decommissioned; its blocks and all accounts are unchanged.");
    }



    //// Replays idempotent NPC-home and mod-owned-claim cleanup for a durable decommission tombstone.
    ////
    private void CleanupDecommissioned(NaturalBranchSource row)
    {
        bankers.RemoveHome(row.Branch, "Natural branch decommissioned: " + row.DecommissionReason);
        foreach (var claim in Claims(row).ToArray()) api.World.Claims.Remove(claim);
    }



    //// Stages the registry on world-save preparation without claiming disk durability.
    ////
    private void Save()
    {
        if (!loaded || failed || disposed) return;
        try { Stage(); }
        catch (Exception error) { Disable(error); }
    }



    //// Persists all rows and refreshes active/reserved topology as one server-thread transition.
    ////
    private void Stage()
    {
        storage!.Stage(sources.Values);
        PublishTopology();
    }



    //// Rebuilds natural topology from durable reservations that have not been decommissioned or skipped.
    ////
    private void PublishTopology()
    {
        topology.Replace(BranchKind.Natural, sources.Values.Where(source => source.BranchBounds is not null &&
            (source.Disposition is NaturalSourceDisposition.Reserved or NaturalSourceDisposition.Placed or
                NaturalSourceDisposition.Quarantined)).Select(source =>
            new BranchFootprint(source.Branch, BranchKind.Natural, source.BranchBounds!, true)));
    }



    //// Disables transitions after an unexpected failure while retaining claims/topology already established.
    ////
    private void Disable(Exception error)
    {
        if (failed) return;
        failed = true;
        log.Write("CRIT", "natural-lifecycle", "Natural branch management disabled after " +
            error.GetType().Name + ".");
    }



    //// Checks inclusive branch geometry against an administrator's current dimension-aware block position.
    ////
    private static bool Contains(BranchBounds bounds, BlockPos pos)
    {
        return pos.dimension == bounds.Dimension && pos.X >= bounds.MinX && pos.X <= bounds.MaxX &&
            pos.Y >= bounds.MinY && pos.Y <= bounds.MaxY && pos.Z >= bounds.MinZ && pos.Z <= bounds.MaxZ;
    }



    //// Creates the exact durable identity used to find only this mod-owned land claim later.
    ////
    private static string ClaimDescription(Guid branch)
    {
        return "First Gear Bank natural branch " + branch.ToString("N");
    }



    //// Stages final state and removes discovery/save/tick callbacks without retiring live branches or NPCs.
    ////
    public void Dispose()
    {
        if (disposed) return;
        Save();
        disposed = true;
        discovery.Dispose();
        api.Event.GameWorldSave -= Save;
        api.Event.UnregisterGameTickListener(tickListener);
    }



}
