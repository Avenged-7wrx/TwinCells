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
            // The base mod's typed registry and accepted targets (the objects Field returned), read without boxing.
            CsfMobs.IdToMob.TryGetValue(id,out var mob);
            if(mob==null || !CsfMobs.MobToId.TryGetValue(mob,out var bound) || bound!=id ||
                !CsfMobs.TargetState(mob,out var life,out _,out var targetX,out var targetY))return;
            // The accepted interpolation target already resolves stale/out-of-order packets.
            if(life<=0)return;
            PlaceSleepingReplica(mob,targetX,targetY);
        }
    }
    private static void PlaceSleepingReplica(Mob mob,double worldX,double worldY)
    {
        if(mob==null || mob.destroyed || mob.life<=0 || !double.IsFinite(worldX) || !double.IsFinite(worldY) ||
            CsfMobCalls.IsBossMob(mob) || !CsfMobCalls.MobCulledLocally(mob))return;
        if(Math.Abs(CsfMobCalls.WorldX(mob)-worldX)<.01 && Math.Abs(CsfMobCalls.WorldY(mob)-worldY)<.01)return;
        var cellSize=Convert.ToDouble(Field("PixelsPerCase"));var x=worldX/cellSize;var y=worldY/cellSize;
        var cx=(int)Math.Floor(x);var cy=(int)Math.Floor(y);
        // Coordinate placement does not unlock AI, force native visibility or run animations. The same virtual method with
        // the same four values (two non-null fractions) as the reflective call; its exception leaves inside a
        // TargetInvocationException, as from MethodInfo.Invoke.
        try{CsfEntityCalls.SetPosCase(mob,cx,cy,x-cx,y-cy);}
        catch(Exception error){throw new TargetInvocationException(error);}
    }
    // Entity.setPosCase(int, int, double?, double?) as an open-instance delegate: it dispatches virtually on the mob, as
    // mob.GetType().GetMethod("setPosCase").Invoke(mob, ...) did (every declaration of the name overrides this one slot).
    private static class CsfEntityCalls
    {
        internal static readonly Action<Mob,int,int,double?,double?> SetPosCase=(Action<Mob,int,int,double?,double?>)Delegate.CreateDelegate(
            typeof(Action<Mob,int,int,double?,double?>),typeof(dc.Entity).GetMethod("setPosCase",BindingFlags.Instance|BindingFlags.Public,null,
                new[]{typeof(int),typeof(int),typeof(double?),typeof(double?)},null)??throw new MissingMethodException("dc.Entity","setPosCase"));
    }
}
