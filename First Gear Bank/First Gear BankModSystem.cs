/*
 * Connects Vintage Story's mod lifecycle to the world-scoped banking server adapter.
 * Server startup creates the host responsible for persistence, authentication, clocks, networking, and inventory
 * settlement.  Separate server lifecycles own Banker homes, NPC replacement, Charter premises, and exact protection.
 * Client startup creates the ledger UI; shared startup registers the custom entity, Charter block, and AI task.
 * No money calculation, room validation, or financial state belongs in this composition root.
 *
 * LiquidityIndex is an optional trusted integration supplied before server startup.  Without one, the adapter uses
 * the approved exact checkpoint scanner.  Disposal stages authority and removes callbacks;
 * the engine, not this mod entry point, owns final save durability and transport lifetime.
 */

using FirstGearBank.Core;
using FirstGearBank.Client;
using FirstGearBank.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace First_Gear_Bank;

/// Thin engine entry point exposing the server adapter to trusted Banker content without exposing core account state.
/// Client instances never create a banking coordinator and cannot select a server-side liquidity implementation.
public class First_Gear_BankModSystem : ModSystem
{
    public FirstGearBankServer? Server { get; private set; }
    public BankingClient? Client { get; private set; }
    public BankerLifecycle? Bankers { get; private set; }
    public CharterLifecycle? Charters { get; private set; }
    public IExactLiquidityIndex? LiquidityIndex { get; set; }



    //// Registers mod-owned entity and AI types on both sides before the engine loads their JSON assets.
    //// Vanilla humanoid rendering is reused without registering or patching any trading behavior.
    ////
    public override void Start(ICoreAPI api)
    {
        api.RegisterEntity("FirstGearBankBanker", typeof(BankerEntity));
        api.RegisterBlockClass("FirstGearBankCharter", typeof(BankerCharterBlock));
        api.RegisterBlockEntityClass("FirstGearBankCharterEntity", typeof(BankerCharterBlockEntity));
        AiTaskRegistry.Register<BankerReturnHomeTask>("firstgearbank-returnhome");
    }



    //// Creates the authoritative adapter once the server API is available and before normal world gameplay begins.
    ////
    public override void StartServerSide(ICoreServerAPI api)
    {
        Server = new(api, LiquidityIndex);
        Bankers = new(api, Server);
        Charters = new(api, Server, Bankers);
    }



    //// Creates the native banking ledger and interaction controller; no financial authority is loaded on the client.
    //// Banker content can call Client.OpenBanker or use the server registration's replicated right-click hint.
    ////
    public override void StartClientSide(ICoreClientAPI api)
    {
        Client = new(api);
    }



    //// Releases the appropriate side's event subscriptions and dialogs when the game unloads this mod instance.
    ////
    public override void Dispose()
    {
        Charters?.Dispose();
        Charters = null;
        Bankers?.Dispose();
        Bankers = null;
        Server?.Dispose();
        Server = null;
        Client?.Dispose();
        Client = null;
        base.Dispose();
    }



}
