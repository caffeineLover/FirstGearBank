/*
 * Converts one mounted Charter and Vintage Story's supported RoomRegistry result into an exact protected snapshot.
 * Validation covers enclosure, documented interior dimensions, a complete 5-by-5 supported floor footprint, required
 * furnishings, one Charter, and the placing player's ordinary BuildOrBreak access over every captured position.
 *
 * Room discovery is engine-owned and bounded to the game's 14-cell search.  This adapter enumerates only the returned
 * room bounds, its immediate shell, and native multiblock dependencies.  Semantic attributes permit compatible mods;
 * a small code vocabulary covers vanilla furnishings.  Native public multiblock behaviors expand dependencies;
 * unsupported third-party mutation footprints remain outside this adapter's guarantees.  No scan loads or edits chunks.
 */

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace FirstGearBank.Server;

/// Immutable result used by Charter persistence, protection, and delayed Banker home establishment.
internal sealed record CharterCapture(ImmutableArray<CharterProtectedCell> Protected,
    ImmutableArray<BankerCell> Interior);

/// Bounded server-side interpreter of one recognized room and its protected structural closure.
internal sealed class CharterPremisesValidator
{
    private readonly ICoreServerAPI api;
    private readonly RoomRegistry rooms;



    //// Borrows the native room registry and world accessor; neither is owned or force-loaded by this validator.
    ////
    internal CharterPremisesValidator(ICoreServerAPI api)
    {
        this.api = api;
        rooms = api.ModLoader.GetModSystem<RoomRegistry>();
    }



    //// Captures a valid loaded room or returns a localized status key without creating branch identity or timers.
    ////
    internal bool TryCapture(BlockPos anchor, IPlayer? placer, out CharterCapture capture, out string status)
    {
        capture = null!;
        status = "charter-status-facing";
        var facing = BlockFacing.FromCode(api.World.BlockAccessor.GetBlock(anchor).LastCodePart());
        if (facing is null || !facing.IsHorizontal) return false;
        var room = rooms.GetRoomForPosition(anchor.AddCopy(facing.Opposite));
        status = "charter-status-room";
        if (room is null) return false;
        status = "charter-status-unloaded";
        if (room.AnyChunkUnloaded != 0) return false;
        status = "charter-status-enclosure";
        if (room.ExitCount != 0) return false;
        var width = room.Location.X2 - room.Location.X1 + 1;
        var height = room.Location.Y2 - room.Location.Y1 + 1;
        var length = room.Location.Z2 - room.Location.Z1 + 1;
        status = "charter-status-size";
        if (width is < 5 or > 13 || length is < 5 or > 13 || height is < 2 or > 13) return false;

        // The native membership bitmap defines connected interior exactly, including irregular room outlines.
        var interior = EnumerateRoom(room, anchor.dimension).ToHashSet();
        status = "charter-status-anchor";
        if (!interior.Contains(BankerRosterStorage.Cell(anchor))) return false;
        var topology = new HashSet<BankerCell>(interior) { BankerRosterStorage.Cell(anchor) };
        foreach (var cell in interior)
            foreach (var side in BlockFacing.ALLFACES)
            {
                var adjacent = new BankerCell(cell.X + side.Normali.X, cell.Y + side.Normali.Y,
                    cell.Z + side.Normali.Z, cell.Dimension);
                if (!interior.Contains(adjacent)) topology.Add(adjacent);
            }
        status = "charter-status-complex";
        if (!ExpandNativeDependencies(topology)) return false;

        // Roles are frozen at acceptance so an emptied damaged position remains protected and cannot change privileges.
        var protectedCells = topology.OrderBy(cell => cell.X).ThenBy(cell => cell.Y).ThenBy(cell => cell.Z)
            .Select(cell => new CharterProtectedCell(cell, RoleAt(cell))).ToImmutableArray();
        status = "charter-status-charter-count";
        if (protectedCells.Count(entry => entry.Role.HasFlag(CharterPositionRole.Charter)) != 1) return false;
        status = "charter-status-door";
        if (!protectedCells.Any(entry => entry.Role.HasFlag(CharterPositionRole.Door))) return false;
        status = "charter-status-table";
        if (!protectedCells.Any(entry => IsTable(entry.Cell))) return false;
        status = "charter-status-seat";
        if (!protectedCells.Any(entry => entry.Role.HasFlag(CharterPositionRole.Seat))) return false;
        status = "charter-status-storage";
        if (!protectedCells.Any(entry => entry.Role.HasFlag(CharterPositionRole.Storage))) return false;
        status = "charter-status-light";
        if (!protectedCells.Any(entry => entry.Role.HasFlag(CharterPositionRole.Light))) return false;
        status = "charter-status-flower";
        if (!protectedCells.Any(entry => IsFlowerpotWithFlower(entry.Cell))) return false;
        status = "charter-status-floor";
        if (!HasFiveByFiveFloor(room, anchor.dimension)) return false;

        // Activation waits for the original placer to be available and authorized over the immutable whole snapshot.
        if (placer is not null && protectedCells.Any(entry => api.World.Claims.TestAccess(placer,
                BankerRosterStorage.Position(entry.Cell), EnumBlockAccessFlags.BuildOrBreak) !=
            EnumWorldAccessResponse.Granted))
        {
            status = "charter-status-permission";
            return false;
        }
        capture = new CharterCapture(protectedCells, SafeStandingCells(interior));
        status = "charter-status-standing";
        return !capture.Interior.IsEmpty;
    }



