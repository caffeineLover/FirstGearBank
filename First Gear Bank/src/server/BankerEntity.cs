/*
 * Supplies the custom humanoid used by BankerLifecycle, including its shared client rendering and interaction hook.
 * Vanilla EntityDressedHumanoid contributes outfit assembly and ordinary humanoid animation, not a trader inventory,
 * shopping cart, or trader dialogue.  The entity asset selects a fixed clothed appearance from installed game assets.
 *
 * Server roster identity remains in private persisted entity attributes.  A watched interaction hint is separately
 * controlled by the existing session registry.  Right-click submits the local player's intent to BankingClient;
 * only server registration can authorize it.  This entity owns no deposits, reserves, customer IDs, or financial data.
 * Death drops nothing and never revives this NPC.  The lifecycle decides whether its active home gets a replacement.
 */

using First_Gear_Bank;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace FirstGearBank.Server;

/// Banking-only dressed humanoid shared by the engine's server entity and client renderer, never a trading subclass.
public sealed class BankerEntity : EntityDressedHumanoid
{
    internal const string BranchAttribute = "firstgearbank:home";
    internal const string SpawnAttribute = "firstgearbank:spawn";



    //// Initializes vanilla outfit/physics integration while preventing ordinary despawn of a living assigned Banker.
    //// Chunk unload remains allowed; it is handled separately from death by BankerLifecycle.
    ////
    public override void Initialize(EntityProperties properties, ICoreAPI api, long chunkIndex)
    {
        base.Initialize(properties, api, chunkIndex);
        AllowDespawn = !Alive;
    }



    //// Routes interaction to the mod's client ledger without invoking any vanilla buy/sell behavior.
    //// Attacks retain normal humanoid damage handling; right-click carries no client-supplied account authority.
    ////
    public override void OnInteract(EntityAgent byEntity, ItemSlot slot, Vec3d hitPosition, EnumInteractMode mode)
    {
        if (mode == EnumInteractMode.Interact)
        {
            if (Alive && Api is ICoreClientAPI client && byEntity.EntityId == client.World.Player.Entity.EntityId)
                Api.ModLoader.GetModSystem<First_Gear_BankModSystem>().Client?.OpenBanker(EntityId);
            return;
        }
        base.OnInteract(byEntity, slot, hitPosition, mode);
    }



    //// Allows the engine to remove a dead body normally while preserving its explicit death/despawn reason.
    //// Replacement scheduling is owned by the server roster event handlers, never by a vanilla revive behavior.
    ////
    public override void Die(EnumDespawnReason reason = EnumDespawnReason.Death, DamageSource? damageSourceForDeath = null)
    {
        AllowDespawn = true;
        base.Die(reason, damageSourceForDeath);
    }



    //// Returns no physical reserves or outfit loot; customers' money cannot exist in an NPC's drop collection.
    ////
    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos position, IPlayer byPlayer)
    {
        return [];
    }



}
