/*
 * Owns namespaced world-save staging for the banking core's versioned, checksummed authority envelope.
 * An independent installation marker distinguishes a genuinely absent first installation from an existing bank.
 * If the saved bank data cannot be read, this hobby mod logs the problem and starts a fresh bank rather than locking
 * everyone out.  Server administrators can restore any wanted gears through ordinary game administration.
 *
 * Every GameWorldSave event stages a fresh snapshot, even when the core reports no changes since serialization.
 * StoreData updates the game's pending save data; neither it nor serialization proves disk durability or atomicity
 * with player inventories.
 */

using System;
using System.Security.Cryptography;
using System.Text;
using FirstGearBank.Core;
using Vintagestory.API.Server;

namespace FirstGearBank.Server;

/// Save adapter for one world's bank.  The game owns the save object and disk commit; this object owns only the
/// decision to stage the current bank bytes.
internal sealed class WorldBankStorage
{
    private const string StateKey = "firstgearbank:authority-v1";
    private const string MarkerKey = "firstgearbank:installation-v1";
    private readonly ISaveGame save;
    private readonly ServerDiagnostics log;
    private readonly byte[] marker;
    private bool writable;



    //// Binds the installation marker to the game's stable world identifier before loading any monetary authority.
    ////
    public WorldBankStorage(ISaveGame save, ServerDiagnostics log)
    {
        this.save = save;
        this.log = log;
        marker = SHA256.HashData(Encoding.UTF8.GetBytes("firstgearbank/v1/" + save.SavegameIdentifier));
    }



    //// Restores existing authority or initializes a fresh bank when saved data is absent or unreadable.
    //// A marker mismatch is treated like unreadable data so normal play remains available.
    ////
    public BankingCoordinator? Load(IBankingHost host, IExactLiquidityIndex index, ServerSettings settings)
    {
        var bytes = save.GetData(StateKey);
        var storedMarker = save.GetData(MarkerKey);
        var markerMatches = storedMarker is null || storedMarker.AsSpan().SequenceEqual(marker);
        if (bytes is null && markerMatches)
        {
            var created = BankingCoordinator.Create(save.SavegameIdentifier, save.Seed, host, index,
                settings.Economics, settings.TimeBasis);
            writable = true;
            Stage(created);
            log.Write("INFO", "storage", "Initialized a bank and staged its installation marker.");
            return created;
        }

        var restored = bytes is null || !markerMatches ? null :
            BankingCoordinator.Restore(bytes, save.SavegameIdentifier, host, index);
        if (restored?.Bank is null)
        {
            log.Write("WARN", "storage", "Existing bank data could not be restored; starting a fresh bank.");
            var created = BankingCoordinator.Create(save.SavegameIdentifier, save.Seed, host, index,
                settings.Economics, settings.TimeBasis);
            writable = true;
            Stage(created);
            return created;
        }
        writable = true;
        return restored.Bank;
    }



    //// Restages the complete validated envelope during each save hook; serialization alone is never a clean flag.
    //// StoreData failures propagate to the host, which logs them and disables further monetary admission.
    ////
    public void Stage(BankingCoordinator bank)
    {
        if (!writable) return;
        var bytes = bank.Save();
        // Publish the marker first: a partial staging failure must look established, not like a fresh installation.
        save.StoreData(MarkerKey, marker);
        save.StoreData(StateKey, bytes);
    }



}
