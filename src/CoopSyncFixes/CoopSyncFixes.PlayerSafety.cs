using System;
using System.Collections.Generic;
using HarmonyLib;
using DeadCellsMultiplayerMod;

public sealed partial class CoopSyncFixes
{
    private static void InitializePlayerSafety(Harmony harmony)
    {
        if(!Disabled("RELOAD"))Patch(harmony,WorldData,"ShouldSuppressClientLevelReload",null,nameof(RequireLiveWorldForReload));
        if(!Disabled("REVIVE"))harmony.Patch(typeof(ModEntry).GetMethod("IsReviverPositionValidForLocalCorpse",InstanceFlags),
            prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(RequireLivingReviver),Flags)));
    }
    private static void RequireLiveWorldForReload(ref bool __result)
    {
        if(__result)return;
        var game=dc.pr.Game.Class.ME;var hero=ModEntry.me;
        bool live=false;
        if((bool)Lobby.GetMethod("IsInActualRun",Flags).Invoke(null,null) && game!=null && !game.destroyed && hero!=null && !hero.destroyed && hero._level!=null && !hero._level.destroyed)
        {
            var current=game.hero;
            live=current!=null && !current.destroyed && current.HashlinkPointer==hero.HashlinkPointer &&
                game.curLevel!=null && !game.curLevel.destroyed && game.curLevel.HashlinkPointer==hero._level.HashlinkPointer;
        }
        __result=!live;
    }
    private static bool RequireLivingReviver(ModEntry __instance,NetNode net,int reviverId,ref bool __result)
    {
        __result=false;
        if(net==null || !net.IsAlive || reviverId<=0 || reviverId==net.id)return false;
        if((bool)typeof(ModEntry).GetMethod("IsRemotePlayerDowned",Flags).Invoke(null,new object[] {reviverId}))return false;
        var active=new HashSet<int>();net.CopyRemoteUserIdsTo(active,includePrimary:true);
        if(!active.Contains(reviverId) || !net.TryGetRemotePosition(reviverId,out var x,out var y,out var level) ||
            !double.IsFinite(x) || !double.IsFinite(y))return false;
        var local=(string)typeof(ModEntry).GetMethod("GetCurrentLevelId",InstanceFlags).Invoke(__instance,null);
        if(string.IsNullOrEmpty(local) || !string.Equals(local,level,StringComparison.Ordinal))return false;
        return true;
    }
}
