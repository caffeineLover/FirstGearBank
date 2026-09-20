/*
 * Integrates the Banker's logical home with Vintage Story's native server AI scheduler and waypoint navigator.
 * BankerLifecycle supplies a deterministic safe standing cell within the caller-provided interior, or bounded nearby
 * space when renovation blocks a detached home.  Return movement outranks ordinary idle but yields to damage fleeing.
 * There is no arbitrary wandering, bed adoption, teleport, block editing, or terrain force-load.
 *
 * Navigation targets use the engine's dimension-encoded InternalY.  Failed paths stop and retry after a short cooldown;
 * they never authorize a replacement entity or modify branch state.  The task borrows the scheduler's traverser and
 * animation manager and releases active movement when interrupted, on arrival, or when its home becomes unavailable.
 */

using First_Gear_Bank;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace FirstGearBank.Server;

/// Native AI task returning the Banker to a roster-selected home without coupling navigation to financial logic.
public sealed class BankerReturnHomeTask : AiTaskBase
{
    private Vec3d? destination;
    private bool finished;
    private long retryAt;
    private int alternative;



    //// Borrows the entity scheduler's shared path traverser and animation settings from the native task configuration.
    ////
    public BankerReturnHomeTask(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
    }



    //// Selects return movement only when displaced from a currently safe home; stable idle ticks avoid path searches.
    ////
    public override bool ShouldExecute()
    {
        if (!entity.Alive || world.ElapsedMilliseconds < retryAt || entity is not BankerEntity banker) return false;
        retryAt = world.ElapsedMilliseconds + 1000;
        var lifecycle = world.Api.ModLoader.GetModSystem<First_Gear_BankModSystem>().Bankers;
        if (lifecycle?.TryHomeTarget(banker, out var target, alternative) != true) return false;
        destination = target;
        return entity.Pos.SquareDistanceTo(target.X, target.Y, target.Z) > 0.16;
    }



    //// Starts bounded native pathfinding toward home, retaining normal collision and no-path failure handling.
    ////
    public override void StartExecute()
    {
        base.StartExecute();
        finished = false;
        if (destination is null || !pathTraverser.NavigateTo(destination, 0.015f, 0.2f, Reached, Blocked,
            Blocked, giveUpWhenNoPath: true, searchDepth: 1000)) finished = true;
    }



    //// Continues only while the native path remains active; higher-priority reactions may cancel this task normally.
    ////
    public override bool ContinueExecute(float elapsedSeconds)
    {
        return !finished && entity.Alive && pathTraverser.Active && base.ContinueExecute(elapsedSeconds);
    }



    //// Releases movement and animation ownership on arrival, obstruction, or interruption before another task starts.
    ////
    public override void FinishExecute(bool cancelled)
    {
        pathTraverser.Stop();
        retryAt = world.ElapsedMilliseconds + 2000;
        base.FinishExecute(cancelled);
    }



    //// Records a reached workstation and resets alternative search without claiming space outside the old interior.
    ////
    private void Reached()
    {
        finished = true;
        alternative = 0;
        if (entity is BankerEntity banker)
            world.Api.ModLoader.GetModSystem<First_Gear_BankModSystem>().Bankers?.ReachedHome(banker);
    }



    //// Advances through deterministic safe candidates after obstruction, without teleporting or spawning another NPC.
    ////
    private void Blocked()
    {
        finished = true;
        alternative = (alternative + 1) % 4096;
    }



}
