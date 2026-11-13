using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using dc.level;
using dc.pr;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using Hashlink.Virtuals;

public sealed partial class CoopSyncFixes
{
    private sealed class BranchSalt {public string Key;}
    private static readonly ConditionalWeakTable<LevelMap,BranchSalt> BranchSalts=new();
    private static void InitializeBranches(Harmony harmony)
    {
        if(Disabled("BRANCHES"))return;
        Patch(harmony,Mobs,"ComputeLevelIdentityToken",null,nameof(SaltBranchIdentity));
        Patch(harmony,Mobs,"DoesLevelMatchCurrentIdentityLocked",nameof(MatchTrackedNativeLevel));
        Patch(harmony,Mobs,"RebuildMobArray",nameof(RejectInactiveRebuild));
        harmony.Patch(Mobs.GetMethod("OnFrameUpdateCore",InstanceFlags),
            prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(ReconcileActiveBranch),Flags)));
    }
    private static void SaltBranchIdentity(Level level,ref int __result)
    {
        if(__result<=0 || level?.map?.rooms==null || level.map.rooms.length==0)return;
        var map=level.map;
        if(!BranchSalts.TryGetValue(map,out var salt))
        {
            var ids=new SortedSet<int>();bool root=false;
            for(int i=0;i<map.rooms.length;i++)
                if(map.rooms.getDyn(i) is Room room && room.isZChild)
                {
                    root=true;
                    if(room.zLinks!=null)for(int j=0;j<room.zLinks.length;j++)
                        if(room.zLinks.getDyn(j) is virtual_contentClue_doorId_id_map_ link)ids.Add(link.id);
                }
            // Do not cache an incompletely constructed branch. Its root boundary
            // exists before entitiesPostCreate, unlike runtime door entities.
            if(root && ids.Count==0)return;
            salt=new BranchSalt {Key=root?"SUB:"+string.Join(",",ids):"MAIN"};
            BranchSalts.Add(map,salt);
        }
        if(salt.Key=="MAIN")return;
        uint hash=(uint)__result;
        foreach(char c in salt.Key){hash^=c;hash*=16777619;}
        __result=(int)(hash & 0x7fffffff);if(__result==0)__result=1;
    }
    private static bool MatchTrackedNativeLevel(Level level,ref bool __result)
    {
        var current=Field("currentLevel") as Level;
        if(level==null || current==null || !(bool)Field("s_levelIdentityReady"))return true;
        __result=level.HashlinkPointer==current.HashlinkPointer;return false;
    }
    private static Level ActiveBranch()
    {
        var net=ModEntry._net;var game=Game.Class.ME;var hero=game?.hero;
        if(net==null || !net.IsAlive || game==null || game.curCine!=null || hero==null || hero.destroyed || hero.life<=0 ||
            !CsfRun.InActualRun())return null;
        return hero._level;
    }
    private static bool RejectInactiveRebuild(Level level,ref bool __result)
    {
        var active=ActiveBranch();
        if(active==null || level!=null && level.HashlinkPointer==active.HashlinkPointer)return true;
        __result=false;return false;
    }
    private static void ReconcileActiveBranch()
    {
        var active=ActiveBranch();if(active?.entities==null || active.map==null)return;
        lock(Gate)
        {
            var tracked=Field("currentLevel") as Level;
            if(tracked?.HashlinkPointer==active.HashlinkPointer && (bool)Field("s_levelIdentityReady"))return;
            // A cached native level does not run entitiesPostCreate on return.
            // Clear only after the actual hero has entered and the cine has ended;
            // otherwise vanilla's replace_empty guard keeps the parent registry.
            Invoke("ClearTrackingForLevelChange");
            if(!(bool)Invoke("RebuildMobArray",active))
                throw new InvalidOperationException("Cannot rebuild authoritative active branch");
        }
    }
}