    //// Enumerates only membership bits within the native room bounds, preserving the Charter's dimension.
    ////
    private static IEnumerable<BankerCell> EnumerateRoom(Room room, int dimension)
    {
        for (var x = room.Location.X1; x <= room.Location.X2; x++)
            for (var y = room.Location.Y1; y <= room.Location.Y2; y++)
                for (var z = room.Location.Z1; z <= room.Location.Z2; z++)
                {
                    var position = new BlockPos(x, y, z, dimension);
                    if (room.Contains(position)) yield return BankerRosterStorage.Cell(position);
                }
    }



    //// Adds controllers and all parts exposed by native generic multiblock and door behaviors to a fixed point.
    ////
    private bool ExpandNativeDependencies(HashSet<BankerCell> topology)
    {
        var queue = new Queue<BankerCell>(topology);
        while (queue.TryDequeue(out var cell))
        {
            var position = BankerRosterStorage.Position(cell);
            var block = api.World.BlockAccessor.GetBlock(position);
            if (block is BlockMultiblock proxy)
            {
                var controller = proxy.GetControlBlockPos(position);
                if (!AddDependency(topology, queue, controller)) continue;
                position = controller;
                block = api.World.BlockAccessor.GetBlock(position);
            }
            if (block.GetBehavior<BlockBehaviorMultiblock>() is { } multiblock)
                multiblock.IterateOverEach(position, part => AddDependency(topology, queue, part));
            if (block.GetBehavior<BlockBehaviorDoor>() is { } door &&
                api.World.BlockAccessor.GetBlockEntity(position)?.GetBehavior<BEBehaviorDoor>() is { } doorEntity)
                door.IterateOverEach(position, doorEntity.RotateYRad, doorEntity.InvertHandles,
                    part => AddDependency(topology, queue, part));
            if (topology.Count > 12_000) return false;
        }
        return true;
    }



    //// Copies one dependency cell and continues traversal only when it was not already captured.
    ////
    private static bool AddDependency(HashSet<BankerCell> topology, Queue<BankerCell> queue, BlockPos position)
    {
        var cell = BankerRosterStorage.Cell(position);
        if (topology.Add(cell)) queue.Enqueue(cell);
        return true;
    }



    //// Assigns narrowly permitted use roles while retaining structural denial for every captured position.
    ////
    private CharterPositionRole RoleAt(BankerCell cell)
    {
        var position = BankerRosterStorage.Position(cell);
        var block = api.World.BlockAccessor.GetBlock(position);
        if (block is BlockMultiblock proxy)
        {
            position = proxy.GetControlBlockPos(position);
            block = api.World.BlockAccessor.GetBlock(position);
        }
        var role = CharterPositionRole.Structural;
        if (block is BankerCharterBlock) role |= CharterPositionRole.Charter;
        if (block is BlockBaseDoor || block.GetBehavior<BlockBehaviorDoor>() is not null)
            role |= CharterPositionRole.Door;
        if (api.World.BlockAccessor.GetBlockEntity(position) is BlockEntityContainer and not BlockEntityPlantContainer)
            role |= CharterPositionRole.Storage;
        if (IsSeat(position, block)) role |= CharterPositionRole.Seat;
        if (IsArtificialLight(position, block)) role |= CharterPositionRole.Light;
        return role;
    }



    //// Recognizes explicit compatibility metadata and the bounded vanilla table/desk vocabulary.
    ////
    private bool IsTable(BankerCell cell)
    {
        var position = BankerRosterStorage.Position(cell);
        var block = api.World.BlockAccessor.GetBlock(position);
        var path = block.Code?.Path ?? string.Empty;
        return block.Attributes?["firstgearbankTable"].AsBool() == true || ContainsWord(path, "table") ||
            ContainsWord(path, "desk") || ContainsTypedClutterWord(position, "table") ||
            ContainsTypedClutterWord(position, "desk");
    }



    //// Recognizes explicit compatibility metadata and vanilla chair/stool codes without treating beds as seating.
    ////
    private bool IsSeat(BlockPos position, Block block)
    {
        var path = block.Code?.Path ?? string.Empty;
        return block.Attributes?["firstgearbankSeat"].AsBool() == true || ContainsWord(path, "chair") ||
            ContainsWord(path, "stool") || ContainsTypedClutterWord(position, "chair") ||
            ContainsTypedClutterWord(position, "stool");
    }



