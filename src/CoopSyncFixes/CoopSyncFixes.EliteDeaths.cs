using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using dc.en;
using DeadCellsMultiplayerMod;
using HarmonyLib;

public sealed partial class CoopSyncFixes
{
    // Integrated after the root lifecycle fence. Managed patchpoints only.
    // The parent package owns GetAuthoritativeDoorState: native _closed is collision truth.
    static readonly MethodInfo EliteIsBoss=Mobs.Assembly.GetType("DeadCellsMultiplayerMod.Mobs.Bosses.BossSyncHelpers",true).GetMethod("IsBossMob",Flags);
    static readonly MethodInfo EliteNativeOrigOnDie=typeof(Mob).Assembly.GetType("dc.en.Hook_Mob",true)
        .GetNestedType("orig_onDie",BindingFlags.Public|BindingFlags.NonPublic).GetMethod("Invoke");
    static readonly Action<object,Mob> EliteCallNativeOriginal=EliteBuildNativeOriginalBridge();
    static readonly FieldInfo EliteRemoteDescriptor=Coordinator.GetField("_remoteDescriptor",Flags);
    static long EliteHeldIncompleteClears,EliteHeldGhostHides,EliteWakeRefreshes,EliteIncompleteNativeCalls;
    internal static bool EliteDeathCompletionReady;
    static long EliteNativeDeathFaults,EliteBlockedFaultRetries,EliteNativeFaultRecordFailures;
    static string EliteLastNativeDeathFaultType;
    sealed class EliteNativeFaultScope
    {
        internal EliteNativeFaultScope Previous;
        internal NetNode Net;
        internal string Run;
        internal int Generation,Build,Id;
        internal IntPtr Level,Mob;
        internal bool Eligible,NativeBodyThrew;
    }
    [ThreadStatic] static EliteNativeFaultScope EliteActiveNativeFaultScope;
    static readonly ConditionalWeakTable<NetNode,Dictionary<IntPtr,EliteNativeFaultScope>> EliteNativeFaults=new();
    static void InitializeEliteDeathCompletion(Harmony harmony)
    {
        if(Disabled("ELITE_DEATH"))return;
        // The root lifecycle prefix owns the retained-death loop and returns false.
        // Refresh pending native state before that prefix can skip later prefixes.
        harmony.Patch(Mobs.GetMethod("FlushPendingClientAuthoritativeDeaths",Flags),
            prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(EliteRefreshPendingWake),Flags)){priority=HarmonyLib.Priority.First});
        Patch(harmony,Mobs,"ClearPendingClientAuthoritativeDeath",nameof(EliteKeepIncompletePending));
        Patch(harmony,Mobs,"HideClientDeadGhost",nameof(EliteDoNotAbandonNativeDeath));
        Patch(harmony,Mobs,"RunAuthoritativeClientDeathNow",nameof(EliteBeforeNativeDeath),nameof(EliteAfterNativeDeath));
        Patch(harmony,Mobs,"TryRunPendingCulledMobDeath",nameof(EliteRunPendingWithoutEarlyRemoval));
        Patch(harmony,Mobs,"TryDeferCulledClientMobDeath",null,nameof(EliteCacheDeferredZeroLife));
        Patch(harmony,Mobs,"ApplyIncomingHostMobStates",nameof(EliteFencePendingPositiveLife));
        Patch(harmony,Mobs,"ResetMobTrackingLocked",null,nameof(EliteResetNativeFaults));
        harmony.Patch(Mobs.GetMethod("Hook_Mob_onDie",Flags),
            prefix:new HarmonyMethod(typeof(EliteNativeFaultPatch).GetMethod(nameof(EliteNativeFaultPatch.Prefix),Flags)),
            transpiler:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(EliteObserveOriginalDeathCallsite),Flags)),
            finalizer:new HarmonyMethod(typeof(EliteNativeFaultPatch).GetMethod(nameof(EliteNativeFaultPatch.Finalizer),Flags)));
        EliteDeathCompletionReady=true;
    }
    // Harmony indexes __state by declaring patch type. The root lifecycle hook
    // on the same original owns DeathStamp, so this scope needs a distinct owner.
    private static class EliteNativeFaultPatch
    {
        internal static bool Prefix(Mob self,out EliteNativeFaultScope __state)
            =>EliteEnterNativeFaultScope(self,out __state);
        internal static Exception Finalizer(Mob self,EliteNativeFaultScope __state,Exception __exception)
            =>EliteFinishNativeFaultScope(self,__state,__exception);
    }
    static string EliteCurrentRunKey()
    {
        // Read one immutable descriptor reference; do not acquire coordinator Sync under mobs Sync.
        var descriptor=EliteRemoteDescriptor.GetValue(null);
        return descriptor==null?"":Property(descriptor,"SessionId")+":"+Property(descriptor,"RunId")+":"+Property(descriptor,"Sequence");
    }
    static EliteNativeFaultScope EliteCaptureFaultIdentity(Mob mob)
    {
        var scope=new EliteNativeFaultScope {Net=ModEntry._net,Run=EliteCurrentRunKey(),
            Generation=(int)Field("s_levelIdentityToken"),Build=(int)Field("s_levelIdentityGeneration"),
            Level=(Field("currentLevel") as dc.pr.Level)?.HashlinkPointer??IntPtr.Zero,Mob=mob.HashlinkPointer};
        var reverse=(IDictionary)Field("MobToId");scope.Id=reverse.Contains(mob)?(int)reverse[mob]:0;
        return scope;
    }
    static bool EliteFaultIdentityCurrent(EliteNativeFaultScope scope)
    {
        return EliteClient() && ReferenceEquals(scope.Net,ModEntry._net) && scope.Run==EliteCurrentRunKey() &&
            scope.Generation==(int)Field("s_levelIdentityToken") && scope.Build==(int)Field("s_levelIdentityGeneration") &&
            scope.Level!=IntPtr.Zero && scope.Level==((Field("currentLevel") as dc.pr.Level)?.HashlinkPointer??IntPtr.Zero);
    }
    static bool EliteHasNativeFault(Mob mob)
    {
        if(mob==null || !EliteClient())return false;
        lock(Gate)
        {
            if(!EliteNativeFaults.TryGetValue(ModEntry._net,out var faults) ||
                !faults.TryGetValue(mob.HashlinkPointer,out var scope))return false;
            if(!EliteFaultIdentityCurrent(scope)){faults.Remove(mob.HashlinkPointer);return false;}
            var reverse=(IDictionary)Field("MobToId");
            // A fresh native allocation reusing an address must not inherit a former mob's fault.
            return !reverse.Contains(mob) || scope.Id<=0 || (int)reverse[mob]==scope.Id;
        }
    }
    static void EliteResetNativeFaults()
    {
        // The original reset clears the native pending set and registry in the same call.
        lock(Gate)if(ModEntry._net!=null)EliteNativeFaults.Remove(ModEntry._net);
    }
    static bool EliteEnterNativeFaultScope(Mob self,out EliteNativeFaultScope __state)
    {
        __state=new EliteNativeFaultScope {Previous=EliteActiveNativeFaultScope};
        EliteActiveNativeFaultScope=__state;
        if(!EliteClient() || (int)Field("authoritativeClientMobDieDepth")<=0)return true;
        if(EliteHasNativeFault(self)){EliteBlockedFaultRetries++;return false;}
        if(!EliteScopedPending(self))return true;
        lock(Gate)
        {
            var captured=EliteCaptureFaultIdentity(self);captured.Previous=__state.Previous;
            captured.Eligible=true;__state=captured;EliteActiveNativeFaultScope=captured;
        }
        return true;
    }
    static IEnumerable<CodeInstruction> EliteObserveOriginalDeathCallsite(IEnumerable<CodeInstruction> instructions)
    {
        var invoke=EliteNativeOrigOnDie;
        var observer=typeof(CoopSyncFixes).GetMethod(nameof(EliteInvokeOriginalDeath),Flags);int replaced=0;
        foreach(var instruction in instructions)
        {
            if(instruction.Calls(invoke)){instruction.opcode=OpCodes.Call;instruction.operand=observer;replaced++;}
            yield return instruction;
        }
        if(replaced!=1)throw new InvalidOperationException("Pinned native mob death delegate callsite changed");
    }
    static void EliteInvokeOriginalDeath(object orig,Mob self)
    {
        var scope=EliteActiveNativeFaultScope;
        try {EliteCallNativeOriginal(orig,self);}
        catch {if(scope?.Eligible==true)scope.NativeBodyThrew=true;throw;}
    }
    static Action<object,Mob> EliteBuildNativeOriginalBridge()
    {
        // Avoid a compile-time reference to the net10 generated delegate. Emit exactly its
        // original cast+Invoke call; no DynamicInvoke/TargetInvocationException is introduced.
        var bridge=new DynamicMethod("EliteInvokeOriginalNativeDeath",typeof(void),new[]{typeof(object),typeof(Mob)},typeof(CoopSyncFixes),true);
        var il=bridge.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Castclass,EliteNativeOrigOnDie.DeclaringType);
        il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Callvirt,EliteNativeOrigOnDie);il.Emit(OpCodes.Ret);
        return (Action<object,Mob>)bridge.CreateDelegate(typeof(Action<object,Mob>));
    }
    static Exception EliteFinishNativeFaultScope(Mob self,EliteNativeFaultScope __state,Exception __exception)
    {
        try
        {
            if(__exception!=null && __state?.Eligible==true && __state.NativeBodyThrew &&
                (int)Field("authoritativeClientMobDieDepth")>0 && ElitePending(self))
            {
                lock(Gate)
                {
                    // Retain identity only: neither the exception nor native proxies are stored.
                    var fault=new EliteNativeFaultScope {Net=__state.Net,Run=__state.Run,Generation=__state.Generation,
                        Build=__state.Build,Id=__state.Id,Level=__state.Level,Mob=__state.Mob};
                    EliteNativeFaults.GetValue(__state.Net,_=>new Dictionary<IntPtr,EliteNativeFaultScope>())[__state.Mob]=fault;
                    EliteNativeDeathFaults++;EliteLastNativeDeathFaultType=__exception.GetType().FullName;
                }
            }
        }
        catch {EliteNativeFaultRecordFailures++;} // Diagnostics must never replace the native failure.
        finally {EliteActiveNativeFaultScope=__state?.Previous;}
        return __exception; // Preserve the exact exception and the original wrapper's catch.
    }
    static bool EliteClient()=>ModEntry._net!=null && ModEntry._net.IsAlive && !ModEntry._net.IsHost;
    static bool ElitePending(Mob mob)
    {
        if(!EliteClient() || mob==null)return false;
        lock(Gate)return ((HashSet<Mob>)Field("s_pendingCulledMobDeaths")).Contains(mob);
    }
    static bool EliteScopedPending(Mob mob)
    {
        if(!ElitePending(mob))return false;
        try
        {
            // Completed/disposed entities are left to the original cleanup. Incomplete entities
            // are protected only within the active native level and outside the boss authority.
            return !mob.destroyed && !(bool)EliteIsBoss.Invoke(null,new object[]{mob}) &&
                (bool)Invoke("DoesLevelMatchCurrentIdentityLocked",mob._level);
        }
        catch {return false;}
    }
    static bool EliteNativeComplete(Mob mob)
    {
        try {return mob!=null && (mob.destroyed || mob.onDieDone);}
        catch {return false;}
    }
    static bool EliteAwakeAndInitialized(Mob mob)
    {
        if((bool)Invoke("IsSyncQuiescedForTransition") || !mob.initDone)return false;
        if(!(bool)Invoke("IsMobCulledLocally",mob))return true;
        // life=0 can stop the vanilla update that maintains cached visibility. Refresh through
        // the native callback only when the engine considers the entity awake at its real XY.
        bool outOfGame=mob._isOutOfGame(),onScreen=mob._isOnScreen();
        if(outOfGame && !onScreen)return false;
        mob.isOnScreen=onScreen;mob.isOutOfGame=outOfGame;
        mob.onOutOfGameChange();mob.lastOutOfGame=outOfGame;
        EliteWakeRefreshes++;
        return true;
    }
    static bool EliteRefreshPendingWake()
    {
        if(!EliteClient())return true;
        if((bool)Invoke("IsSyncQuiescedForTransition"))return false;
        Mob[] pending;lock(Gate)pending=((HashSet<Mob>)Field("s_pendingCulledMobDeaths")).ToArray();
        foreach(var mob in pending)
        {
            if(EliteHasNativeFault(mob))continue;
            if(!EliteScopedPending(mob))continue;
            if(EliteNativeComplete(mob)){Invoke("ClearPendingClientAuthoritativeDeath",mob);continue;}
            try {EliteSetCachedZeroLife(mob);EliteAwakeAndInitialized(mob);}
            catch { /* Keep intent; the original loop is prevented from hiding/clearing it. */ }
        }
        return true; // The original boss branch and its timing remain in control.
    }
    static bool EliteKeepIncompletePending(Mob mob)
    {
        if(EliteHasNativeFault(mob)){EliteHeldIncompleteClears++;return false;}
        if(!EliteScopedPending(mob) || EliteNativeComplete(mob))return true;
        EliteHeldIncompleteClears++;return false;
    }
    static bool EliteDoNotAbandonNativeDeath(Mob mob)
    {
        if(EliteHasNativeFault(mob)){EliteHeldGhostHides++;return false;}
        if(!EliteScopedPending(mob) || EliteNativeComplete(mob))return true;
        EliteHeldGhostHides++;return false;
    }
    static bool EliteBeforeNativeDeath(Mob mob,ref bool __result,out bool __state)
    {
        if(EliteHasNativeFault(mob)){__state=true;__result=false;EliteBlockedFaultRetries++;return false;}
        __state=EliteScopedPending(mob);
        if(!__state)return true;
        if(EliteNativeComplete(mob)){__result=true;return false;}
        try
        {
            if(EliteAwakeAndInitialized(mob))return true;
        }
        catch { }
        __result=false;return false;
    }
    static void EliteAfterNativeDeath(Mob mob,bool __state,ref bool __result)
    {
        if(!__state)return;
        // The pinned wrapper returns true even when its catch merely hides the ghost.
        __result=!EliteHasNativeFault(mob) && EliteNativeComplete(mob);
        if(!__result)EliteIncompleteNativeCalls++;
    }
    static bool EliteRunPendingWithoutEarlyRemoval(Mob mob,ref bool __result)
    {
        if(EliteHasNativeFault(mob)){__result=false;EliteBlockedFaultRetries++;return false;}
        if(!EliteScopedPending(mob))return true;
        __result=false;
        try
        {
            if(!EliteNativeComplete(mob) && !EliteAwakeAndInitialized(mob))return false;
            if(!EliteNativeComplete(mob))Invoke("RunAuthoritativeClientDeathNow",mob);
            __result=!EliteHasNativeFault(mob) && EliteNativeComplete(mob);
            if(__result)Invoke("ClearPendingClientAuthoritativeDeath",mob);
        }
        catch { /* Do not remove intent before a failed native callback. */ }
        return false;
    }
    static void EliteCacheDeferredZeroLife(Mob mob,bool __result)
    {
        if(__result && EliteScopedPending(mob))EliteSetCachedZeroLife(mob);
    }
    static void EliteSetCachedZeroLife(Mob mob)
    {
        lock(Gate)
        {
            var targets=(IDictionary)Field("clientMobTargets");
            if(targets.Contains(mob))
            {
                var old=targets[mob];var type=old.GetType();object Get(string name)=>type.GetField(name).GetValue(old);
                targets[mob]=Activator.CreateInstance(type,new object[]{Get("X"),Get("Y"),Get("Dir"),0,Get("MaxLife"),
                    Get("AnimPayload"),Get("StatePayload"),Get("Time"),Get("Dx"),Get("Dy"),Get("ReceivedFrame"),Get("ForcePositionSnap"),Get("ForceVerticalPositionSnap")});
            }
            ((IDictionary)Field("clientLastReportedMobLife"))[mob]=0;
        }
    }
    static void EliteFencePendingPositiveLife(ref IReadOnlyList<NetNode.MobStateSnapshot> states)
    {
        if(states==null || !EliteClient())return;
        lock(Gate)
        {
            var forward=(IDictionary)Field("IdToMob");List<NetNode.MobStateSnapshot> filtered=null;
            for(int i=0;i<states.Count;i++)
            {
                var state=states[i];bool reject=state.Life>0 && Current(state.Generation) && forward.Contains(state.Index) &&
                    forward[state.Index] is Mob mob && (EliteHasNativeFault(mob) || EliteScopedPending(mob));
                if(reject)
                {
                    if(filtered==null){filtered=new List<NetNode.MobStateSnapshot>();for(int j=0;j<i;j++)filtered.Add(states[j]);}
                }
                else filtered?.Add(state);
            }
            // Caller still owns and returns the original pooled batch; this replacement is ours.
            if(filtered!=null)states=filtered;
        }
    }
}
