/*
 * Persists accepted Charter branches independently of both plaque block entities and the financial ledger.
 * Each row owns its placement and branch identities, exact protected cells with interaction roles, immutable walkable
 * interior, and a once-sampled first-arrival timer measured in ordinary world days.  Removed rows remain tombstones.
 *
 * A checksummed payload and world-bound marker distinguish first installation from damaged established authority.
 * Invalid or partially missing data is preserved and disables Charter management rather than recreating protection,
 * timers, or Bankers.  Engine StoreData stages bytes but cannot make block, NPC, and registry saves atomic on disk.
 */

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vintagestory.API.Server;

namespace FirstGearBank.Server;

/// Distinguishes protected branches from permanent placement tombstones without encoding NPC state.
internal enum CharterDisposition { Active, Removed }

/// Describes why a captured position is protected and which ordinary interaction, if any, remains permitted.
[Flags]
internal enum CharterPositionRole { Structural = 1, Charter = 2, Door = 4, Storage = 8, Seat = 16, Light = 32 }

/// One exact protected cell and its immutable interaction role at branch activation.
internal sealed record CharterProtectedCell(BankerCell Cell, CharterPositionRole Role);

/// Durable Charter authority, including suspended progress but excluding all accounts and NPC replacement state.
/// HomeEstablished records the roster handoff; premises still gate spawning until that roster reports an assignment.
/// RetirementApplied seals the idempotent cancellation/departure handoff while the placement tombstone remains.
internal sealed record CharterBranch(Guid Placement, Guid Branch, BankerCell Anchor, string Placer,
    ImmutableArray<CharterProtectedCell> Protected, ImmutableArray<BankerCell> Interior,
    double SampledDays, double RemainingDays, double LastCalendarDay, bool PremisesValid,
    bool HomeEstablished, CharterDisposition Disposition, bool ImmediateRetirement = false,
    double? DepartureDay = null, bool RetirementApplied = false);

/// Versioned collection bound to one Vintage Story save identity.
internal sealed record CharterRegistryFile(int Version, string World, ImmutableArray<CharterBranch> Branches);

/// Conservative save boundary that exposes Charter authority only after complete payload validation.
internal sealed class CharterRegistryStorage
{
    private const string StateKey = "firstgearbank:charter-registry-v1";
    private const string MarkerKey = "firstgearbank:charter-installed-v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 16 };
    private readonly ISaveGame save;
    private readonly ServerDiagnostics log;
    private readonly byte[] marker;
    private bool writable;



    //// Binds this registry to one world while leaving branch creation to validated lifecycle transitions.
    ////
    internal CharterRegistryStorage(ISaveGame save, ServerDiagnostics log)
    {
        this.save = save;
        this.log = log;
        marker = SHA256.HashData(Encoding.UTF8.GetBytes("firstgearbank/charters/v1/" + save.SavegameIdentifier));
    }



    //// Accepts a wholly absent first-install state or a valid established registry; every partial case fails closed.
    ////
    internal Dictionary<Guid, CharterBranch>? Load()
    {
        var bytes = save.GetData(StateKey);
        var storedMarker = save.GetData(MarkerKey);
        try
        {
            if (bytes is null && storedMarker is null)
            {
                writable = true;
                return new();
            }
            if (storedMarker is null || !storedMarker.AsSpan().SequenceEqual(marker) ||
                bytes is not { Length: > 32 } || !CryptographicOperations.FixedTimeEquals(
                    bytes.AsSpan(0, 32), SHA256.HashData(bytes.AsSpan(32)))) throw new JsonException();
            var file = JsonSerializer.Deserialize<CharterRegistryFile>(bytes.AsSpan(32), JsonOptions);
            if (file is null || file.Version != 1 || file.World != save.SavegameIdentifier ||
                file.Branches.IsDefault) throw new JsonException();
            var branches = new Dictionary<Guid, CharterBranch>();
            var branchIds = new HashSet<Guid>();
            foreach (var branch in file.Branches)
            {
                Validate(branch);
                if (!branches.TryAdd(branch.Placement, branch) || !branchIds.Add(branch.Branch))
                    throw new JsonException();
            }
            writable = true;
            return branches;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or OverflowException)
        {
            log.Write("CRIT", "charter-storage",
                "Charter registry is invalid or missing; preserved bytes and disabled Charter management.");
            return null;
        }
    }



