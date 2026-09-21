/*
 * Owns loaded Banker registration, persistent NPC homes, and replacement scheduling for one server world.
 * Branch content supplies a validated anchor/interior through EstablishHome; an administrator may also create a
 * one-cell manual home for in-game use.  Neither route grants land protection, Charter status, ownership privileges,
 * or access to customer accounts.  FirstGearBankServer independently authenticates each financial request.
 *
 * The roster and private entity attributes share a spawn-operation identity.  Reservations are staged before entity
 * creation; observed matching entities reconcile after load.  Missing assigned entities remain assigned because they
 * may be unloaded.  Contradictory evidence disables that home instead of guessing, respawning, or touching money.
 * Explicit death samples one independent deterministic replacement delay from current configuration and persists its
 * progress in ordinary world days.  Unload never starts a timer, and administrative removal never gets a replacement.
 *
 * Spawning requires loaded, unobstructed standing cells.  The lifecycle never force-loads terrain or repairs blocks.
 * Home selection is deterministic and feeds the native return-home AI; normal idle behavior stays at that workstation.
 * DetachHome preserves an existing Banker's service until the saved 17:00 cutoff, but confers no continuing protection.
 * Charter authority gates only an initial Ready spawn and routes its NPC away from the manual removal shortcut.
 * Startup/save/disposal follow the engine lifecycle; staging remains distinct from atomic entity/world disk durability.
 */

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace FirstGearBank.Server;

/// Server-thread NPC authority, owned by the mod system separately from the financial coordinator and GUI.
public sealed class BankerLifecycle : IDisposable
{
    private readonly ICoreServerAPI api;
    private readonly FirstGearBankServer banking;
    private readonly ServerDiagnostics log;
    private readonly Dictionary<Guid, Dictionary<long, BankerEntity>> loadedEntities = new();
    private readonly HashSet<long> registered = new();
    private readonly long tickListener;
    private Dictionary<Guid, BankerHome> homes = new();
    private BankerRosterStorage? storage;
    private System.Func<Guid, bool>? initialStaffingGate;
    private System.Func<Guid, bool>? managedBranchProbe;
    private int serverThread;
    private bool loaded;
    private bool failed;
    private bool disposed;



