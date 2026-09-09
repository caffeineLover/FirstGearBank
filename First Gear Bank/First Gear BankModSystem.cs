/*
 * Connects Vintage Story's mod lifecycle to the world-scoped banking server adapter.
 * Server startup creates the host responsible for persistence, authentication, clocks, networking, and inventory
 * settlement.  Client startup creates the disposable ledger UI and request controller; Banker spawning remains a
 * separate content integration.  No money calculation or mutable customer state belongs in this entry point.
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

namespace First_Gear_Bank;

/// Thin engine entry point exposing the server adapter to trusted Banker content without exposing core account state.
/// Client instances never create a banking coordinator and cannot select a server-side liquidity implementation.
public class First_Gear_BankModSystem : ModSystem
{
    public FirstGearBankServer? Server { get; private set; }
    public BankingClient? Client { get; private set; }
    public IExactLiquidityIndex? LiquidityIndex { get; set; }



    //// Creates the authoritative adapter once the server API is available and before normal world gameplay begins.
    ////
    public override void StartServerSide(ICoreServerAPI api)
    {
        Server = new(api, LiquidityIndex);
    }



    //// Creates the native banking ledger and interaction controller; no financial authority is loaded on the client.
    //// Trusted Banker content can call Client.OpenBanker, or use the server registration's replicated right-click hint.
    ////
    public override void StartClientSide(ICoreClientAPI api)
    {
        Client = new(api);
    }



    //// Releases the appropriate side's event subscriptions and dialog resources when the game unloads this mod instance.
    ////
    public override void Dispose()
    {
        Server?.Dispose();
        Server = null;
        Client?.Dispose();
        Client = null;
        base.Dispose();
    }



}
