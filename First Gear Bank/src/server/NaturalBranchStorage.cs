/*
 * Persists deterministic vanilla-trader source decisions and natural-branch placement phases independently of chunks,
 * claims, schematics, and Banker entities.  Every observed source receives one permanent disposition; reservations
 * carry complete geometry before world mutation so startup reconciliation can resume or quarantine without rerolling.
 *
 * A world-bound marker and SHA-256 frame distinguish first installation from damaged established authority.  Invalid
 * bytes disable natural management and remain in the save rather than being replaced by a fresh registry.
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

/// Permanent evaluation and lifecycle outcomes for one recognized generated trader source.
internal enum NaturalSourceDisposition
{
    AwaitingData, NotSelected, Reserved, Placed, Skipped, Decommissioned, Quarantined
}

/// Durable milestones that make natural placement and cleanup replayable after partial saves.
internal enum NaturalPlacementPhase
{
    None, Reserved, BlocksPlaced, AnchorBound, ClaimPlaced, HomeEstablished, Complete
}

/// One canonical vanilla generated-structure observation without any trader-entity dependency.
internal sealed record NaturalStructureSource(string Id, string Code, BranchBounds Bounds);

/// Complete persisted decision and optional placement reservation for one generated trader source.
internal sealed record NaturalBranchSource(string Id, string StructureCode, BranchBounds StructureBounds,
    double Probability, double Roll, NaturalSourceDisposition Disposition, Guid Branch,
    BranchBounds? BranchBounds, BankerCell? Anchor, ImmutableArray<BankerCell> Interior, int SearchCursor, int Rotation,
    int SchematicVersion, NaturalPlacementPhase Phase, string? ClaimDescription = null,
    string? DecommissionReason = null);

/// Versioned, world-bound natural registry document.
internal sealed record NaturalBranchRegistryFile(int Version, string World,
    ImmutableArray<NaturalBranchSource> Sources);

/// Checksummed world-save adapter that stages only wholly validated natural registry snapshots.
internal sealed class NaturalBranchStorage
{
    private const string StateKey = "firstgearbank:natural-branches-v1";
    private const string MarkerKey = "firstgearbank:natural-branches-installed-v1";
    private readonly ISaveGame save;
    private readonly ServerDiagnostics log;
    private readonly byte[] marker;
    private bool writable;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 24 };



    //// Binds natural branch authority to the exact world save identifier.
    ////
    internal NaturalBranchStorage(ISaveGame save, ServerDiagnostics log)
    {
        this.save = save;
        this.log = log;
        marker = SHA256.HashData(Encoding.UTF8.GetBytes("firstgearbank/natural/v1/" + save.SavegameIdentifier));
    }



    //// Restores a complete valid registry or initializes only when both state and marker are absent.
    ////
    internal Dictionary<string, NaturalBranchSource>? Load()
    {
        var bytes = save.GetData(StateKey);
        var installed = save.GetData(MarkerKey);
        if (installed is not null && !installed.AsSpan().SequenceEqual(marker)) return Fail("marker mismatch");
        if (bytes is null)
        {
            if (installed is not null) return Fail("established registry missing");
            writable = true;
            Stage([]);
            return new(StringComparer.Ordinal);
        }
        try
        {
            if (bytes.Length is <= 32 or > 16_777_216 ||
                !CryptographicOperations.FixedTimeEquals(bytes.AsSpan(0, 32), SHA256.HashData(bytes.AsSpan(32))))
                return Fail("checksum invalid");
            var file = JsonSerializer.Deserialize<NaturalBranchRegistryFile>(bytes.AsSpan(32), JsonOptions);
            if (file is null || file.Version != 1 || file.World != save.SavegameIdentifier ||
                file.Sources.IsDefault || file.Sources.Length > 100_000) return Fail("document invalid");
            foreach (var source in file.Sources) Validate(source);
            var result = file.Sources.ToDictionary(source => source.Id, StringComparer.Ordinal);
            if (result.Count != file.Sources.Length || result.Values.Where(source => source.Branch != Guid.Empty)
                    .Select(source => source.Branch).Distinct().Count() !=
                result.Values.Count(source => source.Branch != Guid.Empty)) return Fail("identity conflict");
            writable = true;
            return result;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            return Fail(error.GetType().Name);
        }
    }



    //// Stages one deterministically ordered full registry and its installation marker.
    ////
    internal void Stage(IEnumerable<NaturalBranchSource> sources)
    {
        if (!writable) throw new InvalidOperationException("Natural branch registry is not writable.");
        var rows = sources.OrderBy(source => source.Id, StringComparer.Ordinal).ToImmutableArray();
        foreach (var source in rows) Validate(source);
        var data = JsonSerializer.SerializeToUtf8Bytes(new NaturalBranchRegistryFile(1,
            save.SavegameIdentifier, rows), JsonOptions);
        var framed = new byte[32 + data.Length];
        SHA256.HashData(data).CopyTo(framed, 0);
        data.CopyTo(framed, 32);
        save.StoreData(MarkerKey, marker);
        save.StoreData(StateKey, framed);
    }



    //// Rejects impossible disposition, geometry, probability, rotation, and placement-phase combinations.
    ////
    private void Validate(NaturalBranchSource? source)
    {
        if (source is null || string.IsNullOrWhiteSpace(source.Id) || source.Id.Length != 64 ||
            string.IsNullOrWhiteSpace(source.StructureCode) || source.StructureCode.Length > 256 ||
            !source.StructureCode.StartsWith("game:", StringComparison.Ordinal) ||
            !double.IsFinite(source.Probability) || source.Probability is < 0 or > 1 ||
            !double.IsFinite(source.Roll) || source.Roll is < 0 or >= 1 || !Enum.IsDefined(source.Disposition) ||
            !Enum.IsDefined(source.Phase) || source.SearchCursor is < 0 or > 1_000_000 ||
            source.Rotation is < 0 or > 3 || source.SchematicVersion is < 0 or > 1 ||
            !ValidBounds(source.StructureBounds) || source.StructureBounds.MaxX - source.StructureBounds.MinX > 1024 ||
            source.StructureBounds.MaxZ - source.StructureBounds.MinZ > 1024 ||
            source.StructureBounds.MaxY - source.StructureBounds.MinY > 1024 ||
            source.Interior.IsDefault || source.Interior.Length > 25 ||
            (source.Disposition is NaturalSourceDisposition.Reserved or NaturalSourceDisposition.Placed or
                NaturalSourceDisposition.Decommissioned or NaturalSourceDisposition.Quarantined) &&
                (source.Branch == Guid.Empty || source.BranchBounds is null || source.Anchor is null ||
                    !ValidBounds(source.BranchBounds) || source.Interior.IsDefaultOrEmpty ||
                    source.Phase == NaturalPlacementPhase.None || !ValidReservation(source)) ||
            (source.Disposition is NaturalSourceDisposition.AwaitingData or NaturalSourceDisposition.NotSelected or
                NaturalSourceDisposition.Skipped) &&
                (source.Branch != Guid.Empty || source.BranchBounds is not null || source.Anchor is not null ||
                    !source.Interior.IsEmpty || source.Phase != NaturalPlacementPhase.None ||
                    source.Disposition == NaturalSourceDisposition.NotSelected && source.SearchCursor != 0))
            throw new JsonException();
        var structure = new NaturalStructureSource(source.Id, source.StructureCode, source.StructureBounds);
        var expectedRoll = NaturalBranchIdentity.Roll(save.Seed, source.Id);
        if (source.Id != NaturalBranchIdentity.SourceId(save.SavegameIdentifier, structure) ||
            BitConverter.DoubleToInt64Bits(source.Roll) != BitConverter.DoubleToInt64Bits(expectedRoll) ||
            (source.Disposition == NaturalSourceDisposition.NotSelected) != (source.Roll >= source.Probability) ||
            (source.Branch != Guid.Empty &&
                source.Branch != NaturalBranchIdentity.Branch(save.SavegameIdentifier, source.Id)))
            throw new JsonException();
    }



    //// Applies broad coordinate and ordered-bound validation before any row can affect topology or world mutation.
    ////
    private static bool ValidBounds(BranchBounds bounds)
    {
        const int coordinateLimit = 30_000_000;
        return bounds.Dimension is >= 0 and <= 65534 &&
            bounds.MinX is >= -coordinateLimit and <= coordinateLimit &&
            bounds.MaxX is >= -coordinateLimit and <= coordinateLimit &&
            bounds.MinZ is >= -coordinateLimit and <= coordinateLimit &&
            bounds.MaxZ is >= -coordinateLimit and <= coordinateLimit && bounds.MinY is >= 0 and <= 32767 &&
            bounds.MaxY <= 32767 && bounds.MaxY >= bounds.MinY && bounds.MaxX >= bounds.MinX &&
            bounds.MaxZ >= bounds.MinZ;
    }



    //// Confirms exact v1 footprint dimensions, contained cells, claim evidence, and terminal phase combinations.
    ////
    private static bool ValidReservation(NaturalBranchSource source)
    {
        var bounds = source.BranchBounds!;
        var anchor = source.Anchor!;
        if (bounds.Dimension != source.StructureBounds.Dimension || bounds.MaxX - bounds.MinX != 6 ||
            bounds.MaxY - bounds.MinY != 4 || bounds.MaxZ - bounds.MinZ != 6 || source.SchematicVersion != 1 ||
            source.ClaimDescription != "First Gear Bank natural branch " + source.Branch.ToString("N") ||
            anchor.Dimension != bounds.Dimension || anchor.X != bounds.MinX + 3 || anchor.Y != bounds.MinY ||
            anchor.Z != bounds.MinZ + 3 || source.Interior.Length != 25 ||
            source.Interior.Distinct().Count() != source.Interior.Length ||
            source.Interior.Any(cell => cell.Dimension != bounds.Dimension || cell.X <= bounds.MinX ||
                cell.X >= bounds.MaxX || cell.Y != bounds.MinY + 1 || cell.Z <= bounds.MinZ ||
                cell.Z >= bounds.MaxZ)) return false;
        return source.Disposition switch
        {
            NaturalSourceDisposition.Reserved => (int)source.Phase >= (int)NaturalPlacementPhase.Reserved &&
                (int)source.Phase < (int)NaturalPlacementPhase.Complete,
            NaturalSourceDisposition.Placed or NaturalSourceDisposition.Decommissioned =>
                source.Phase == NaturalPlacementPhase.Complete,
            NaturalSourceDisposition.Quarantined => source.Phase != NaturalPlacementPhase.None,
            _ => false
        };
    }



    //// Logs one bounded reason and leaves established bytes untouched with writes disabled.
    ////
    private Dictionary<string, NaturalBranchSource>? Fail(string reason)
    {
        log.Write("CRIT", "natural-storage", "Natural branch registry unavailable: " + reason + ".");
        return null;
    }



}
