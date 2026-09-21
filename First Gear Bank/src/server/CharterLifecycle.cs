/*
 * Owns accepted Charter branches, exact-position player protection, and delayed first arrival for one server world.
 * Loaded plaque block entities supply stateless placement observations.  CharterPremisesValidator supplies one bounded
 * room snapshot; CharterRegistryStorage owns durable placement, topology, spacing, and ordinary-day timer authority.
 *
 * Protection is a deny layer after ordinary claims, not a synthetic claim.  Structural positions remain protected even
 * after environmental damage; only captured door, storage, seat, and light interactions remain usable.  Breaking the
 * Charter itself releases this layer and delegates an existing Banker's saved 17:00 departure to BankerLifecycle.
 *
 * Dirty work is spatially queued after nearby block changes, with bounded fallback rechecks.  Pending arrival progress
 * suspends when premises cease to match their immutable capture, resumes without reroll, and never force-loads terrain.
 * Player changes are covered through supported server hooks.  Unsupported third-party mutation footprints are outside
 * the public API boundary; native public multiblock and door behaviors receive explicit dependency preflight.
 */

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace FirstGearBank.Server;

/// Server-thread Charter authority coordinating plaque observations, protection, timers, and first Banker homes.
public sealed class CharterLifecycle : IDisposable
{
    private const int ChunkSize = 32;
    private readonly ICoreServerAPI api;
    private readonly FirstGearBankServer banking;
    private readonly BankerLifecycle bankers;
    private readonly ServerDiagnostics log;
    private readonly CharterPremisesValidator validator;
    private readonly Dictionary<BankerCell, BankerCharterBlockEntity> observed = new();
    private readonly List<BankerCell> observedOrder = new();
    private readonly Dictionary<(int Dimension, int X, int Z), HashSet<BankerCell>> observedByChunk = new();
    private readonly Dictionary<BankerCell, (Guid Placement, CharterPositionRole Role)> protectedCells = new();
    private readonly Dictionary<Guid, Guid> activeByBranch = new();
    private readonly List<Guid> activePlacements = new();
    private readonly List<Guid> pendingRetirements = new();
    private readonly HashSet<Guid> queuedRemovals = new();
    private readonly HashSet<Guid> loggedRetirementFailures = new();
    private readonly HashSet<Guid> loggedStaffingFailures = new();
    private readonly Queue<BankerCell> dirty = new();
    private readonly HashSet<BankerCell> dirtySet = new();
    private readonly long tickListener;
    private Dictionary<Guid, CharterBranch> branches = new();
    private CharterRegistryStorage? storage;
    private int branchCursor;
    private int retirementCursor;
    private int observationCursor;
    private int fallbackTicks;
    private bool loaded;
    private bool failed;
    private bool disposed;



