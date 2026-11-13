using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using dc.en.inter;
using DeadCellsMultiplayerMod;

public sealed partial class CoopSyncFixes
{
    static readonly Type DoorSync=typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.Interaction.InteractionSync",true);
    static readonly MethodInfo IsTransitionQuiesced=Mobs.GetMethod("IsSyncQuiescedForTransition",Flags);
    static readonly MethodInfo NativeDoorOpen=typeof(Door).GetMethods(InstanceFlags|BindingFlags.DeclaredOnly).Single(m=>m.Name=="open" && m.GetParameters().Length==3);
    static long CorrectedDoorOpenArguments,NativeDoorStateReads,HeldDeathConsumes;
    static void InitializeDoorAndDeathBoundaries(Harmony harmony)
    {
        if(!Disabled("DOORS")){
            harmony.Patch(DoorSync.GetMethod("ApplyRemoteDoorEvents",InstanceFlags),
                transpiler:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(RewriteRemoteDoorCalls),Flags)));
            harmony.Patch(DoorSync.GetMethod("GetAuthoritativeDoorState",InstanceFlags),prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(ReadNativeDoorState),Flags)));
        }
        if(!Disabled("DIE_BOUNDARY"))Patch(harmony,Mobs,"ConsumeIncomingMobDies",nameof(HoldDeathsBeforeConsume));
    }
    static IEnumerable<CodeInstruction> RewriteRemoteDoorCalls(IEnumerable<CodeInstruction> instructions)
    {
        int replaced=0;var shim=typeof(CoopSyncFixes).GetMethod(nameof(ReplayRemoteDoorOpen),Flags);
        foreach(var instruction in instructions){if(instruction.Calls(NativeDoorOpen)){instruction.opcode=OpCodes.Call;instruction.operand=shim;replaced++;}yield return instruction;}
        if(replaced!=2)throw new InvalidOperationException("Pinned remote door callsites changed");
    }
    static void ReplayRemoteDoorOpen(Door door,int direction,int? duration,double? ratio)
    {
        // Patch the managed receiver callsites, not a generated native wrapper (which DCCM hooks).
        if(!duration.HasValue && (direction==180||direction==300)){duration=direction;direction=1;CorrectedDoorOpenArguments++;}
        NativeDoorOpen.Invoke(door,new object[]{direction,duration,ratio});
    }
    static bool ReadNativeDoorState(Door door,ref string __result)
    {
        if(door==null || door.destroyed)return true;
        // _closed updates native collision immediately; openFactor is only the animated pose.
        // TriggeredDoor remains locked after an elite script opens it. locked controls
        // activation, while native applyCollisions depends on _closed (including its override).
        __result=door.broken?"state_broken":!door._closed?"state_open":"state_closed";
        NativeDoorStateReads++;
        return false;
    }
    static bool HoldDeathsBeforeConsume()
    {
        if(!CsfMobs.SyncQuiesced())return true;   // IsSyncQuiescedForTransition, entered on every call as before
        HeldDeathConsumes++;
        return false;
    }
}
