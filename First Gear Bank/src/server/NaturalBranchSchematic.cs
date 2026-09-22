/*
 * Loads the version-one natural bank schematic from mod assets, validates/remaps its block codes against the current
 * world, rotates a packed clone, and places it only into cells already accepted by the site selector.  The caller owns
 * durable reservation and placement-phase transitions; this class owns no registry or crash policy.
 */

using System;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace FirstGearBank.Server;

/// Validated packed schematic template used for all natural branches in this schema version.
internal sealed class NaturalBranchSchematic
{
    internal const int Version = 1;
    private readonly ICoreServerAPI api;
    private readonly BlockSchematic template;



    //// Loads and validates the required asset once before any source may be reserved.
    ////
    internal NaturalBranchSchematic(ICoreServerAPI api)
    {
        this.api = api;
        var asset = api.Assets.Get(new AssetLocation("firstgearbank:worldgen/schematics/natural-bank-v1.json"));
        var error = "";
        template = BlockSchematic.LoadFromString(asset.ToText(), ref error);
        if (template is null || !string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException("Natural bank schematic cannot be decoded: " + error);
        template.LoadMetaInformationAndValidate(api.World.BlockAccessor, api.World, "natural-bank-v1");
        var required = new[]
        {
            "game:cobblestone-granite", "game:planks-pine-ud", "game:door-solid-oak", "game:table-normal",
            "game:chair-plain", "game:flowerpot-moss", "game:lantern-down", "game:flower-forgetmenot-free",
            "firstgearbank:natural-bank-strongbox", "firstgearbank:natural-bank-anchor"
        };
        var codes = template.BlockCodes.Values.Select(code => code.ToString()).ToHashSet(StringComparer.Ordinal);
        if (template.SizeX != 7 || template.SizeY != 5 || template.SizeZ != 7 ||
            template.Indices.Count != template.BlockIds.Count || required.Any(code => !codes.Contains(code)) ||
            template.BlockCodes.Values.Any(code => api.World.GetBlock(code) is null))
            throw new InvalidOperationException("Natural bank schematic is incomplete or references a missing block.");
    }



    //// Places one rotated clone with replaceable-only semantics and returns the engine's placed-block count.
    ////
    internal int Place(NaturalBranchSource reservation)
    {
        if (reservation.BranchBounds is null || reservation.Anchor is null || reservation.SchematicVersion != Version)
            throw new InvalidOperationException("Natural branch reservation lacks schematic geometry.");
        var schematic = template.ClonePacked();
        if (reservation.Rotation != 0)
            schematic.TransformWhilePacked(api.World, EnumOrigin.BottomCenter, reservation.Rotation * 90, null);
        var start = new BlockPos(reservation.BranchBounds.MinX, reservation.BranchBounds.MinY,
            reservation.BranchBounds.MinZ, reservation.BranchBounds.Dimension);
        return schematic.Place(api.World.BlockAccessor, api.World, start, EnumReplaceMode.Replaceable, true);
    }



}
