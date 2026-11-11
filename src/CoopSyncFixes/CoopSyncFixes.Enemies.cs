using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using dc.en;
using HarmonyLib;

public sealed partial class CoopSyncFixes
{
    private static void InitializeEnemyExtensions(Harmony harmony)
    {
        if (!Disabled("DUPLICATES") || !Disabled("BATCH"))
        {
            var bind = Mobs.GetMethod("TryBindUnboundMobByTypeAndSpawnLocked", Flags, null,
                new[] { typeof(int), typeof(string), typeof(double), typeof(double), typeof(HashSet<Mob>),
                    typeof(Mob).MakeByRefType(), typeof(bool).MakeByRefType() }, null);
            if (bind == null) throw new MissingMethodException("Exact seven-argument registry binder not found");
            harmony.Patch(bind, postfix: new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(AllowDistinctHostId), Flags)));
            harmony.Patch(typeof(NetNode).GetMethod("TryConsumeMobRegistry", BindingFlags.Public | BindingFlags.Instance),
                postfix: new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(FilterDeadRegistryEntries), Flags)));
        }
        if (!Disabled("RETENTION")) Patch(harmony, Mobs, "FlushHostDeathTombstoneResends", nameof(RetainHostDeathUntilTtl));
    }
    private static void AllowDistinctHostId(int netId,string type,double x,double y,HashSet<Mob> reservedMobs,ref Mob bound,ref bool __result,ref bool duplicateTwin)
    {
        if (__result || DeadCellsMultiplayerMod.ModEntry._net == null ||
            DeadCellsMultiplayerMod.ModEntry._net.IsHost) return;
        lock (Gate)
        {
            var generation = (int)Field("s_levelIdentityToken");
            if(ConfirmedDead(generation,netId))return;
            // The shipped DLL refuses near-equal candidates even with a batch reservation set.
            // Distinct host IDs in a batch may safely consume interchangeable same-type locals.
            if(!Disabled("BATCH") && reservedMobs!=null && netId>0 && double.IsFinite(x) && double.IsFinite(y))
            {
                var reverse=(IDictionary)Field("MobToId");var forward=(IDictionary)Field("IdToMob");
                if(!forward.Contains(netId))
                {
                    var reservedPointers=new HashSet<IntPtr>();foreach(var used in reservedMobs)reservedPointers.Add(used.HashlinkPointer);
                    Mob best=null;var distance=192.0*192.0;
                    foreach(Mob candidate in (IEnumerable)Field("trackedMobs"))
                    {
                        if(candidate==null || reverse.Contains(candidate) || reservedPointers.Contains(candidate.HashlinkPointer) ||
                            !(bool)Invoke("IsStateRebindCandidateLocked",candidate) || !(bool)Invoke("DoesMobMatchStateType",candidate,type))continue;
                        var dx=(double)Invoke("GetWorldX",candidate)-x;var dy=(double)Invoke("GetWorldY",candidate)-y;var d=dx*dx+dy*dy;
                        if(d<=distance){best=candidate;distance=d;}
                    }
                    if(best!=null)
                    {Invoke("TryRebindTrackedMobSyncIdLocked",best,netId);bound=best;__result=true;duplicateTwin=false;return;}
                }
            }
            if(HasDeathIntent(generation,netId))duplicateTwin=true;
            else if(!Disabled("DUPLICATES"))duplicateTwin = false;
        }
    }
    private static void FilterDeadRegistryEntries(NetNode __instance, ref bool __result, ref List<NetNode.MobRegistryEntry> entries)
    {
        if (!__result || __instance.IsHost || entries == null) return;
        lock (Gate) entries.RemoveAll(entry => Current(entry.Generation) && ConfirmedDead(entry.Generation, entry.NetId));
        if (entries.Count == 0)
        {
            // The caller returns before its finally block on an empty batch. Return our rental here.
            NetNode.ReleaseConsumedList(entries);
            entries = null;
            __result = false;
        }
    }
    private static void RetainHostDeathUntilTtl(NetNode net)
    {
        if (net == null || !net.IsAlive || !net.IsHost) return;
        lock (Gate)
        {
            foreach (var value in ((IDictionary)Field("s_hostDeathTombstonesBySyncId")).Values)
            {
                var remaining = value.GetType().GetField("SendsRemaining");
                if ((int)remaining.GetValue(value) < 1) remaining.SetValue(value, 1);
            }
        }
        // Original generation/TTL/cadence checks remain authoritative. Only the shorter send
        // budget is removed; records are still discarded after the existing 600-frame TTL.
    }
}
