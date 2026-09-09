/*
 * Provides the server adapter's bounded diagnostic sink beneath the game's runtime data directory.
 * Each record contains a UTC timestamp, the project's four-letter severity, and a component name.  One active file
 * and one previous file limit retention to approximately two MiB.  Callers provide operational descriptions, never
 * raw packets, account snapshots, inventory evidence, player UIDs, or confirmation tokens.
 *
 * The service opens files only while writing and serializes rotation with a private gate.  File failures are reported
 * through the game logger; they do not escape into an inventory rollback or prevent unrelated server shutdown work.
 */

using System;
using System.IO;
using Vintagestory.API.Common;

namespace FirstGearBank.Server;

/// World-host diagnostic writer with bounded retention and a game-log fallback for inaccessible log storage.
/// It owns no persistent stream and can safely outlive the coordinator during final shutdown diagnostics.
internal sealed class ServerDiagnostics
{
    private readonly object gate = new();
    private readonly string path;
    private readonly ILogger fallback;



    //// Binds diagnostics to the host data directory without opening or modifying any bank authority.
    ////
    public ServerDiagnostics(ICoreAPI api)
    {
        path = Path.Combine(api.DataBasePath, "logs", "firstgearbank.log");
        fallback = api.Logger;
    }



    //// Appends one sanitized diagnostic and rotates the previous generation at one MiB.
    //// Only owned log files are replaced.  The fallback reports the failure category without private input data.
    ////
    public void Write(string severity, string component, string message)
    {
        if (severity is not ("INFO" or "DEBG" or "WARN" or "CRIT"))
            throw new ArgumentOutOfRangeException(nameof(severity));
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length >= 1_048_576)
                    File.Move(path, path + ".previous", true);
                var safe = message.Replace('\r', ' ').Replace('\n', ' ');
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {severity} {component} {safe}\n");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                fallback.Warning("[firstgearbank] WARN Diagnostic file unavailable: {0}", error.GetType().Name);
            }
        }
    }



}
