using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using dc.en;
using DeadCellsMultiplayerMod;
using HarmonyLib;

public sealed partial class CoopSyncFixes
{
    private static readonly MethodInfo IsSnapshotBoss=typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.Mobs.Bosses.BossSyncHelpers",true).GetMethod("IsBossMob",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
    private static void InitializeSleepingTransforms(Harmony harmony)
    {
        if(Disabled("CULLED"))return;
        Patch(harmony,Mobs,"ApplyIncomingHostMobStates",null,nameof(PlaceSleepingStates));
        Patch(harmony,Mobs,"ApplyIncomingHostMobMoves",null,nameof(PlaceSleepingMoves));
    }
    private static void PlaceSleepingStates(IReadOnlyList<NetNode.MobStateSnapshot> states)
    {if(states!=null)foreach(var state in states)PlaceAcceptedSleepingTarget(state.Generation,state.Index);}
    private static void PlaceSleepingMoves(IReadOnlyList<NetNode.MobMoveSnapshot> moves)
    {if(moves!=null)foreach(var move in moves)PlaceAcceptedSleepingTarget(move.Generation,move.Index);}
    private static void PlaceAcceptedSleepingTarget(int generation,int id)
    {
        if(ModEntry._net==null || ModEntry._net.IsHost)return;
        lock(Gate)
        {
            if(!Current(generation) || DeadOnClient(generation,id))return;
            var forward=(IDictionary)Field("IdToMob");var reverse=(IDictionary)Field("MobToId");var targets=(IDictionary)Field("clientMobTargets");
            var mob=forward.Contains(id)?forward[id] as Mob:null;
            if(mob==null || !reverse.Contains(mob) || (int)reverse[mob]!=id || !targets.Contains(mob))return;
            // The accepted interpolation target already resolves stale/out-of-order packets.
            var target=targets[mob];var type=target.GetType();
            if((int)type.GetField("Life").GetValue(target)<=0)return;
            PlaceSleepingReplica(mob,(double)type.GetField("X").GetValue(target),(double)type.GetField("Y").GetValue(target));
        }
    }
    private static void PlaceSleepingReplica(Mob mob,double worldX,double worldY)
    {
        if(mob==null || mob.destroyed || mob.life<=0 || !double.IsFinite(worldX) || !double.IsFinite(worldY) ||
            (bool)IsSnapshotBoss.Invoke(null,new object[] {mob}) || !(bool)Invoke("IsMobCulledLocally",mob))return;
        if(Math.Abs((double)Invoke("GetWorldX",mob)-worldX)<.01 && Math.Abs((double)Invoke("GetWorldY",mob)-worldY)<.01)return;
        var cellSize=Convert.ToDouble(Field("PixelsPerCase"));var x=worldX/cellSize;var y=worldY/cellSize;
        var cx=(int)Math.Floor(x);var cy=(int)Math.Floor(y);
        // Coordinate placement does not unlock AI, force native visibility or run animations.
        mob.GetType().GetMethod("setPosCase").Invoke(mob,new object[] {cx,cy,x-cx,y-cy});
    }
}
