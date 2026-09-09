/*
 * Persists the bounded set of banking notification IDs already submitted to the local player's chat display.
 * BankingClient selects an opaque server-generated world/player namespace, checks IDs before display, records them
 * afterward, and acknowledges only after this cache saves successfully.  No message text, player name, UID, balance,
 * or transaction authority is stored.  Each cache retains at most 2,048 GUIDs beneath the game's runtime Cache folder.
 *
 * Replacement uses a flushed temporary file in the same directory.  A missing or malformed receipt cache permits
 * duplicate display, never money replay or a guessed server acknowledgment.  Disk failure leaves notices unacknowledged
 * for retry, with one sanitized warning per failure episode through the game's existing file logger.
 * Display and file persistence are not atomic: a crash between them can still repeat a notice, as the spec allows.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Vintagestory.API.Client;

namespace FirstGearBank.Client;

/// Versioned local display evidence, containing opaque notice identifiers only and no financial or identity payloads.
internal sealed record BankingNoticeReceiptFile(int Version, Guid[] Ids);

/// Connection-local cache of a selected persisted world/player receipt file; BankingClient owns its lifecycle.
internal sealed class BankingNoticeReceipts
{
    private readonly ICoreClientAPI api;
    private readonly HashSet<Guid> ids = new();
    private readonly Queue<Guid> order = new();
    private string? scope;
    private string? path;
    private bool dirty;
    private bool warned;



    //// Borrows the client data root and logger without touching disk until the server supplies a receipt namespace.
    ////
    internal BankingNoticeReceipts(ICoreClientAPI api)
    {
        this.api = api;
    }



    //// Loads one bounded display cache after validating the opaque namespace as exactly 64 uppercase hex characters.
    //// Untrusted scope strings never become arbitrary filesystem paths; unreadable cache data grants no authority.
    ////
    internal void Select(string selected)
    {
        if (selected.Length != 64 || selected.Any(c => !char.IsAsciiHexDigitUpper(c))) throw new JsonException();
        if (scope == selected) return;
        Reset();
        scope = selected;
        path = Path.Combine(api.DataBasePath, "Cache", "firstgearbank", selected + ".json");
        if (!File.Exists(path)) return;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 131_072) throw new JsonException();
            var saved = JsonSerializer.Deserialize<BankingNoticeReceiptFile>(stream);
            if (saved is not { Version: 1, Ids.Length: <= 2048 }) throw new JsonException();
            foreach (var id in saved.Ids)
                if (id != Guid.Empty && ids.Add(id)) order.Enqueue(id);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Warn();
        }
    }



    //// Checks display evidence only; a cached notice does not imply any local authority over its financial operation.
    ////
    internal bool Contains(Guid id)
    {
        return ids.Contains(id);
    }



    //// Records an ID only after chat submission, evicting the oldest retained ID at the fixed cache bound.
    ////
    internal void RecordDisplayed(Guid id)
    {
        if (!ids.Add(id)) return;
        order.Enqueue(id);
        if (order.Count > 2048) ids.Remove(order.Dequeue());
        dirty = true;
    }



    //// Flushes new receipt evidence before the controller may acknowledge the corresponding server outbox entries.
    //// Failed writes preserve dirty memory for later retry and never convert delivery trouble into a money failure.
    ////
    internal bool Save()
    {
        if (!dirty) return true;
        if (path is null) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new BankingNoticeReceiptFile(1, order.ToArray()));
            using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(path + ".tmp", path, true);
            dirty = false;
            warned = false;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Warn();
            return false;
        }
    }



    //// Emits one sanitized cache warning through the engine logger, which writes timestamped client log records.
    ////
    private void Warn()
    {
        if (!warned) api.Logger.Warning("[firstgearbank] WARN client-notices: display receipt cache unavailable; notices may repeat.");
        warned = true;
    }



    //// Clears only connection-local references; persisted display evidence survives reconnects and world reloads.
    ////
    internal void Reset()
    {
        ids.Clear();
        order.Clear();
        scope = path = null;
        dirty = warned = false;
    }



}
