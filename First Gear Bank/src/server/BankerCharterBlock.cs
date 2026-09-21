/*
 * Defines the wall-mounted Banker’s Charter and its lightweight per-block placement identity.  The block reuses the
 * native sign renderer for a fixed localized two-line plaque, but never opens its editor.  Its crafted item carries
 * no branch, placement, topology, timer, or financial state; those values begin only after server placement.
 *
 * The block entity reports load, placement, and actual removal to CharterLifecycle.  Chunk unload is deliberately
 * distinct from removal.  A short replicated status is presentation only and never authorizes protection or spawning.
 */

using System;
using System.Text;
using First_Gear_Bank;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace FirstGearBank.Server;



/// Fixed-label wall sign that records the placing player only after ordinary sign placement succeeds.
public sealed class BankerCharterBlock : BlockSign
{



    //// Restricts the Charter to a wall face, delegates support and claim checks, then assigns its placement identity.
    ////
    public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack,
        BlockSelection blockSelection, ref string failureCode)
    {
        if (!blockSelection.Face.IsHorizontal)
        {
            failureCode = "requireswall";
            return false;
        }
        if (!base.TryPlaceBlock(world, byPlayer, itemstack, blockSelection, ref failureCode)) return false;
        if (world.Side == EnumAppSide.Server && world.BlockAccessor.GetBlockEntity(blockSelection.Position) is
            BankerCharterBlockEntity charter)
            charter.RecordPlacement(byPlayer?.PlayerUID ?? string.Empty);
        return true;
    }



    //// Suppresses the native pigment editor and routes deliberate removal through server branch authority.
    ////
    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSelection)
    {
        if (byPlayer.Entity?.Controls?.ShiftKey != true) return false;
        if (world.Side == EnumAppSide.Server && byPlayer is IServerPlayer serverPlayer &&
            world.BlockAccessor.GetBlockEntity(blockSelection.Position) is BankerCharterBlockEntity charter)
            world.Api.ModLoader.GetModSystem<First_Gear_BankModSystem>().Charters?
                .RemoveByPlayer(charter, serverPlayer);
        return true;
    }



    //// Replaces native sign-writing help with the deliberate Charter-removal gesture.
    ////
    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection,
        IPlayer forPlayer)
    {
        return
        [
            new WorldInteraction
            {
                ActionLangCode = "firstgearbank:blockhelp-charter-remove",
                HotKeyCode = "shift",
                MouseButton = EnumMouseButton.Right
            }
        ];
    }



}

/// Persisted placement metadata and fixed native-sign label for one plaque; branch authority remains in the roster.
public sealed class BankerCharterBlockEntity : BlockEntitySign
{
    private const string LabelKey = "firstgearbank:charter-plaque-text";

    private Guid placement;
    private string placer = string.Empty;
    private string status = "charter-status-checking";



    //// Establishes the fixed label before the native renderer starts, then registers the plaque with server lifecycle.
    ////
    public override void Initialize(ICoreAPI api)
    {
        text = LabelKey;
        Translateable = true;
        base.Initialize(api);
        if (api.Side == EnumAppSide.Server)
            api.ModLoader.GetModSystem<First_Gear_BankModSystem>().Charters?.Observe(this);
    }



    //// Assigns the identity and placing player to a newly placed plaque, then queues immediate validation.
    ////
    internal void RecordPlacement(string playerUid)
    {
        if (placement == Guid.Empty) placement = Guid.NewGuid();
        placer = playerUid;
        MarkDirty(true);
        Api.ModLoader.GetModSystem<First_Gear_BankModSystem>().Charters?.Observe(this);
    }



    //// Repairs placed-block metadata only from an already validated world-registry row at this exact anchor.
    ////
    internal void RestorePlacement(Guid placementId, string playerUid)
    {
        if (placement != Guid.Empty || placementId == Guid.Empty || string.IsNullOrWhiteSpace(playerUid)) return;
        placement = placementId;
        placer = playerUid;
        MarkDirty(true);
    }



    //// Exposes immutable copies of server-owned placement metadata to the validating lifecycle.
    ////
    internal bool TryPlacement(out Guid placementId, out string placerUid)
    {
        placementId = placement;
        placerUid = placer;
        return placement != Guid.Empty && !string.IsNullOrWhiteSpace(placer);
    }



    //// Updates the localized status key shown when a player inspects the plaque; it grants no authority.
    ////
    internal void SetStatus(string statusCode)
    {
        if (status == statusCode) return;
        status = statusCode;
        MarkDirty(true);
    }



    //// Adds the current server decision to the ordinary block-information panel.
    ////
    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
    {
        base.GetBlockInfo(forPlayer, dsc);
        dsc.AppendLine(Lang.Get("firstgearbank:" + status));
    }



    //// Persists the fixed label with placed-block metadata and status; the crafted item remains stateless.
    ////
    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        text = LabelKey;
        Translateable = true;
        base.ToTreeAttributes(tree);
        tree.SetInt("color", ColorUtil.BlackArgb);
        tree.SetString("firstgearbank:placement", placement.ToString("N"));
        tree.SetString("firstgearbank:placer", placer);
        tree.SetString("firstgearbank:status", status);
    }



    //// Forces fixed label attributes before native sign restoration, then restores server placement metadata.
    ////
    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        tree.SetString("text", LabelKey);
        tree.SetBool("translateable", true);
        tree.SetInt("color", ColorUtil.BlackArgb);
        base.FromTreeAttributes(tree, worldAccessForResolve);
        Guid.TryParse(tree.GetString("firstgearbank:placement"), out placement);
        placer = tree.GetString("firstgearbank:placer", string.Empty);
        status = tree.GetString("firstgearbank:status", "charter-status-checking");
    }



    //// Rejects all native sign-edit packets so clients cannot replace the fixed identity label.
    ////
    public override void OnReceivedClientPacket(IPlayer fromPlayer, int packetid, byte[] data)
    {
    }



    //// Decommissions this placement only when the block is removed; ordinary chunk unload invokes another callback.
    ////
    public override void OnBlockRemoved()
    {
        if (Api?.Side == EnumAppSide.Server)
            Api.ModLoader.GetModSystem<First_Gear_BankModSystem>().Charters?.Removed(Pos.Copy(), placement);
        base.OnBlockRemoved();
    }



    //// Forgets only the loaded object reference while retaining the persisted active branch and protection snapshot.
    ////
    public override void OnBlockUnloaded()
    {
        if (Api?.Side == EnumAppSide.Server)
            Api.ModLoader.GetModSystem<First_Gear_BankModSystem>().Charters?.Unloaded(Pos.Copy(), placement);
        base.OnBlockUnloaded();
    }



}
