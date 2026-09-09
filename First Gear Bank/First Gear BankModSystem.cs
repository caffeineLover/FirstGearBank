/*
 * Connects Vintage Story's mod lifecycle to the world-scoped banking server adapter.
 * Server startup creates the host responsible for persistence, authentication, clocks, networking, and inventory
 * settlement.  The client registers only the shared transport contract here; account UI and Banker content remain
 * separate integrations.  No money calculation or mutable customer state belongs in this entry point.
 *
 * LiquidityIndex is an optional trusted integration supplied before server startup.  Without one, the adapter requires
 * an explicit configuration opt-in for the scan-based fallback.  Disposal stages authority and removes callbacks;
 * the engine, not this mod entry point, owns final save durability and transport lifetime.
 */

using FirstGearBank.Core;
using FirstGearBank.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace First_Gear_Bank;

/// Thin engine entry point exposing the server adapter to trusted Banker content without exposing core account state.
/// Client instances never create a banking coordinator and cannot select a server-side liquidity implementation.
public class First_Gear_BankModSystem : ModSystem
{
    public FirstGearBankServer? Server { get; private set; }
    public IExactLiquidityIndex? LiquidityIndex { get; set; }



    //// Creates the authoritative adapter once the server API is available and before normal world gameplay begins.
    ////
    public override void StartServerSide(ICoreServerAPI api)
    {
        Server = new(api, LiquidityIndex);
    }



    //// Registers the shared packet type in server order on the client; no financial authority is loaded.
    //// A future client UI attaches its response handler to this channel and submits only intent packets.
    ////
    public override void StartClientSide(ICoreClientAPI api)
    {
        api.Network.RegisterChannel("firstgearbank-v1").RegisterMessageType<BankingPacket>();
    }



    //// Releases the server adapter's event subscriptions when the game unloads this mod instance.
    ////
    public override void Dispose()
    {
        Server?.Dispose();
        Server = null;
        base.Dispose();
    }



}
