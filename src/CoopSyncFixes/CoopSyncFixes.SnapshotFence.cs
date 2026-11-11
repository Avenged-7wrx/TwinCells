using System;
using System.Collections;
using System.Collections.Generic;
using DeadCellsMultiplayerMod;
using HarmonyLib;

public sealed partial class CoopSyncFixes
{
    [ThreadStatic] private static int SnapshotApplyDepth;
    private static void InitializeSnapshotFence(Harmony harmony)
    {
        if(Disabled("SNAPSHOTFENCE"))return;
        Patch(harmony,Mobs,"ApplyIncomingHostMobStates",nameof(FenceSnapshotStates));
        Patch(harmony,Mobs,"ApplyIncomingHostMobMoves",nameof(FenceSnapshotMoves));
        Patch(harmony,Mobs,"ApplyIncomingHostMobAttacks",nameof(FenceSnapshotAttacks));
        harmony.Patch(Mobs.GetMethod("ApplyIncomingMobDies",Flags),prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(FenceSnapshotDeaths),Flags)) {priority=HarmonyLib.Priority.First});
        Patch(harmony,Mobs,"ResolveMobFromHitLocked",nameof(FenceSnapshotHit));
        harmony.Patch(typeof(NetNode).GetMethod("TryConsumeMobRegistry"),postfix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(FenceSnapshotRegistry),Flags)));
    }
    private static bool SnapshotAllows(int generation,int id,double? frame=null,bool death=false)
    {
        var net=ModEntry._net;
        if(SnapshotApplyDepth>0 || net==null || net.IsHost || !WorldJoins.TryGetValue(net,out var state) || !state.ManifestApplied ||
            state.Manifest==null || !LocalActivationCurrent(state))return true;
        var manifest=state.Manifest;
        if(generation!=manifest.Generation || id<=0)return false;
        foreach(var item in manifest.Entries)if(item.Id==id)
            return !item.Dead && (!frame.HasValue || double.IsFinite(frame.Value) && frame.Value>=manifest.Frame);
        // An allocator ID already issued before this snapshot but absent from it
        // belongs to another activation/dead object. Runtime births allocate above it.
        return id>manifest.HighWatermark && (!frame.HasValue || double.IsFinite(frame.Value) && frame.Value>=manifest.Frame);
    }
    private static IReadOnlyList<T> FilterSnapshot<T>(IReadOnlyList<T> input,Func<T,int> generation,Func<T,int> id,Func<T,double?> frame=null)
    {
        if(input==null || SnapshotApplyDepth>0)return input;
        List<T> filtered=null;
        for(int i=0;i<input.Count;i++)
        {
            var item=input[i];
            if(!SnapshotAllows(generation(item),id(item),frame?.Invoke(item)))
            {if(filtered==null){filtered=new List<T>();for(int j=0;j<i;j++)filtered.Add(input[j]);}}
            else filtered?.Add(item);
        }
        return filtered??input;
    }
    private static void FenceSnapshotStates(ref IReadOnlyList<NetNode.MobStateSnapshot> states)=>states=FilterSnapshot(states,s=>s.Generation,s=>s.Index,s=>s.Time);
    private static void FenceSnapshotMoves(ref IReadOnlyList<NetNode.MobMoveSnapshot> moves)=>moves=FilterSnapshot(moves,s=>s.Generation,s=>s.Index,s=>s.Time);
    private static void FenceSnapshotAttacks(ref IReadOnlyList<NetNode.MobAttack> attacks)=>attacks=FilterSnapshot(attacks,s=>s.Generation,s=>s.Index);
    private static void FenceSnapshotDeaths(ref IReadOnlyList<NetNode.MobDie> dies)=>dies=FilterSnapshot(dies,s=>s.Generation,s=>s.MobIndex);
    private static void FenceSnapshotRegistry(List<NetNode.MobRegistryEntry> entries)
    {entries?.RemoveAll(s=>!SnapshotAllows(s.Generation,s.NetId));}
    private static bool FenceSnapshotHit(NetNode.MobHit hit,ref dc.en.Mob __result)
    {
        var net=ModEntry._net;
        if(net==null)return true;
        if(net.IsHost)
        {
            lock(Gate)if(Current(hit.Generation) && ((IDictionary)Field("IdToMob")).Contains(hit.MobIndex))return true;
        }
        else if(SnapshotAllows(hit.Generation,hit.MobIndex))return true;
        __result=null;return false;
    }
}