    //// Validates, orders, checksums, and stages the full registry without claiming that the engine flushed it to disk.
    ////
    internal void Stage(IEnumerable<CharterBranch> branches)
    {
        if (!writable) throw new InvalidOperationException("Charter registry is not writable.");
        var rows = branches.OrderBy(row => row.Placement).ToImmutableArray();
        foreach (var row in rows) Validate(row);
        var data = JsonSerializer.SerializeToUtf8Bytes(new CharterRegistryFile(1, save.SavegameIdentifier, rows),
            JsonOptions);
        var framed = new byte[32 + data.Length];
        SHA256.HashData(data).CopyTo(framed, 0);
        data.CopyTo(framed, 32);
        save.StoreData(StateKey, framed);
        save.StoreData(MarkerKey, marker);
    }



    //// Rejects malformed geometry, identities, roles, and timers before they can protect blocks or create a Banker.
    ////
    internal static void Validate(CharterBranch? branch)
    {
        if (branch is null) throw new JsonException();
        var anchor = RequiredCell(branch.Anchor);
        if (branch.Placement == Guid.Empty || branch.Branch == Guid.Empty ||
            string.IsNullOrWhiteSpace(branch.Placer) || branch.Placer.Length > 256 ||
            branch.Protected.IsDefaultOrEmpty || branch.Protected.Length > 12_000 ||
            branch.Interior.IsDefaultOrEmpty || branch.Interior.Length > 4096 ||
            !double.IsFinite(branch.SampledDays) || branch.SampledDays <= 0 ||
            !double.IsFinite(branch.RemainingDays) || branch.RemainingDays < 0 ||
            branch.RemainingDays > branch.SampledDays || !double.IsFinite(branch.LastCalendarDay) ||
            branch.LastCalendarDay < 0 || !Enum.IsDefined(branch.Disposition) ||
            branch.DepartureDay is { } departure && (!double.IsFinite(departure) || departure < 0) ||
            branch.Disposition == CharterDisposition.Active &&
                (branch.ImmediateRetirement || branch.DepartureDay is not null || branch.RetirementApplied) ||
            branch.Disposition == CharterDisposition.Removed && branch.DepartureDay is null) throw new JsonException();
        var protectedCells = branch.Protected.Select(RequiredProtectedCell).ToArray();
        var cells = protectedCells.Select(entry => RequiredCell(entry.Cell)).ToArray();
        var interior = branch.Interior.Select(RequiredCell).ToArray();
        var cellSet = cells.ToHashSet();
        if (cells.Any(cell => !ValidCell(cell, anchor)) || interior.Any(cell => !ValidCell(cell, anchor)) ||
            cellSet.Count != cells.Length || interior.Distinct().Count() != interior.Length ||
            interior.Any(cell => !cellSet.Contains(cell)) ||
            protectedCells.Count(entry => entry.Role.HasFlag(CharterPositionRole.Charter)) != 1 ||
            protectedCells.Count(entry => entry.Cell == anchor &&
                entry.Role.HasFlag(CharterPositionRole.Charter)) != 1 ||
            protectedCells.Any(entry => entry.Role == 0 ||
                (entry.Role & ~Enum.GetValues<CharterPositionRole>().Aggregate((left, right) => left | right)) != 0))
            throw new JsonException();
    }



    //// Converts nullable deserialization input into a validated cell before geometry code may dereference it.
    ////
    private static BankerCell RequiredCell(BankerCell? cell)
    {
        return cell ?? throw new JsonException();
    }



    //// Converts nullable deserialization input into a validated protected-cell record before role checks use it.
    ////
    private static CharterProtectedCell RequiredProtectedCell(CharterProtectedCell? entry)
    {
        return entry ?? throw new JsonException();
    }



    //// Applies the bounded room-capture coordinate domain shared with Banker home persistence.
    ////
    private static bool ValidCell(BankerCell cell, BankerCell anchor)
    {
        return cell.Dimension == anchor.Dimension && cell.Dimension is >= 0 and <= 65534 &&
            cell.Y is >= 1 and <= 32765 && Math.Abs((long)cell.X - anchor.X) <= 128 &&
            Math.Abs((long)cell.Y - anchor.Y) <= 128 && Math.Abs((long)cell.Z - anchor.Z) <= 128;
    }



}
