/*
 * Owns namespaced world-save staging for the banking core's versioned, checksummed authority envelope.
 * An independent installation marker distinguishes a genuinely absent first installation from missing established
 * authority.  Unreadable, foreign-world, or unsupported bytes remain untouched; startup never replaces them with an
 * empty bank.  A successful core restore may separately quarantine recipient services or unresolved item settlements.
 *
 * Every GameWorldSave event stages a fresh snapshot, even when the core reports no changes since serialization.
 * StoreData updates the game's pending save data; neither it nor serialization proves disk durability or atomicity
 * with player inventories.  The adapter therefore leaves ambiguous cross-save physical settlements quarantined.
 */

using System;
using System.Security.Cryptography;
using System.Text;
using FirstGearBank.Core;
using Vintagestory.API.Server;

namespace FirstGearBank.Server;

/// Guarded save adapter for one world's bank, with no recovery path that silently discards established authority.
/// The game owns the save object and disk commit; this object owns only the decision to stage validated bank bytes.
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



    //// Restores existing authority or initializes only when both the bank and installation marker are absent.
    //// A marker mismatch or failed restore disables writes and leaves all original game-save entries intact.
    ////
    public BankingCoordinator? Load(IBankingHost host, IExactLiquidityIndex index, ServerSettings settings)
    {
        var bytes = save.GetData(StateKey);
        var storedMarker = save.GetData(MarkerKey);
        if (storedMarker is not null && !storedMarker.AsSpan().SequenceEqual(marker))
        {
            log.Write("CRIT", "storage", "Installation marker is invalid or belongs to another world; banking is disabled.");
            return null;
        }
        if (bytes is null)
        {
            if (storedMarker is not null)
            {
                log.Write("CRIT", "storage", "Established bank authority is missing; no replacement bank was created.");
                return null;
            }
            var created = BankingCoordinator.Create(save.SavegameIdentifier, save.Seed, host, index,
                settings.Economics, settings.TimeBasis);
            writable = true;
            Stage(created);
            log.Write("INFO", "storage", "Initialized a first-install bank and staged its installation marker.");
            return created;
        }

        var restored = BankingCoordinator.Restore(bytes, save.SavegameIdentifier, host, index);
        if (restored.Bank is null)
        {
            log.Write("CRIT", "storage", "Existing bank could not be restored; original authority bytes are preserved.");
            return null;
        }
        writable = true;
        if (restored.Error != BankError.None)
            log.Write("WARN", "storage", $"Restored bank with restricted services: {restored.Error}.");
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
