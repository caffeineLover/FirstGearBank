/*
 * Defines and stages the world-owned Banker home roster independently of financial account persistence.
 * Branch content supplies a logical anchor and permitted standing cells.  The roster retains those cells, the chosen
 * home position, entity/spawn identities, and replacement progress in ordinary calendar days.  It owns NPC service
 * availability only: claims, Charter validation, spacing reservations, and customer money remain other systems' work.
 *
 * A world-bound installation marker and checksummed versioned payload distinguish first installation from missing or
 * corrupt established state.  Invalid bytes are preserved and disable NPC management rather than recreating Bankers.
 * StoreData stages data for the engine's save; it cannot make entity and roster writes atomically durable.  Lifecycle
 * reconciliation therefore preserves unloaded assignments and quarantines ambiguous spawn/death evidence.
 */

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace FirstGearBank.Server;

/// Durable NPC disposition, separate from branch protection and all banking account/contract states.
/// Ready permits initial staffing; Reserved records an unfinished spawn; Assigned retains its entity even unloaded.
/// Waiting retains a death timer; Removed is a retirement tombstone; Quarantined preserves ambiguous evidence.
internal enum BankerDisposition { Ready, Reserved, Assigned, Waiting, Removed, Quarantined }

/// Immutable dimension-aware feet/anchor cell; Y is local height, never the engine's dimension-encoded InternalY.
internal sealed record BankerCell(int X, int Y, int Z, int Dimension);

/// One branch's NPC assignment and saved replacement timer; a detached assignment retains only temporary service.
/// SpawnId links roster reservation to entity attributes, while EntityId prevents an unloaded Banker being duplicated.
internal sealed record BankerHome(Guid Branch, BankerCell Anchor, BankerCell Standing,
    ImmutableArray<BankerCell> Interior, BankerDisposition Disposition, Guid SpawnId, long EntityId,
    int Generation, double SampledDays, double RemainingDays, double LastCalendarDay, double? DepartureDay = null);

/// Versioned world-bound collection of homes; no player identifiers, inventories, or monetary data are stored here.
internal sealed record BankerRosterFile(int Version, string World, ImmutableArray<BankerHome> Homes);