    //// Registers bounded observation, mutation, save, and tick hooks before gameplay begins.
    ////
    public CharterLifecycle(ICoreServerAPI api, FirstGearBankServer banking, BankerLifecycle bankers)
    {
        this.api = api;
        this.banking = banking;
        this.bankers = bankers;
        log = new(api);
        validator = new(api);
        bankers.SetInitialStaffingGate(CanInitiallyStaff);
        bankers.SetManagedBranchProbe(IsActiveBranch);
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, Load);
        api.Event.DidPlaceBlock += Placed;
        api.Event.DidBreakBlock += Broken;
        api.Event.CanPlaceOrBreakBlock += CanPlaceOrBreak;
        api.Event.CanUseBlock += CanUse;
        api.Event.GameWorldSave += Save;
        tickListener = api.Event.RegisterGameTickListener(Tick, 5000);
        api.ChatCommands.Create("bankbranch").RequiresPrivilege(Privilege.controlserver)
            .WithDescription("Emergency decommission of the Charter branch containing the administrator")
            .WithArgs(api.ChatCommands.Parsers.All("decommission reason")).HandleWith(AdminCommand);
    }



    //// Restores valid branch authority, builds the exact protection index, and queues every loaded plaque for review.
    ////
    private void Load()
    {
        if (loaded || disposed) return;
        loaded = true;
        try
        {
            storage = new(api.WorldManager.SaveGame, log);
            var restored = storage.Load();
            if (restored is null) { failed = true; return; }
            branches = restored;
            activePlacements.AddRange(branches.Values.Where(row => row.Disposition == CharterDisposition.Active)
                .OrderBy(row => row.Placement).Select(row => row.Placement));
            foreach (var placement in activePlacements) activeByBranch.Add(branches[placement].Branch, placement);
            pendingRetirements.AddRange(branches.Values.Where(row => row.Disposition == CharterDisposition.Removed &&
                !row.RetirementApplied).OrderBy(row => row.Placement).Select(row => row.Placement));
            RebuildProtection();
            foreach (var cell in observed.Keys) MarkDirty(cell);
            Save();
            log.Write("INFO", "charter-lifecycle", "Charter registry loaded; room validation and protection are active.");
        }
        catch (Exception error) { Disable(error); }
    }



    //// Records a loaded plaque and queues validation without treating chunk load as placement or branch creation.
    ////
    internal void Observe(BankerCharterBlockEntity charter)
    {
        if (disposed) return;
        var cell = BankerRosterStorage.Cell(charter.Pos);
        if (!observed.ContainsKey(cell)) observedOrder.Add(cell);
        observed[cell] = charter;
        var key = ChunkKey(cell);
        if (!observedByChunk.TryGetValue(key, out var bucket)) observedByChunk[key] = bucket = new();
        bucket.Add(cell);
        MarkDirty(cell);
    }



    //// Removes only the loaded observation; durable branch authority survives ordinary chunk unload.
    ////
    internal void Unloaded(BlockPos position, Guid placement)
    {
        var cell = BankerRosterStorage.Cell(position);
        observed.Remove(cell);
        observedOrder.Remove(cell);
        if (observationCursor >= observedOrder.Count) observationCursor = 0;
        if (observedByChunk.TryGetValue(ChunkKey(cell), out var bucket)) bucket.Remove(cell);
    }



    //// Decommissions the matching accepted placement after actual Charter removal and leaves all finance unchanged.
    ////
    internal void Removed(BlockPos position, Guid placement)
    {
        Unloaded(position, placement);
        if (!loaded || failed || disposed) return;
        var anchor = BankerRosterStorage.Cell(position);
        CharterBranch? branch = null;
        if (placement != Guid.Empty && branches.TryGetValue(placement, out var identified) &&
            identified.Disposition == CharterDisposition.Active && identified.Anchor == anchor) branch = identified;
        branch ??= activePlacements.Select(candidate => branches[candidate])
            .SingleOrDefault(candidate => candidate.Anchor == anchor);
        if (branch is null) return;
        try { Retire(branch, false, "Charter removed"); }
        catch (Exception error) { Disable(error); }
    }



    //// Queues deliberate removal after the current interaction dispatch so destroying the selected block cannot
    //// recursively re-enter that dispatch.  Placement identity and claim access are rechecked in the queued task.
    ////
    internal bool RemoveByPlayer(BankerCharterBlockEntity charter, IServerPlayer player)
    {
        if (!CanRemove(charter, player) || !charter.TryPlacement(out var placement, out _) ||
            !queuedRemovals.Add(placement)) return false;
        var position = charter.Pos.Copy();
        api.Event.EnqueueMainThreadTask(() => CompleteQueuedRemoval(position, placement, player),
            "firstgearbank-remove-charter");
        return true;
    }



    //// Completes one queued removal only if the same Charter and caller authority still exist a frame later.
    ////
    private void CompleteQueuedRemoval(BlockPos position, Guid placement, IServerPlayer player)
    {
        queuedRemovals.Remove(placement);
        if (disposed || api.World.BlockAccessor.GetBlockEntity(position) is not BankerCharterBlockEntity charter ||
            !charter.TryPlacement(out var currentPlacement, out _) || currentPlacement != placement ||
            !CanRemove(charter, player)) return;
        api.World.BlockAccessor.BreakBlock(position, player);
    }



    //// Marks nearby loaded plaques dirty after a successful placement without scanning unrelated world candidates.
    ////
    private void Placed(IServerPlayer player, int oldBlockId, BlockSelection selection, ItemStack stack)
    {
        MarkNearby(selection.Position);
    }



    //// Marks nearby loaded plaques dirty after a successful break, including environmental callbacks with no player.
    ////
    private void Broken(IServerPlayer player, int oldBlockId, BlockSelection selection)
    {
        MarkNearby(selection.Position);
    }



    //// Denies placement or breaking at captured positions, except removal of that branch's actual Charter plaque.
    //// Prospective native multiblocks are preflighted as a unit before their behavior writes dependent positions.
    ////
    private bool CanPlaceOrBreak(IServerPlayer player, BlockSelection selection, out string claimant)
    {
        claimant = "First Gear Bank";
        if (!loaded || disposed) return true;
        if (ProtectionAt(selection.Position) is { } protection)
        {
            var current = api.World.BlockAccessor.GetBlock(selection.Position);
            if (!failed && protection.Role.HasFlag(CharterPositionRole.Charter) &&
                current is BankerCharterBlock &&
                api.World.BlockAccessor.GetBlockEntity(selection.Position) is BankerCharterBlockEntity charter)
                return CanRemove(charter, player);
            return false;
        }
        return !ProspectiveMultiblockTouchesProtection(player, selection);
    }



    //// Allows only the captured nonstructural operations and rejects chisel, wrench, or crowbar mutation attempts.
    ////
    private bool CanUse(IServerPlayer player, BlockSelection selection)
    {
        if (!loaded || disposed || ProtectionAt(selection.Position) is not { } protection) return true;
        if (protection.Role.HasFlag(CharterPositionRole.Charter) &&
            api.World.BlockAccessor.GetBlockEntity(selection.Position) is BankerCharterBlockEntity charter)
            return player.Entity.Controls.ShiftKey && CanRemove(charter, player);
        var slot = player.InventoryManager.ActiveHotbarSlot;
        var tool = slot?.Itemstack?.Collectible?.GetTool(slot);
        if (tool is EnumTool.Chisel or EnumTool.Wrench or EnumTool.Crowbar) return false;
        return (protection.Role & (CharterPositionRole.Door | CharterPositionRole.Storage |
            CharterPositionRole.Seat | CharterPositionRole.Light)) != 0;
    }



    //// Authorizes only the recorded placer or a controlserver administrator with current build access at the plaque.
    ////
    private bool CanRemove(BankerCharterBlockEntity charter, IServerPlayer player)
    {
        if (!loaded || failed || disposed || !charter.TryPlacement(out _, out var placer) ||
            placer != player.PlayerUID && !player.HasPrivilege(Privilege.controlserver)) return false;
        return api.World.Claims.TestAccess(player, charter.Pos, EnumBlockAccessFlags.BuildOrBreak) ==
            EnumWorldAccessResponse.Granted;
    }



    //// Resolves a multiblock proxy to its controller before consulting immutable protected roles.
    ////
    private (Guid Placement, CharterPositionRole Role)? ProtectionAt(BlockPos position)
    {
        var cell = BankerRosterStorage.Cell(position);
        if (protectedCells.TryGetValue(cell, out var direct)) return direct;
        if (api.World.BlockAccessor.GetBlock(position) is not BlockMultiblock proxy) return null;
        protectedCells.TryGetValue(BankerRosterStorage.Cell(proxy.GetControlBlockPos(position)), out var controller);
        return controller == default ? null : controller;
    }



    //// Checks public generic-multiblock and door footprints for a complete, plausible placement request.
    //// Position-only claim probes and breaking nonreplaceable blocks cannot supply safe prospective geometry.
    ////
    private bool ProspectiveMultiblockTouchesProtection(IServerPlayer player, BlockSelection selection)
    {
        var candidate = player.InventoryManager.ActiveHotbarSlot?.Itemstack?.Block;
        if (candidate is null || selection.HitPosition is null || selection.Face is null ||
            !api.World.BlockAccessor.GetBlock(selection.Position).IsReplacableBy(candidate)) return false;
        var touched = false;
        if (candidate.GetBehavior<BlockBehaviorMultiblock>() is { } multiblock)
            multiblock.IterateOverEach(selection.Position, part => !(touched |= ProtectionAt(part) is not null));
        if (!touched && candidate.GetBehavior<BlockBehaviorDoor>() is { } door)
        {
            // Mirror the native paired-door origin and handle calculation before evaluating its complete footprint.
            var origin = selection.Position.Copy();
            var rotation = BEBehaviorDoor.getRotateYRad(player, selection);
            var facing = BlockFacing.HorizontalFromYaw(rotation);
            var inverted = BlockBehaviorDoor.HasCombinableLeftDoor(api.World, rotation, selection.Position,
                door.width, out var paired, out var offset);
            if (inverted && door.width > 1 && offset != 0) origin.Add(facing.GetCCW(), offset);
            if (!inverted && BlockBehaviorDoor.HasCombinableRightDoor(api.World, rotation, selection.Position,
                    door.width, out paired, out offset))
            {
                if (door.width > 1 && offset != 0) origin.Add(facing.GetCW(), offset);
            }
            if (paired is not null && ProtectionAt(paired.Pos) is not null) touched = true;
            door.IterateOverEach(origin, rotation, inverted,
                part => !(touched |= ProtectionAt(part) is not null));
        }
        return touched;
    }



    //// Processes bounded dirty observations and one saved branch, so cost does not scale per simulation tick.
    ////
    private void Tick(float elapsedSeconds)
    {
        if (!loaded && api.Server.CurrentRunPhase == EnumServerRunPhase.RunGame) Load();
        if (!loaded || failed || disposed) return;
        try
        {
            for (var count = 0; count < 4 && dirty.TryDequeue(out var cell); count++)
            {
                dirtySet.Remove(cell);
                if (observed.TryGetValue(cell, out var charter)) Evaluate(charter);
            }
            ProcessOneBranch();
            ReplayOneRetirement();
            if (++fallbackTicks >= 6)
            {
                fallbackTicks = 0;
                QueueOneFallbackObservation();
            }
        }
        catch (Exception error) { Disable(error); }
    }



    //// Validates a new placement or refreshes one active pending branch without changing an immutable snapshot.
    ////
    private void Evaluate(BankerCharterBlockEntity charter)
    {
        if (!charter.TryPlacement(out var placement, out var placerUid))
        {
            // The registry is authoritative if a crash saved branch acceptance before the block entity metadata.
            var recovered = branches.Values.SingleOrDefault(row => row.Disposition == CharterDisposition.Active &&
                row.Anchor == BankerRosterStorage.Cell(charter.Pos));
            if (recovered is null)
            {
                charter.SetStatus("charter-status-checking");
                return;
            }
            charter.RestorePlacement(recovered.Placement, recovered.Placer);
            placement = recovered.Placement;
            placerUid = recovered.Placer;
        }
        if (branches.TryGetValue(placement, out var existing))
        {
            if (existing.Disposition == CharterDisposition.Removed ||
                existing.Anchor != BankerRosterStorage.Cell(charter.Pos))
            {
                charter.SetStatus(existing.Disposition == CharterDisposition.Removed ?
                    "charter-status-decommissioned" : "charter-status-conflict");
                return;
            }
            if (InitialStaffingComplete(existing)) charter.SetStatus(bankers.IsHomeStaffed(existing.Branch) ?
                "charter-status-staffed" : "charter-status-vacant");
            else RefreshPending(existing, charter);
            return;
        }
        var placer = api.World.PlayerByUid(placerUid);
        if (placer is null)
        {
            charter.SetStatus("charter-status-checking");
            return;
        }
        if (!validator.TryCapture(charter.Pos, placer, out var capture, out var status))
        {
            charter.SetStatus(status);
            return;
        }
        if (Conflicts(charter.Pos, capture))
        {
            charter.SetStatus("charter-status-conflict");
            return;
        }
        Activate(charter, placement, placerUid, capture);
    }



    //// Atomically allocates branch identity, samples one arrival delay, stages it, then publishes protection.
    ////
    private void Activate(BankerCharterBlockEntity charter, Guid placement, string placer, CharterCapture capture)
    {
        var branchId = Guid.NewGuid();
        var range = banking.CharterArrivalDelayRange;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("firstgearbank/charter-arrival/v1/" +
            api.WorldManager.SaveGame.Seed.ToString(CultureInfo.InvariantCulture) + "/" + placement.ToString("N")));
        var uniform = (BinaryPrimitives.ReadUInt64BigEndian(bytes) >> 11) / 9007199254740992.0;
        var sampled = range.Minimum + (range.Maximum - range.Minimum) * uniform;
        var branch = new CharterBranch(placement, branchId, BankerRosterStorage.Cell(charter.Pos), placer,
            capture.Protected, capture.Interior, sampled, sampled, api.World.Calendar.TotalDays, true, false,
            CharterDisposition.Active);
        branches.Add(placement, branch);
        activePlacements.Add(placement);
        activeByBranch.Add(branchId, placement);
        try { Stage(); }
        catch
        {
            activeByBranch.Remove(branchId);
            activePlacements.Remove(placement);
            branches.Remove(placement);
            throw;
        }
        AddProtection(branch);
        charter.SetStatus("charter-status-waiting");
        log.Write("INFO", "charter-lifecycle", "Validated Charter branch activated with a saved arrival wait.");
    }



    //// Revalidates captured pending premises and suspends/resumes their same saved timer without rerolling it.
    ////
    private void RefreshPending(CharterBranch branch, BankerCharterBlockEntity charter)
    {
        var valid = validator.TryCapture(charter.Pos, null, out var capture, out var status);
        var matches = valid &&
            capture.Protected.SequenceEqual(branch.Protected) && capture.Interior.SequenceEqual(branch.Interior);
        var now = api.World.Calendar.TotalDays;
        if (!matches)
        {
            branches[branch.Placement] = branch with { PremisesValid = false, LastCalendarDay = now };
            if (branch.PremisesValid) Stage();
            charter.SetStatus(valid ? "charter-status-topology" : status);
            return;
        }
        if (!branch.PremisesValid)
        {
            branches[branch.Placement] = branch with { PremisesValid = true, LastCalendarDay = now };
            Stage();
        }
        charter.SetStatus("charter-status-waiting");
    }



    //// Revalidates the immutable premises at the NPC roster's last safe point before an initial spawn operation.
    //// Manual homes have no active Charter row and remain allowed after Charter authority has loaded successfully.
    ////
    private bool CanInitiallyStaff(Guid branchId)
    {
        if (!loaded || failed || disposed) return false;
        try
        {
            if (!activeByBranch.TryGetValue(branchId, out var branchPlacement)) return true;
            var branch = branches[branchPlacement];
            if (!branch.HomeEstablished || !branch.PremisesValid) return false;
            var anchor = BankerRosterStorage.Position(branch.Anchor);
            if (!api.World.IsFullyLoadedChunk(anchor) ||
                api.World.BlockAccessor.GetBlock(anchor) is not BankerCharterBlock ||
                api.World.BlockAccessor.GetBlockEntity(anchor) is not BankerCharterBlockEntity charter ||
                !charter.TryPlacement(out var placement, out _) || placement != branch.Placement) return false;
            RefreshPending(branch, charter);
            return branches[branch.Placement].PremisesValid;
        }
        catch (Exception error)
        {
            Disable(error);
            return false;
        }
    }



    //// Distinguishes a completed first assignment from a home that has only been handed to the NPC roster.
    //// Durable NPC evidence keeps later death-replacement vacancies outside the premises-suspension state machine.
    ////
    private bool InitialStaffingComplete(CharterBranch branch)
    {
        return branch.HomeEstablished && bankers.HasEverBeenStaffed(branch.Branch);
    }



    //// Reports whether an NPC home belongs to a currently reserving Charter for administrator-command routing.
    ////
    private bool IsActiveBranch(Guid branchId)
    {
        return loaded && activeByBranch.ContainsKey(branchId);
    }



    //// Advances one pending branch by forward ordinary days, audits Charter presence, and establishes one due home.
    ////
    private void ProcessOneBranch()
    {
        if (activePlacements.Count == 0) return;
        if (branchCursor >= activePlacements.Count) branchCursor = 0;
        var branch = branches[activePlacements[branchCursor++]];
        var anchor = BankerRosterStorage.Position(branch.Anchor);
        if (!api.World.IsFullyLoadedChunk(anchor)) return;
        if (api.World.BlockAccessor.GetBlock(anchor) is not BankerCharterBlock ||
            api.World.BlockAccessor.GetBlockEntity(anchor) is not BankerCharterBlockEntity charter ||
            !charter.TryPlacement(out var placement, out _) || placement != branch.Placement)
        {
            Retire(branch, false, "Charter missing from its loaded anchor");
            return;
        }
        if (InitialStaffingComplete(branch))
        {
            charter.SetStatus(bankers.IsHomeStaffed(branch.Branch) ?
                "charter-status-staffed" : "charter-status-vacant");
            return;
        }
        // Revalidate immediately before charging time or handing a due arrival to the separate NPC roster.
        RefreshPending(branch, charter);
        branch = branches[branch.Placement];
        if (!branch.PremisesValid) return;
        if (branch.HomeEstablished) return;
        var now = api.World.Calendar.TotalDays;
        branch = branch with
        {
            RemainingDays = Math.Max(0, branch.RemainingDays - Math.Max(0, now - branch.LastCalendarDay)),
            LastCalendarDay = now
        };
        branches[branch.Placement] = branch;
        if (branch.RemainingDays > 0) return;
        try
        {
            bankers.EstablishHome(branch.Branch, anchor,
                branch.Interior.Select(BankerRosterStorage.Position).ToArray());
        }
        catch (Exception error)
        {
            if (loggedStaffingFailures.Add(branch.Placement))
                log.Write("WARN", "charter-lifecycle", "Due first arrival awaits NPC roster availability after " +
                    error.GetType().Name + ".");
            return;
        }
        loggedStaffingFailures.Remove(branch.Placement);
        branches[branch.Placement] = branch with { HomeEstablished = true };
        Stage();
        charter.SetStatus(bankers.IsHomeStaffed(branch.Branch) ?
            "charter-status-staffed" : "charter-status-waiting");
    }



    //// Releases topology and spacing before cancelling pending arrival or starting the existing Banker's departure.
    ////
    private void Retire(CharterBranch branch, bool immediate, string reason)
    {
        var now = api.World.Calendar.TotalDays;
        var departure = immediate ? now : Math.Max(now, Math.Floor(now) + 17.0 / 24.0);
        branches[branch.Placement] = branch with
        {
            Disposition = CharterDisposition.Removed,
            PremisesValid = false,
            ImmediateRetirement = immediate,
            DepartureDay = departure
        };
        activePlacements.Remove(branch.Placement);
        activeByBranch.Remove(branch.Branch);
        loggedStaffingFailures.Remove(branch.Placement);
        pendingRetirements.Add(branch.Placement);
        if (branchCursor >= activePlacements.Count) branchCursor = 0;
        Stage();
        RebuildProtection();
        ApplyRetirement(branches[branch.Placement], reason);
        if (observed.TryGetValue(branch.Anchor, out var charter))
            charter.SetStatus("charter-status-decommissioned");
        log.Write("INFO", "charter-lifecycle", "Charter branch decommissioned; financial authority is unchanged. " +
            reason);
    }



    //// Resolves exactly one active Charter branch around the administrator and retires it without changing blocks.
    ////
    private TextCommandResult AdminCommand(TextCommandCallingArgs args)
    {
        if (!loaded || failed || disposed || args.Caller.Player is not IServerPlayer player ||
            !player.HasPrivilege(Privilege.controlserver))
            return TextCommandResult.Error("Charter management is unavailable to this caller.");
        var words = ((string?)args[0] ?? string.Empty).Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != 2 || words[0] != "decommission" || string.IsNullOrWhiteSpace(words[1]) ||
            words[1].Length > 512)
            return TextCommandResult.Error("Use /bankbranch decommission reason while standing inside one branch.");
        var position = BankerRosterStorage.Cell(player.Entity.Pos.AsBlockPos);
        var matches = branches.Values.Where(branch => branch.Disposition == CharterDisposition.Active &&
            branch.Protected.Any(entry => entry.Cell == position)).ToArray();
        if (matches.Length != 1)
            return TextCommandResult.Error(matches.Length == 0 ? "No active branch contains this position." :
                "The branch at this position is ambiguous; nothing was changed.");
        try
        {
            var branch = matches[0];
            var reason = SafeAuditText(words[1]);
            if (string.IsNullOrWhiteSpace(reason))
                return TextCommandResult.Error("A visible decommission reason is required; nothing was changed.");
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(player.PlayerUID)))[..16];
            Retire(branch, true, "Administrative decommission: " + reason);
            log.Write("WARN", "charter-administration", "Decommissioned Charter branch; admin=" +
                SafeAuditText(player.PlayerName) + ", identity=" + identity + ", branch=" +
                branch.Branch.ToString("N") + ", type=Charter, dimension=" + position.Dimension +
                ", position=" + position.X + "/" +
                position.Y + "/" + position.Z + ", day=" + api.World.Calendar.TotalDays.ToString("R",
                    CultureInfo.InvariantCulture) + ", reason=" + reason);
            return TextCommandResult.Success("Charter branch decommissioned; its blocks and all accounts are unchanged.");
        }
        catch (Exception error)
        {
            Disable(error);
            return TextCommandResult.Error("The branch could not be decommissioned; Charter management is disabled.");
        }
    }



    //// Removes control characters and bounds administrator-authored audit fields before writing one log record.
    ////
    private static string SafeAuditText(string value)
    {
        return new string(value.Where(character => !char.IsControl(character)).Take(512).ToArray());
    }



    //// Replays one durable retirement into the independent NPC roster, closing cross-save crash windows idempotently.
    ////
    private void ReplayOneRetirement()
    {
        if (pendingRetirements.Count == 0) return;
        if (retirementCursor >= pendingRetirements.Count) retirementCursor = 0;
        var placement = pendingRetirements[retirementCursor++];
        ApplyRetirement(branches[placement], "Replayed durable Charter decommission");
    }



    //// Applies one saved branch retirement to the independent NPC roster and then seals its replay marker.
    //// Roster unavailability leaves the operation pending and emits only one warning until a later successful retry.
    ////
    private void ApplyRetirement(CharterBranch branch, string reason)
    {
        try
        {
            if (branch.ImmediateRetirement) bankers.RemoveHome(branch.Branch, SafeAuditText(reason));
            else bankers.DetachHome(branch.Branch, branch.DepartureDay!.Value);
        }
        catch (Exception error)
        {
            if (loggedRetirementFailures.Add(branch.Placement))
                log.Write("WARN", "charter-lifecycle", "Saved NPC retirement awaits roster recovery after " +
                    error.GetType().Name + ".");
            return;
        }
        branches[branch.Placement] = branch with { RetirementApplied = true };
        pendingRetirements.Remove(branch.Placement);
        loggedRetirementFailures.Remove(branch.Placement);
        if (retirementCursor > 0) retirementCursor--;
        Stage();
    }



    //// Rejects horizontal spacing inside the configured radius and any exact overlap with another active capture.
    ////
    private bool Conflicts(BlockPos anchor, CharterCapture capture)
    {
        var cells = capture.Protected.Select(entry => entry.Cell).ToHashSet();
        var radius = banking.MinimumBranchSpacing;
        foreach (var placement in activePlacements)
        {
            var branch = branches[placement];
            if (branch.Protected.Any(entry => cells.Contains(entry.Cell))) return true;
            if (branch.Anchor.Dimension != anchor.dimension || radius == 0) continue;
            var dx = (long)branch.Anchor.X - anchor.X;
            var dz = (long)branch.Anchor.Z - anchor.Z;
            if ((decimal)dx * dx + (decimal)dz * dz < (decimal)radius * radius) return true;
        }
        return false;
    }



    //// Adds one active branch to the exact protection index and rejects contradictory persisted overlap.
    ////
    private void AddProtection(CharterBranch branch)
    {
        if (branch.Disposition != CharterDisposition.Active) return;
        foreach (var entry in branch.Protected)
            if (!protectedCells.TryAdd(entry.Cell, (branch.Placement, entry.Role)))
                throw new InvalidOperationException("Active Charter protection snapshots overlap.");
    }



    //// Reconstructs protection from validated active rows after load or decommissioning.
    ////
    private void RebuildProtection()
    {
        protectedCells.Clear();
        foreach (var placement in activePlacements.Order()) AddProtection(branches[placement]);
    }



    //// Adds a candidate once to the bounded validation queue.
    ////
    private void MarkDirty(BankerCell cell)
    {
        if (dirtySet.Add(cell)) dirty.Enqueue(cell);
    }



    //// Uses chunk buckets to find only loaded Charters within the native room scan radius of a changed block.
    ////
    private void MarkNearby(BlockPos position)
    {
        for (var chunkX = FloorChunk(position.X) - 1; chunkX <= FloorChunk(position.X) + 1; chunkX++)
            for (var chunkZ = FloorChunk(position.Z) - 1; chunkZ <= FloorChunk(position.Z) + 1; chunkZ++)
                if (observedByChunk.TryGetValue((position.dimension, chunkX, chunkZ), out var bucket))
                    foreach (var cell in bucket)
                        if (Math.Abs((long)cell.X - position.X) <= 15 && Math.Abs((long)cell.Y - position.Y) <= 15 &&
                            Math.Abs((long)cell.Z - position.Z) <= 15) MarkDirty(cell);
    }



    //// Periodically queues one loaded observation as a bounded fallback for mutations lacking a block event.
    ////
    private void QueueOneFallbackObservation()
    {
        if (observedOrder.Count == 0) return;
        if (observationCursor >= observedOrder.Count) observationCursor = 0;
        MarkDirty(observedOrder[observationCursor++]);
    }



    //// Converts negative and positive world coordinates into stable 32-block observation buckets.
    ////
    private static int FloorChunk(int coordinate)
    {
        return (int)Math.Floor(coordinate / (double)ChunkSize);
    }



    //// Builds the observation bucket key without relying on mutable engine positions.
    ////
    private static (int Dimension, int X, int Z) ChunkKey(BankerCell cell)
    {
        return (cell.Dimension, FloorChunk(cell.X), FloorChunk(cell.Z));
    }



    //// Revalidates loaded first-arrival premises, advances eligible timers, and stages one complete registry snapshot.
    ////
    private void Save()
    {
        if (!loaded || failed || disposed) return;
        try
        {
            foreach (var saved in branches.Values.ToArray())
            {
                var branch = saved;
                if (branch.Disposition == CharterDisposition.Active && !InitialStaffingComplete(branch))
                {
                    var anchor = BankerRosterStorage.Position(branch.Anchor);
                    if (api.World.IsFullyLoadedChunk(anchor) &&
                        api.World.BlockAccessor.GetBlock(anchor) is BankerCharterBlock &&
                        api.World.BlockAccessor.GetBlockEntity(anchor) is BankerCharterBlockEntity charter &&
                        charter.TryPlacement(out var placement, out _) && placement == branch.Placement)
                    {
                        RefreshPending(branch, charter);
                        branch = branches[branch.Placement];
                    }
                }
                if (branch.Disposition == CharterDisposition.Active && !branch.HomeEstablished && branch.PremisesValid)
                {
                    var now = api.World.Calendar.TotalDays;
                    branches[branch.Placement] = branch with
                    {
                        RemainingDays = Math.Max(0, branch.RemainingDays - Math.Max(0, now - branch.LastCalendarDay)),
                        LastCalendarDay = now
                    };
                }
            }
            Stage();
        }
        catch (Exception error) { Disable(error); }
    }



    //// Stages current Charter authority and propagates failure so its initiating transition cannot appear successful.
    ////
    private void Stage()
    {
        storage!.Stage(branches.Values);
    }



    //// Disables further Charter transitions after unexpected failure while retaining known protection in memory.
    ////
    private void Disable(Exception error)
    {
        if (failed) return;
        failed = true;
        log.Write("CRIT", "charter-lifecycle", "Charter management disabled after " + error.GetType().Name + ".");
    }



    //// Stages timer progress and releases callbacks without decommissioning branches during server shutdown.
    ////
    public void Dispose()
    {
        if (disposed) return;
        Save();
        disposed = true;
        bankers.SetInitialStaffingGate(null);
        bankers.SetManagedBranchProbe(null);
        api.Event.DidPlaceBlock -= Placed;
        api.Event.DidBreakBlock -= Broken;
        api.Event.CanPlaceOrBreakBlock -= CanPlaceOrBreak;
        api.Event.CanUseBlock -= CanUse;
        api.Event.GameWorldSave -= Save;
        api.Event.UnregisterGameTickListener(tickListener);
    }



}