    //// Subscribes to entity/save events before gameplay; the financial adapter supplies only session registration.
    //// The roster is loaded at RunGame, after the engine restores its calendar and the bank establishes its thread.
    ////
    public BankerLifecycle(ICoreServerAPI api, FirstGearBankServer banking)
    {
        this.api = api;
        this.banking = banking;
        log = new(api);
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, Load);
        api.Event.OnEntitySpawn += Track;
        api.Event.OnEntityLoaded += Track;
        api.Event.OnEntityDeath += Died;
        api.Event.OnEntityDespawn += Despawned;
        api.Event.GameWorldSave += Save;
        tickListener = api.Event.RegisterGameTickListener(Tick, 1000);
        api.ChatCommands.Create("bankernpc").RequiresPrivilege(Privilege.controlserver)
            .WithDescription("Administrator Banker placement or removal; does not create protected branches")
            .WithArgs(api.ChatCommands.Parsers.All("place | remove reason")).HandleWith(AdminCommand);
    }



    //// Installs the Charter authority consulted immediately before a newly established home may spawn its first NPC.
    //// A null gate restores the default used by manual and non-Charter homes; replacement staffing is unaffected.
    ////
    internal void SetInitialStaffingGate(System.Func<Guid, bool>? gate)
    {
        initialStaffingGate = gate;
    }



    //// Installs the branch-authority probe that keeps the manual NPC command from orphaning Charter protection.
    //// Trusted lifecycle removal remains available through RemoveHome; only the administrator shortcut consults it.
    ////
    internal void SetManagedBranchProbe(System.Func<Guid, bool>? probe)
    {
        managedBranchProbe = probe;
    }



    //// Restores NPC authority before reconciling loaded entities; failed bytes never authorize a replacement roster.
    //// A single startup enumeration handles entities loaded before this mod received its RunGame callback.
    ////
    private void Load()
    {
        if (loaded || disposed) return;
        loaded = true;
        serverThread = Environment.CurrentManagedThreadId;
        try
        {
            storage = new(api.WorldManager.SaveGame, log);
            var restored = storage.Load();
            if (restored is null) { failed = true; return; }
            homes = restored;
            foreach (var entity in api.World.LoadedEntities.Values.OfType<BankerEntity>()) Track(entity);
            Save();
            log.Write("INFO", "banker-lifecycle", "NPC roster loaded; safe home reconciliation is active.");
        }
        catch (Exception error) { Disable(error); }
    }



    //// Establishes a trusted content-supplied NPC home after the caller validates/reserves its branch premises.
    //// Repeated identical calls are idempotent; a removed branch identity cannot be reused for a successor placement.
    //// Manual administrator placement uses this same path but supplies no claim or Charter authority.
    ////
    public void EstablishHome(Guid branch, BlockPos anchor, IReadOnlyCollection<BlockPos> interior)
    {
        RequireReady();
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(interior);
        // Copy caller-owned positions and canonicalize ordering before identity and overlap comparisons.
        var cells = interior.Select(BankerRosterStorage.Cell).Distinct().OrderBy(cell => cell.X)
            .ThenBy(cell => cell.Y).ThenBy(cell => cell.Z).ToImmutableArray();
        if (cells.IsEmpty) throw new ArgumentException("A Banker needs at least one permitted standing cell.");
        var logicalAnchor = BankerRosterStorage.Cell(anchor);
        var standing = PreferredStanding(cells, logicalAnchor);
        var home = new BankerHome(branch, logicalAnchor, standing, cells, BankerDisposition.Ready, Guid.Empty, 0,
            0, 0, 0, api.World.Calendar.TotalDays);
        BankerRosterStorage.Validate(home);
        // Replayed establishment may confirm the same home, but cannot revive or repurpose a retired identity.
        if (homes.TryGetValue(branch, out var prior))
        {
            if (prior.Disposition is not (BankerDisposition.Removed or BankerDisposition.Quarantined) &&
                prior.DepartureDay is null && prior.Anchor == logicalAnchor && prior.Interior.SequenceEqual(cells)) return;
            throw new InvalidOperationException("The branch identity already has a different or retired home.");
        }
        if (homes.Values.Any(row => row.DepartureDay is null && row.Disposition != BankerDisposition.Removed &&
                row.Interior.Intersect(cells).Any()))
            throw new InvalidOperationException("This standing region overlaps another Banker home.");
        homes.Add(branch, home);
        try { Stage(); }
        catch
        {
            homes.Remove(branch);
            throw;
        }
    }



    //// Ends a Charter assignment without changing claims or finance, allowing only its existing NPC until 17:00.
    //// The caller releases Charter topology/spacing separately.  Pending unstaffed homes are cancelled immediately.
    ////
    public void DetachHome(Guid branch)
    {
        var now = api.World.Calendar.TotalDays;
        // Displayed hours always span 24 clock hours, even when the engine uses a different HoursPerDay.
        var cutoff = Math.Max(now, Math.Floor(now) + 17.0 / 24.0);
        DetachHome(branch, cutoff);
    }



    //// Replays a caller-persisted Charter cutoff so a crash between separate save sections cannot extend service.
    ////
    internal void DetachHome(Guid branch, double cutoff)
    {
        RequireReady();
        if (!double.IsFinite(cutoff) || cutoff < 0) throw new ArgumentOutOfRangeException(nameof(cutoff));
        if (!homes.TryGetValue(branch, out var home) || home.DepartureDay is not null ||
            home.Disposition == BankerDisposition.Removed) return;
        homes[branch] = home.Disposition == BankerDisposition.Assigned ? home with { DepartureDay = cutoff } :
            home with { Disposition = BankerDisposition.Removed, RemainingDays = 0 };
        Stage();
        Tick(0);
    }



    //// Retires this home before despawning its loaded NPC, cancelling every replacement or departure timer.
    //// A nonblank reason is mandatory for diagnostics; callers release branch-specific protections independently.
    ////
    public void RemoveHome(Guid branch, string reason)
    {
        RequireReady();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512)
            throw new ArgumentException("A removal reason of at most 512 characters is required.");
        if (!homes.TryGetValue(branch, out var home)) return;
        if (home.Disposition == BankerDisposition.Removed) return;
        homes[branch] = home with { Disposition = BankerDisposition.Removed, RemainingDays = 0, DepartureDay = null };
        Stage();
        RemoveLoaded(branch);
        log.Write("INFO", "banker-lifecycle", "Banker home administratively retired: " + reason);
    }



    //// Reports persisted assignment state for Charter presentation without exposing entity or financial authority.
    ////
    internal bool IsHomeStaffed(Guid branch)
    {
        return loaded && !failed && !disposed && homes.TryGetValue(branch, out var home) &&
            home.Disposition == BankerDisposition.Assigned;
    }



    //// Reports durable evidence that this home completed its first assignment, even during death replacement vacancy.
    //// A reserved or merely ready initial operation does not count; its premises must remain under the Charter gate.
    ////
    internal bool HasEverBeenStaffed(Guid branch)
    {
        return loaded && !failed && !disposed && homes.TryGetValue(branch, out var home) &&
            (home.EntityId > 0 || home.Generation > 0 || home.Disposition == BankerDisposition.Assigned);
    }



    //// Records loaded tagged entities without granting access or creating homes from arbitrary entity attributes.
    //// Duplicate spawn/load events are harmless; the next reconciliation considers all candidates together.
    ////
    private void Track(Entity entity)
    {
        if (disposed || entity is not BankerEntity banker) return;
        if (!Guid.TryParse(banker.Attributes.GetString(BankerEntity.BranchAttribute), out var branch))
        {
            banker.WatchedAttributes.SetBool("firstgearbank:banker", false);
            return;
        }
        if (!loadedEntities.TryGetValue(branch, out var entries)) loadedEntities[branch] = entries = new();
        if (entries.TryGetValue(banker.EntityId, out var prior) && ReferenceEquals(prior, banker)) return;
        if (loaded && registered.Contains(banker.EntityId)) Unregister(banker.EntityId);
        banker.WatchedAttributes.SetBool("firstgearbank:banker", false);
        entries[banker.EntityId] = banker;
    }



    //// Invalidates service immediately on explicit death and schedules only the matching active assignment once.
    //// Death of a detached or retired Banker cannot create a replacement or modify customer finance.
    ////
    private void Died(Entity entity, DamageSource damageSource)
    {
        if (!loaded || failed || disposed || entity is not BankerEntity banker) return;
        try
        {
            Unregister(banker.EntityId);
            if (!FindAssignment(banker, out var home) || home.Disposition != BankerDisposition.Assigned) return;
            if (home.DepartureDay is not null)
                homes[home.Branch] = home with { Disposition = BankerDisposition.Removed, DepartureDay = null };
            else
            {
                // A separate hash domain prevents NPC replacement draws from consuming the interest-rate stream.
                // Generation advances only on the first matching death, so reloads cannot reroll this duration.
                var generation = checked(home.Generation + 1);
                var range = banking.ReplacementDelayRange;
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("firstgearbank/banker-replacement/v1/" +
                    api.WorldManager.SaveGame.Seed.ToString(CultureInfo.InvariantCulture) + "/" +
                    home.Branch.ToString("N") + "/" + generation.ToString(CultureInfo.InvariantCulture)));
                var uniform = (BinaryPrimitives.ReadUInt64BigEndian(bytes) >> 11) / 9007199254740992.0;
                var delay = range.Minimum + (range.Maximum - range.Minimum) * uniform;
                homes[home.Branch] = home with { Disposition = BankerDisposition.Waiting, Generation = generation,
                    SampledDays = delay, RemainingDays = delay, LastCalendarDay = api.World.Calendar.TotalDays };
            }
            Stage();
        }
        catch (Exception error) { Disable(error); }
    }



    //// Distinguishes unload from actual death/removal while removing only the disappearing loaded instance.
    //// The entity ID remains assigned across unload; a later load must reconcile it before anyone can spawn again.
    ////
    private void Despawned(Entity entity, EntityDespawnData reason)
    {
        if (disposed || entity is not BankerEntity banker) return;
        try
        {
            if (loaded && !failed)
            {
                Unregister(banker.EntityId);
                if (reason.Reason is EnumDespawnReason.Death or EnumDespawnReason.Combusted) Died(banker, reason.DamageSourceForDeath);
                else if (reason.Reason is not (EnumDespawnReason.Unload or EnumDespawnReason.Disconnect or EnumDespawnReason.OutOfRange) &&
                    FindAssignment(banker, out var home) && home.Disposition == BankerDisposition.Assigned)
                {
                    // Cleanup of an already dead body's Waiting record cannot cancel its scheduled replacement.
                    homes[home.Branch] = home with { Disposition = BankerDisposition.Removed,
                        RemainingDays = 0, DepartureDay = null };
                    Stage();
                }
            }
            if (Guid.TryParse(banker.Attributes.GetString(BankerEntity.BranchAttribute), out var branch) &&
                loadedEntities.TryGetValue(branch, out var entries)) entries.Remove(banker.EntityId);
        }
        catch (Exception error) { Disable(error); }
    }



    //// Advances ordinary-day timers and reconciles only known homes/loaded Bankers, never the financial account set.
    //// Simulation jumps cross departure deadlines on this update; no periodic task force-loads an anchor chunk.
    ////
    private void Tick(float elapsedSeconds)
    {
        if (!loaded && api.Server.CurrentRunPhase == EnumServerRunPhase.RunGame) Load();
        if (!loaded || failed || disposed) return;
        try
        {
            foreach (var saved in homes.Values.ToArray())
            {
                var home = AdvanceTimer(saved);
                if (home.Disposition == BankerDisposition.Assigned && home.DepartureDay is { } cutoff &&
                    api.World.Calendar.TotalDays >= cutoff)
                {
                    homes[home.Branch] = home with { Disposition = BankerDisposition.Removed,
                        RemainingDays = 0, DepartureDay = null };
                    Stage();
                    RemoveLoaded(home.Branch);
                    continue;
                }
                Reconcile(home);
            }
        }
        catch (Exception error) { Disable(error); }
    }



    //// Uses forward ordinary-calendar progress only; configuration reloads never change an already sampled duration.
    //// A backward calendar edit reanchors the next interval without subtracting progress or rerolling the wait.
    ////
    private BankerHome AdvanceTimer(BankerHome home)
    {
        if (home.Disposition != BankerDisposition.Waiting) return home;
        var now = api.World.Calendar.TotalDays;
        home = home with { RemainingDays = Math.Max(0, home.RemainingDays - Math.Max(0, now - home.LastCalendarDay)),
            LastCalendarDay = now };
        homes[home.Branch] = home;
        return home;
    }



    //// Requires matching branch, spawn operation, and entity identity before granting service or completing a spawn.
    //// Absence is not evidence of death.  Conflicting living entities are preserved but denied service.
    ////
    private void Reconcile(BankerHome home)
    {
        var entries = loadedEntities.GetValueOrDefault(home.Branch);
        var living = entries?.Values.Where(entity => entity.Alive).ToArray() ?? [];
        if (home.Disposition == BankerDisposition.Removed) { RemoveLoaded(home.Branch); return; }
        if (home.Disposition == BankerDisposition.Quarantined) return;
        // Evaluate all loaded candidates together before adopting an entity or issuing another spawn operation.
        if (living.Length > 1 || living.Length == 1 &&
            (!Guid.TryParse(living[0].Attributes.GetString(BankerEntity.SpawnAttribute), out var spawn) ||
                spawn != home.SpawnId || home.Disposition is BankerDisposition.Ready or BankerDisposition.Waiting ||
                home.EntityId != 0 && living[0].EntityId != home.EntityId))
        {
            Quarantine(home);
            return;
        }
        if (living.Length == 1)
        {
            var entity = living[0];
            if (home.Disposition == BankerDisposition.Reserved)
            {
                home = home with { Disposition = BankerDisposition.Assigned, EntityId = entity.EntityId };
                homes[home.Branch] = home;
                Stage();
            }
            if (registered.Add(entity.EntityId))
                banking.RegisterBanker(home.Branch, entity, () => Available(entity));
            return;
        }
        if (home.Disposition == BankerDisposition.Assigned)
        {
            // A loaded dead body without a recorded death is contradictory; an unloaded living NPC is not.
            if (entries?.ContainsKey(home.EntityId) == true) Quarantine(home);
            return;
        }
        if (!api.World.IsFullyLoadedChunk(BankerRosterStorage.Position(home.Anchor))) return;
        if (home.Disposition == BankerDisposition.Reserved) { Quarantine(home); return; }
        if (home.Disposition == BankerDisposition.Ready ||
            home.Disposition == BankerDisposition.Waiting && home.RemainingDays <= 0)
            Spawn(home);
    }



    //// Checks safety and initial Charter permission, stages a reservation, then records the engine-assigned entity ID.
    //// Failure after reservation leaves recoverable evidence rather than permission to spawn another NPC.
    ////
    private void Spawn(BankerHome home)
    {
        var standing = SafeStanding(home);
        if (standing is null) return;
        var position = BankerRosterStorage.Center(standing);
        if (api.World.GetEntitiesAround(position, 0.8f, 2, entity => entity.Alive && entity is EntityAgent).Length != 0) return;
        // Charter premises are checked only after physical spawn preconditions pass and immediately before reservation.
        if (home.Disposition == BankerDisposition.Ready && initialStaffingGate?.Invoke(home.Branch) == false) return;
        var type = api.World.GetEntityType(new AssetLocation("firstgearbank:banker")) ??
            throw new InvalidOperationException("Banker entity asset is unavailable.");
        var entity = api.ClassRegistry.CreateEntity(type) as BankerEntity ??
            throw new InvalidOperationException("Banker class registration is unavailable.");
        // Publish reservation evidence before spawning; a failed later step cannot justify repeating world mutation.
        home = home with { Disposition = BankerDisposition.Reserved, Standing = standing, SpawnId = Guid.NewGuid(), EntityId = 0 };
        homes[home.Branch] = home;
        Stage();
        entity.Attributes.SetString(BankerEntity.BranchAttribute, home.Branch.ToString("N"));
        entity.Attributes.SetString(BankerEntity.SpawnAttribute, home.SpawnId.ToString("N"));
        entity.Pos.SetPos(standing.X + 0.5, standing.Y, standing.Z + 0.5);
        entity.Pos.Dimension = standing.Dimension;
        api.World.SpawnEntity(entity);
        // Only the accepted engine identity can complete this reservation and receive banking session authority.
        if (entity.EntityId <= 0 || !ReferenceEquals(api.World.GetEntityById(entity.EntityId), entity))
            throw new InvalidOperationException("Banker spawn was not accepted by the engine.");
        homes[home.Branch] = home with { Disposition = BankerDisposition.Assigned, EntityId = entity.EntityId };
        Stage();
        Track(entity);
        Reconcile(homes[home.Branch]);
    }



    //// Resolves a safe current home for the native AI without authorizing new entities or crossing dimensions.
    //// A blocked standing cell selects the nearest safe permitted interior deterministically and persists that choice.
    ////
    internal bool TryHomeTarget(BankerEntity entity, out Vec3d target, int alternative = 0)
    {
        target = null!;
        if (!loaded || failed || disposed || !FindAssignment(entity, out var home) ||
            home.Disposition != BankerDisposition.Assigned || entity.Pos.Dimension != home.Anchor.Dimension) return false;
        // A central workstation keeps the Banker in the room without making the Charter plaque his focal point.
        if (alternative == 0)
        {
            var safe = home.Interior.Where(SafeCell).ToImmutableArray();
            if (!safe.IsEmpty)
            {
                var preferred = PreferredStanding(safe, home.Anchor);
                if (preferred != home.Standing) homes[home.Branch] = home = home with { Standing = preferred };
                target = BankerRosterStorage.Center(preferred);
                return true;
            }
        }
        var candidates = home.Interior.Where(SafeCell).OrderBy(cell => Distance(cell, home.Standing))
            .ThenBy(cell => cell.X).ThenBy(cell => cell.Y).ThenBy(cell => cell.Z).ToArray();
        if (candidates.Length == 0 && home.DepartureDay is not null)
        {
            // Detached NPCs claim no region; a bounded local search may use safe nearby space during renovation.
            var center = BankerRosterStorage.Cell(entity.Pos.AsBlockPos);
            candidates = (from x in Enumerable.Range(-3, 7)
                from y in Enumerable.Range(-1, 3)
                from z in Enumerable.Range(-3, 7)
                select new BankerCell(center.X + x, center.Y + y, center.Z + z, center.Dimension))
                .Where(cell => cell.Y is > 0 and < 32766 && SafeCell(cell))
                .OrderBy(cell => Distance(cell, center)).ThenBy(cell => cell.X).ThenBy(cell => cell.Y)
                .ThenBy(cell => cell.Z).ToArray();
        }
        if (candidates.Length == 0) return false;
        var standing = candidates[alternative % candidates.Length];
        if (alternative == 0 && standing != home.Standing && home.Interior.Contains(standing))
            homes[home.Branch] = home with { Standing = standing };
        target = BankerRosterStorage.Center(standing);
        return true;
    }



    //// Retains a reached safe interior workstation after navigation tried an alternative to an obstructed route.
    //// Detached movement outside the former interior remains ephemeral and cannot create a new protected home.
    ////
    internal void ReachedHome(BankerEntity entity)
    {
        if (!loaded || failed || disposed || !FindAssignment(entity, out var home) ||
            home.Disposition != BankerDisposition.Assigned) return;
        var cell = BankerRosterStorage.Cell(entity.Pos.AsBlockPos);
        if (home.Interior.Contains(cell) && SafeCell(cell)) homes[home.Branch] = home with { Standing = cell };
    }



    //// Prefers the room's central safe workstation so new or replacement Bankers do not spawn against the Charter.
    //// No safe cell leaves the existing assignment intact for later repair/load rather than spawning a second NPC.
    ////
    private BankerCell? SafeStanding(BankerHome home)
    {
        var safe = home.Interior.Where(SafeCell).ToImmutableArray();
        return safe.IsEmpty ? null : PreferredStanding(safe, home.Anchor);
    }



    //// Checks loaded feet/head/floor, dry standing space, solid support, and the complete humanoid collision box.
    //// All coordinates remain dimension-aware; querying safety never changes blocks or requests chunk loading.
    ////
    private bool SafeCell(BankerCell cell)
    {
        var position = BankerRosterStorage.Position(cell);
        var above = position.UpCopy();
        var below = position.DownCopy();
        if (!api.World.IsFullyLoadedChunk(position) || !api.World.IsFullyLoadedChunk(above) ||
            !api.World.IsFullyLoadedChunk(below)) return false;
        var blocks = api.World.BlockAccessor;
        if (!blocks.GetBlock(below).SideSolid[BlockFacing.UP.Index] ||
            blocks.GetBlock(position, BlockLayersAccess.Fluid).LiquidLevel != 0 ||
            blocks.GetBlock(above, BlockLayersAccess.Fluid).LiquidLevel != 0) return false;
        return !api.World.CollisionTester.IsColliding(blocks, new Cuboidf(-0.3f, 0, -0.3f, 0.3f, 1.75f, 0.3f),
            BankerRosterStorage.Center(cell), false);
    }



    //// Computes a stable geometric ordering within one dimension without rounding distances to whole blocks.
    ////
    private static double Distance(BankerCell left, BankerCell right)
    {
        return Math.Pow((double)left.X - right.X, 2) + Math.Pow((double)left.Y - right.Y, 2) +
            Math.Pow((double)left.Z - right.Z, 2);
    }



    //// Chooses the safe cell nearest the room centroid, breaking equal distances away from the Charter anchor.
    ////
    private static BankerCell PreferredStanding(IReadOnlyCollection<BankerCell> cells, BankerCell anchor)
    {
        var centerX = cells.Average(cell => cell.X);
        var centerY = cells.Average(cell => cell.Y);
        var centerZ = cells.Average(cell => cell.Z);
        return cells.OrderBy(cell => Math.Pow(cell.X - centerX, 2) + Math.Pow(cell.Y - centerY, 2) +
                Math.Pow(cell.Z - centerZ, 2))
            .ThenByDescending(cell => Distance(cell, anchor)).ThenBy(cell => cell.X).ThenBy(cell => cell.Y)
            .ThenBy(cell => cell.Z).First();
    }



    //// Matches only persisted assignment identity, never the replicated interaction marker or a client-supplied ID.
    ////
    private bool FindAssignment(BankerEntity entity, out BankerHome home)
    {
        home = null!;
        if (!Guid.TryParse(entity.Attributes.GetString(BankerEntity.BranchAttribute), out var branch) ||
            !Guid.TryParse(entity.Attributes.GetString(BankerEntity.SpawnAttribute), out var spawn) ||
            !homes.TryGetValue(branch, out var saved) || saved.EntityId != entity.EntityId || saved.SpawnId != spawn) return false;
        home = saved;
        return true;
    }



    //// Supplies the session registry's inexpensive execution-time branch cutoff and assignment check.
    //// The registry separately checks connection, entity life, dimension, distance, and conversation lifetime.
    ////
    private bool Available(BankerEntity entity)
    {
        return !failed && !disposed && FindAssignment(entity, out var home) &&
            home.Disposition == BankerDisposition.Assigned && entity.Pos.Dimension == home.Anchor.Dimension &&
            (home.DepartureDay is null || api.World.Calendar.TotalDays < home.DepartureDay);
    }



    //// Preserves contradictory NPC evidence while removing all loaded service access for that home.
    //// Financial authority remains available through other Bankers; no money or item repair is attempted.
    ////
    private void Quarantine(BankerHome home)
    {
        homes[home.Branch] = home with { Disposition = BankerDisposition.Quarantined };
        if (loadedEntities.TryGetValue(home.Branch, out var entries))
            foreach (var entity in entries.Values) Unregister(entity.EntityId);
        Stage();
        log.Write("CRIT", "banker-lifecycle", "Conflicting NPC assignment evidence; home quarantined without replacement.");
    }



    //// Invalidates session authority before removing a known entity; a living detached Banker may still be unloaded.
    ////
    private void Unregister(long entityId)
    {
        registered.Remove(entityId);
        banking.UnregisterBanker(entityId);
    }



    //// Removes only this retired home's known loaded NPCs; later loads are removed by the same retained tombstone.
    ////
    private void RemoveLoaded(Guid branch)
    {
        if (!loadedEntities.TryGetValue(branch, out var entries)) return;
        foreach (var entity in entries.Values.ToArray())
        {
            Unregister(entity.EntityId);
            api.World.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
        }
    }



    //// Provides explicit administrator placement on targeted solid ground and targeted removal with a required reason.
    //// Manual homes are public banking access points, not Charter branches or protection claims.
    ////
    private TextCommandResult AdminCommand(TextCommandCallingArgs args)
    {
        try
        {
            RequireReady();
            if (args.Caller.Player is not IServerPlayer player || !player.HasPrivilege(Privilege.controlserver))
                return TextCommandResult.Error("An authenticated server administrator is required.");
            var words = ((string?)args[0] ?? "").Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 1 && words[0] == "place")
            {
                var selected = player.CurrentBlockSelection?.Position;
                if (selected is null || player.Entity.Pos.Dimension != selected.dimension ||
                    player.Entity.Pos.SquareDistanceTo(selected.ToVec3d()) > 64)
                    return TextCommandResult.Error("Look at nearby solid ground for the Banker's standing position.");
                var standing = selected.UpCopy();
                if (!SafeCell(BankerRosterStorage.Cell(standing)))
                    return TextCommandResult.Error("The selected standing position is not safely loaded, supported, and clear.");
                EstablishHome(Guid.NewGuid(), standing, [standing]);
                Tick(0);
                log.Write("INFO", "banker-lifecycle", "Administrator established a manual Banker home.");
                return TextCommandResult.Success("Banker home established; staffing waits for safely loaded, unoccupied space.");
            }
            if (words.Length == 2 && words[0] == "remove" && !string.IsNullOrWhiteSpace(words[1]) &&
                player.CurrentEntitySelection?.Entity is BankerEntity entity &&
                player.Entity.Pos.SquareDistanceTo(entity.Pos) <= 64 && FindAssignment(entity, out var home))
            {
                if (managedBranchProbe?.Invoke(home.Branch) == true)
                    return TextCommandResult.Error("Use /bankbranch decommission reason for a Charter Banker.");
                RemoveHome(home.Branch, words[1]);
                return TextCommandResult.Success("Banker removed without replacement. Accounts are unchanged.");
            }
            return TextCommandResult.Error("Use /bankernpc place, or look at a Banker and use /bankernpc remove reason.");
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return TextCommandResult.Error("Banker home could not be changed. Check NPC availability and standing-region overlap.");
        }
    }



    //// Checkpoints pending ordinary-day waits immediately before staging the engine's next world-save snapshot.
    ////
    private void Save()
    {
        if (!loaded || failed || disposed) return;
        try
        {
            foreach (var home in homes.Values.ToArray()) AdvanceTimer(home);
            Stage();
        }
        catch (Exception error) { Disable(error); }
    }



    //// Stages the current roster; callers retain failure evidence and stop admission if persistence throws.
    ////
    private void Stage()
    {
        try { storage!.Stage(homes.Values); }
        catch (Exception error)
        {
            Disable(error);
            throw;
        }
    }



    //// Requires initialized, healthy NPC authority on the owning server thread for trusted content mutations.
    ////
    private void RequireReady()
    {
        if (!loaded || failed || disposed || Environment.CurrentManagedThreadId != serverThread)
            throw new InvalidOperationException("Banker management is unavailable on this thread.");
    }



    //// Disables only NPC management after an unexpected lifecycle failure, preserving existing roster/entity evidence.
    ////
    private void Disable(Exception error)
    {
        if (failed) return;
        failed = true;
        foreach (var entityId in registered.ToArray()) Unregister(entityId);
        log.Write("CRIT", "banker-lifecycle", "NPC management disabled after " + error.GetType().Name + ".");
    }



    //// Stages final timer progress before removing event handlers; the mod disposes NPCs before the financial adapter.
    //// Loaded entities are not killed or decommissioned merely because the server is shutting down.
    ////
    public void Dispose()
    {
        if (disposed) return;
        Save();
        foreach (var entityId in registered.ToArray()) Unregister(entityId);
        disposed = true;
        api.Event.OnEntitySpawn -= Track;
        api.Event.OnEntityLoaded -= Track;
        api.Event.OnEntityDeath -= Died;
        api.Event.OnEntityDespawn -= Despawned;
        api.Event.GameWorldSave -= Save;
        api.Event.UnregisterGameTickListener(tickListener);
    }



}
