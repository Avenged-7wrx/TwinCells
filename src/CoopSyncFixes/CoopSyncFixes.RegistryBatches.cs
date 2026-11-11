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
        __state=new RegistryBindScope {Previous=RegistryScope};RegistryScope=__state;
    }
    private static Exception LeaveRegistryBatch(Exception __exception,RegistryBindScope __state)
    {
        if(__state!=null){RegistryScope=__state.Previous;__state.Reserved.Clear();}
        return __exception;
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