    //// Recognizes explicit compatibility metadata and bounded source types/codes, including unlit lamps and lanterns.
    ////
    private bool IsArtificialLight(BlockPos position, Block block)
    {
        var path = block.Code?.Path ?? string.Empty;
        return block.Attributes?["firstgearbankArtificialLight"].AsBool() == true ||
            block is BlockTorch or BlockOilLamp ||
            ContainsWord(path, "lamp") || ContainsWord(path, "lantern") || ContainsWord(path, "torch") ||
            ContainsWord(path, "chandelier") || ContainsWord(path, "candle") || ContainsWord(path, "brazier") ||
            ContainsWord(path, "firepit") || ContainsWord(path, "hearth") ||
            ContainsTypedClutterWord(position, "lamp") || ContainsTypedClutterWord(position, "lantern") ||
            ContainsTypedClutterWord(position, "chandelier") || ContainsTypedClutterWord(position, "candle");
    }



    //// Reads the placed clutter subtype because many vanilla tables and chairs share only the generic block code.
    ////
    private bool ContainsTypedClutterWord(BlockPos position, string word)
    {
        var type = api.World.BlockAccessor.GetBlockEntity(position)?
            .GetBehavior<BEBehaviorShapeFromAttributes>()?.Type;
        return type is not null && ContainsWord(type, word);
    }



    //// Requires a plant-container inventory whose actual placed content is a flower block, excluding empty pots.
    ////
    private bool IsFlowerpotWithFlower(BankerCell cell)
    {
        return api.World.BlockAccessor.GetBlockEntity(BankerRosterStorage.Position(cell)) is
            BlockEntityPlantContainer pot && pot.GetContents()?.Block?.Code?.Path.StartsWith("flower-",
                StringComparison.OrdinalIgnoreCase) == true;
    }



    //// Finds a complete five-by-five supported footprint with two-cell headroom; furniture may occupy its lower cell.
    ////
    private bool HasFiveByFiveFloor(Room room, int dimension)
    {
        for (var y = room.Location.Y1; y < room.Location.Y2; y++)
            for (var x = room.Location.X1; x <= room.Location.X2 - 4; x++)
                for (var z = room.Location.Z1; z <= room.Location.Z2 - 4; z++)
                {
                    var complete = true;
                    for (var dx = 0; dx < 5 && complete; dx++)
                        for (var dz = 0; dz < 5 && complete; dz++)
                        {
                            var feet = new BlockPos(x + dx, y, z + dz, dimension);
                            var head = feet.UpCopy();
                            var floor = feet.DownCopy();
                            var lowerInterior = room.Contains(feet) || IsFurnishing(feet);
                            complete = lowerInterior && room.Contains(head) &&
                                api.World.BlockAccessor.GetBlock(floor).SideSolid[BlockFacing.UP.Index];
                        }
                    if (complete) return true;
                }
        return false;
    }



    //// Treats only required fixture categories as permissible occupants of a validated floor footprint.
    ////
    private bool IsFurnishing(BlockPos position)
    {
        var cell = BankerRosterStorage.Cell(position);
        var block = api.World.BlockAccessor.GetBlock(position);
        var role = RoleAt(cell);
        return IsTable(cell) || IsFlowerpotWithFlower(cell) ||
            (role & (CharterPositionRole.Storage | CharterPositionRole.Seat | CharterPositionRole.Light)) != 0 ||
            block.Attributes?["firstgearbankFurniture"].AsBool() == true;
    }



    //// Retains only dry, supported, collision-free room cells suitable for the Banker's complete standing box.
    ////
    private ImmutableArray<BankerCell> SafeStandingCells(HashSet<BankerCell> interior)
    {
        return interior.Where(cell =>
        {
            var feet = BankerRosterStorage.Position(cell);
            var head = feet.UpCopy();
            var floor = feet.DownCopy();
            if (!interior.Contains(BankerRosterStorage.Cell(head)) ||
                !api.World.BlockAccessor.GetBlock(floor).SideSolid[BlockFacing.UP.Index] ||
                api.World.BlockAccessor.GetBlock(feet, BlockLayersAccess.Fluid).LiquidLevel != 0 ||
                api.World.BlockAccessor.GetBlock(head, BlockLayersAccess.Fluid).LiquidLevel != 0) return false;
            return !api.World.CollisionTester.IsColliding(api.World.BlockAccessor,
                new Cuboidf(-0.3f, 0, -0.3f, 0.3f, 1.75f, 0.3f), BankerRosterStorage.Center(cell), false);
        }).OrderBy(cell => cell.X).ThenBy(cell => cell.Y).ThenBy(cell => cell.Z).ToImmutableArray();
    }



    //// Matches whole semantic code segments so unrelated names containing the same letters do not qualify.
    ////
    private static bool ContainsWord(string path, string word)
    {
        return path.Split('-', '/', '_').Any(part => part.Equals(word, StringComparison.OrdinalIgnoreCase));
    }



}
