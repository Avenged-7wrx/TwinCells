using dc.en;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using System.Runtime.CompilerServices;

public sealed partial class CoopSyncFixes
{
    private sealed class SentSleepState {public int Id,Generation;public bool Sleeping;}
    private static readonly ConditionalWeakTable<Mob,SentSleepState> SentSleepStates=new();
    private static void InitializeSleepingMotion(Harmony harmony)
    {
        if(Disabled("SLEEPMOTION"))return;
        Patch(harmony,Mobs,"TryBuildHostMobDeltaSnapshot",nameof(SendSleepBoundary),nameof(StopSleepingVelocity));
    }
    private static void SendSleepBoundary(Mob mob,int mobSyncId,ref bool forceFullState)
    {
        if(ModEntry._net?.IsHost!=true || mob==null || mob.destroyed || !SentSleepStates.TryGetValue(mob,out var prior))return;
        if(prior.Id==mobSyncId && Current(prior.Generation) && prior.Sleeping!=CsfMobCalls.MobCulledLocally(mob))forceFullState=true;
    }
    private static void StopSleepingVelocity(Mob mob,bool __result,bool sendStateSnapshot,ref NetNode.MobStateSnapshot stateSnapshot,ref NetNode.MobMoveSnapshot moveSnapshot)
    {
        if(!__result || ModEntry._net?.IsHost!=true || mob==null || mob.destroyed)return;
        var sleeping=CsfMobCalls.MobCulledLocally(mob);
        var sent=SentSleepStates.GetValue(mob,_=>new SentSleepState());
        sent.Id=sendStateSnapshot?stateSnapshot.Index:moveSnapshot.Index;sent.Generation=sendStateSnapshot?stateSnapshot.Generation:moveSnapshot.Generation;sent.Sleeping=sleeping;
        if(!sleeping)return;
        // Native culling retains dx/bdx even while position is frozen. Sending that
        // latent velocity makes an awake remote replica walk away from the host;
        // its bounded correction cannot overcome a Rat's 7.2px native step.
        // Keep native fields intact: waking the host must resume its own motion.
        if(sendStateSnapshot)
        {
            var s=stateSnapshot;
            stateSnapshot=new NetNode.MobStateSnapshot(s.Index,s.X,s.Y,s.Dir,s.Life,s.MaxLife,s.AnimPayload,s.Type,s.StatePayload,s.Generation,s.Time,0,0);
        }
        else
        {
            var m=moveSnapshot;
            moveSnapshot=new NetNode.MobMoveSnapshot(m.Index,m.X,m.Y,m.Dir,m.AnimPayload,m.Generation,m.Time,0,0);
        }
    }
}
