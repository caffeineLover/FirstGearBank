/*
 * Writes and reads bounded registry-recovery artifacts beneath the server data directory.  Structured healthy
 * snapshots use JSON for administrator review; quarantined bytes remain byte-for-byte binary evidence.  Filenames are
 * generated or reduced to a single leaf name so chat input cannot escape the dedicated recovery directory.
 *
 * This adapter does not decide whether recovery is allowed and never mutates bank state.  The coordinator validates
 * world binding and registry contents, while FirstGearBankServer supplies permission and audit attribution.
 */

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using FirstGearBank.Core;

namespace FirstGearBank.Server;

/// Server-only filesystem boundary for registry evidence and separately verified restore snapshots.
internal sealed class RegistryRecoveryStorage
{
    private readonly string directory;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 32 };



    //// Selects one fixed recovery directory without creating or reading it until an administrator requests an action.
    ////
    internal RegistryRecoveryStorage(string dataDirectory)
    {
        directory = Path.Combine(dataDirectory, "ModData", "FirstGearBank", "registry-recovery");
    }



    //// Creates a unique artifact and returns only its leaf filename for safe command feedback.
    ////
    internal string Export(RegistryRecoveryExport export)
    {
        Directory.CreateDirectory(directory);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff");
        var extension = export.QuarantinedBytes.IsEmpty ? ".json" : ".bin";
        var name = $"registry-{stamp}-{export.Sha256[..12]}{extension}";
        var path = Path.Combine(directory, name);
        var bytes = export.QuarantinedBytes.IsEmpty ?
            JsonSerializer.SerializeToUtf8Bytes(export.Snapshot, JsonOptions) : export.QuarantinedBytes.ToArray();
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        return name;
    }



    //// Reads one bounded JSON snapshot from the fixed directory; binary quarantine exports are deliberately unrestorable.
    ////
    internal RegistryRecoverySnapshot ReadSnapshot(string filename)
    {
        var leaf = Path.GetFileName(filename);
        if (leaf != filename || !leaf.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || leaf.Length > 128)
            throw new BankException(BankError.InvalidAmount);
        var path = Path.Combine(directory, leaf);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > 16_777_216) throw new BankException(BankError.CorruptState);
        return JsonSerializer.Deserialize<RegistryRecoverySnapshot>(File.ReadAllBytes(path), JsonOptions) ??
            throw new BankException(BankError.CorruptState);
    }



}