/// Conservative storage boundary for the NPC roster, borrowing the engine save and existing bounded diagnostic sink.
internal sealed class BankerRosterStorage
{
    private const string StateKey = "firstgearbank:npc-roster-v1";
    private const string MarkerKey = "firstgearbank:npc-installed-v1";
    private readonly ISaveGame save;
    private readonly ServerDiagnostics log;
    private readonly byte[] marker;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 16 };
    private bool writable;



    //// Binds this roster to one save identity without reading financial authority or initializing any NPC assignment.
    ////
    internal BankerRosterStorage(ISaveGame save, ServerDiagnostics log)
    {
        this.save = save;
        this.log = log;
        marker = SHA256.HashData(Encoding.UTF8.GetBytes("firstgearbank/npc/v1/" + save.SavegameIdentifier));
    }



    //// Accepts absent first-install data or a valid roster; all other cases preserve bytes and disable NPC management.
    //// Loaded rows are validated before exposing any home that could authorize spawning or banking interaction.
    ////
    internal Dictionary<Guid, BankerHome>? Load()
    {
        var bytes = save.GetData(StateKey);
        var storedMarker = save.GetData(MarkerKey);
        try
        {
            // Both records must be absent for first installation; partial absence is not a new-world signal.
            if (bytes is null && storedMarker is null)
            {
                writable = true;
                return new();
            }
            if (storedMarker is null || !storedMarker.AsSpan().SequenceEqual(marker) || bytes is not { Length: > 32 } ||
                !CryptographicOperations.FixedTimeEquals(bytes.AsSpan(0, 32), SHA256.HashData(bytes.AsSpan(32))))
                throw new JsonException();
            var file = JsonSerializer.Deserialize<BankerRosterFile>(bytes.AsSpan(32), JsonOptions);
            if (file is null || file.Version != 1 || file.World != save.SavegameIdentifier || file.Homes.IsDefault)
                throw new JsonException();
            var homes = new Dictionary<Guid, BankerHome>();
            // Reject duplicate branch identities before handing any spawn authority to the lifecycle.
            foreach (var home in file.Homes)
            {
                Validate(home);
                if (!homes.TryAdd(home.Branch, home)) throw new JsonException();
            }
            writable = true;
            return homes;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or OverflowException)
        {
            log.Write("CRIT", "banker-storage", "NPC roster is invalid or missing; preserved bytes and disabled NPC management.");
            return null;
        }
    }



    //// Stages a checksummed complete roster and installation marker without claiming the engine has flushed either.
    //// The lifecycle calls this before spawning and at every world save, even when previous staging succeeded.
    ////
    internal void Stage(IEnumerable<BankerHome> homes)
    {
        if (!writable) throw new InvalidOperationException("NPC roster is not writable.");
        var rows = homes.OrderBy(home => home.Branch).ToImmutableArray();
        foreach (var home in rows) Validate(home);
        var data = JsonSerializer.SerializeToUtf8Bytes(new BankerRosterFile(1, save.SavegameIdentifier, rows), JsonOptions);
        var framed = new byte[32 + data.Length];
        SHA256.HashData(data).CopyTo(framed, 0);
        data.CopyTo(framed, 32);
        save.StoreData(StateKey, framed);
        save.StoreData(MarkerKey, marker);
    }



    //// Rejects malformed home geometry and timer/assignment combinations instead of inventing replacement authority.
    //// Interior cells are a caller-validated region, not evidence that a Charter or claim exists.
    ////
    internal static void Validate(BankerHome home)
    {
        if (home is null || home.Anchor is null || home.Standing is null || home.Branch == Guid.Empty || home.Interior.IsDefaultOrEmpty ||
            home.Interior.Length > 4096 || home.Generation < 0 || home.EntityId < 0 ||
            !Enum.IsDefined(home.Disposition) || !double.IsFinite(home.SampledDays) || home.SampledDays < 0 ||
            !double.IsFinite(home.RemainingDays) || home.RemainingDays < 0 || home.RemainingDays > home.SampledDays ||
            !double.IsFinite(home.LastCalendarDay) || home.LastCalendarDay < 0 ||
            home.DepartureDay is { } departure && (!double.IsFinite(departure) || departure < 0))
            throw new JsonException();
        foreach (var cell in home.Interior.Append(home.Anchor).Append(home.Standing))
            if (cell is null || cell.Dimension != home.Anchor.Dimension || cell.Dimension is < 0 or > 65534 ||
                cell.Y is < 1 or > 32765 || Math.Abs((long)cell.X - home.Anchor.X) > 128 ||
                Math.Abs((long)cell.Z - home.Anchor.Z) > 128 || Math.Abs((long)cell.Y - home.Anchor.Y) > 128)
                throw new JsonException();
        if (!home.Interior.Contains(home.Standing) || home.Interior.Distinct().Count() != home.Interior.Length ||
            home.Disposition is BankerDisposition.Assigned or BankerDisposition.Waiting &&
                (home.EntityId == 0 || home.SpawnId == Guid.Empty) ||
            home.Disposition == BankerDisposition.Reserved && (home.EntityId != 0 || home.SpawnId == Guid.Empty))
            throw new JsonException();
    }



    //// Captures a mutable engine position by value so later caller edits cannot move a saved NPC home.
    ////
    internal static BankerCell Cell(BlockPos position)
    {
        return new(position.X, position.Y, position.Z, position.dimension);
    }



    //// Reconstructs a dimension-aware engine block position from immutable persisted geometry.
    ////
    internal static BlockPos Position(BankerCell cell)
    {
        return new(cell.X, cell.Y, cell.Z, cell.Dimension);
    }



    //// Converts a standing cell to the internal-Y coordinates required by native collision and navigation APIs.
    ////
    internal static Vec3d Center(BankerCell cell)
    {
        return new(cell.X + 0.5, cell.Y + cell.Dimension * (double)BlockPos.DimensionBoundary, cell.Z + 0.5);
    }



}
