using System;
using System.Collections.Generic;
using System.Reflection;
using dc.en;
using DeadCellsMultiplayerMod;
using HarmonyLib;

public sealed partial class CoopSyncFixes
{
    private sealed class RegistryBindScope
    {
        public RegistryBindScope Previous;
        public readonly HashSet<Mob> Reserved=new(ReferenceEqualityComparer.Instance);
    }
    [ThreadStatic] private static RegistryBindScope RegistryScope;
    private static void InitializeRegistryBatches(Harmony harmony)
    {
        if(Disabled("REGISTRYBATCH"))return;
        harmony.Patch(Mobs.GetMethod("ConsumeIncomingMobRegistry",Flags),
            prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(EnterRegistryBatch),Flags)),
            finalizer:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(LeaveRegistryBatch),Flags)));
        var bind=Mobs.GetMethod("TryBindUnboundMobByTypeAndSpawnLocked",Flags,null,
            new[] {typeof(int),typeof(string),typeof(double),typeof(double),typeof(HashSet<Mob>),typeof(Mob).MakeByRefType(),typeof(bool).MakeByRefType()},null);
        harmony.Patch(bind,prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(SupplyRegistryReservation),Flags)),
            postfix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(ReserveRegistryBinding),Flags)) {priority=HarmonyLib.Priority.Last});
    }
    private static void EnterRegistryBatch(out RegistryBindScope __state)
    {
        // gc-opt call sites: while no batch is open on this thread, the scope of the last finished outermost batch is used
        // again (its reservation set was emptied when that batch finished and nothing else holds it); a nested batch gets a
        // new scope, as before.
        var previous=RegistryScope;
        __state=(previous==null?System.Threading.Interlocked.Exchange(ref CsfRegistrySpare.Slot[0],null) as RegistryBindScope:null)??new RegistryBindScope();
        __state.Previous=previous;RegistryScope=__state;
    }
    private static Exception LeaveRegistryBatch(Exception __exception,RegistryBindScope __state)
    {
        if(__state!=null){RegistryScope=__state.Previous;__state.Reserved.Clear();if(__state.Previous==null)CsfRegistrySpare.Slot[0]=__state;}
        return __exception;
    }
    private static class CsfRegistrySpare
    {
        internal static readonly object[] Slot=new object[1];   // [0]: a finished outermost RegistryBindScope, or null
    }
    private static void SupplyRegistryReservation(ref HashSet<Mob> reservedMobs)
    {
        // The pinned DLL passes null here; only its registry consumer is a batch.
        // Standalone state/combat binds retain their normal ambiguity checks.
        if(reservedMobs==null && RegistryScope!=null)reservedMobs=RegistryScope.Reserved;
    }
    private static void ReserveRegistryBinding(Mob bound,bool __result,HashSet<Mob> reservedMobs)
    {
        if(RegistryScope!=null && __result && bound!=null)reservedMobs?.Add(bound);
    }
}
