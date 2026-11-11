using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using dc;
using dc.cine;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using Serilog;

public sealed partial class CoopSyncFixes
{
    private sealed class HostLeaseSet {public readonly List<(object Connection,WorldTransferSlot Slot)> Entries=new();}
    private static readonly ConditionalWeakTable<NetNode,HostLeaseSet> HostJoinLeases=new();
    private static readonly List<Delegate> TransitionPhaseHooks=new();
    private sealed class NativeTransitionGate {public NetNode Net;public LevelTransition Cine;public Delegate Original;}
    private static readonly List<NativeTransitionGate> NativeTransitionGates=new();
    private static long HeldTransitionFrames;
    private static void InitializeJoinLease(Harmony harmony)
    {
        if(Disabled("LEASE"))return;
        var exits=typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.MultiplayerModUI.LevelExit.LevelExitSync",true);
        harmony.Patch(exits.GetMethod("TryBeginCoordinatedTransition",InstanceFlags),
            prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(WaitForJoiningPlayers),Flags)));
        // LevelTransition schedules loadNewLevel through its native delayer, before its
        // presentation script has any queues. Hold that single void callback, then invoke
        // the original once. Returning from goto itself must still return a real transition.
        var entry=typeof(LevelTransition).Assembly.GetType("dc.cine.Hook_LevelTransition",true).GetEvent("loadNewLevel");
        var p=entry.EventHandlerType.GetMethod("Invoke").GetParameters();
        if(p.Length!=2)throw new InvalidOperationException("Unexpected level-load callback signature");
        var types=new[] {p[0].ParameterType,p[1].ParameterType};
        var method=new DynamicMethod("CoopJoinHoldNativeLoad",typeof(void),types,typeof(CoopSyncFixes),true);
        var il=method.GetILGenerator();var run=il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Call,typeof(CoopSyncFixes).GetMethod(nameof(DeferNativeTransitionLoad),Flags));
        il.Emit(OpCodes.Brfalse,run);il.Emit(OpCodes.Ret);il.MarkLabel(run);
        il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Callvirt,types[0].GetMethod("Invoke"));il.Emit(OpCodes.Ret);
        var hook=method.CreateDelegate(entry.EventHandlerType);RuntimeHelpers.PrepareDelegate(hook);TransitionPhaseHooks.Add(hook);entry.AddEventHandler(null,hook);
    }
    private static void BeginHostJoinLease(NetNode net,object connection,WorldTransferSlot slot)
    {
        if(Disabled("LEASE") || slot.FinishAcknowledged || slot.Cancelled)return;
        if(slot.LeaseUntil==0)slot.LeaseUntil=Environment.TickCount64+30000;
        var set=HostJoinLeases.GetValue(net,_=>new HostLeaseSet());
        if(!set.Entries.Exists(e=>ReferenceEquals(e.Slot,slot)))set.Entries.Add((connection,slot));
    }
    private static void TickHostJoinLeases(NetNode net)
    {
        if(net==null || !net.IsHost || !HostJoinLeases.TryGetValue(net,out var set))return;
        var live=new HashSet<object>();
        if(net.IsAlive)lock(NetField(net,"_clientsLock"))
            foreach(DictionaryEntry pair in (IDictionary)NetField(net,(bool)NetField(net,"_useSteamTransport")?"_steamClients":"_clients"))live.Add(pair.Value);
        var now=Environment.TickCount64;
        for(int i=set.Entries.Count-1;i>=0;i--)
        {
            var entry=set.Entries[i];var slot=entry.Slot;
            if(slot.FinishAcknowledged || slot.Cancelled || !live.Contains(entry.Connection)){set.Entries.RemoveAt(i);continue;}
            if(now<slot.LeaseUntil)continue;
            // A timed-out join loses its transport authority before transitions are released.
            slot.Cancelled=true;set.Entries.RemoveAt(i);
            var method=(bool)NetField(net,"_useSteamTransport")?"CleanupHostSteamClient":"CleanupHostClient";
            typeof(NetNode).GetMethod(method,InstanceFlags).Invoke(net,new[] {entry.Connection});
            Log.Warning("[CoopSyncFixes] Joining peer timed out before final acknowledgement; connection closed");
        }
    }
    private static bool HostJoinLeasePending(NetNode net)
    {
        if(Disabled("LEASE") || net==null || !net.IsHost || !net.IsAlive)return false;
        TickHostJoinLeases(net);
        return HostJoinLeases.TryGetValue(net,out var set) && set.Entries.Exists(e=>!e.Slot.FinishAcknowledged && !e.Slot.Cancelled);
    }
    private static bool WaitForJoiningPlayers(NetNode net,ref bool __result)
    {if(!HostJoinLeasePending(net))return true;__result=false;return false;}
    private static bool HoldJoiningTransition(GameCinematic cine)
    {
        if(cine is not LevelTransition)return false;
        var net=ModEntry._net;
        var hold=HostJoinLeasePending(net) || net!=null && !net.IsHost && PendingWorld(net,out var state) && state.Finalizing;
        return hold;
    }
    private static bool DeferNativeTransitionLoad(Delegate original,LevelTransition cine)
    {
        if(!HoldJoiningTransition(cine))return false;
        if(!NativeTransitionGates.Exists(g=>g.Cine.HashlinkPointer==cine.HashlinkPointer))
            NativeTransitionGates.Add(new NativeTransitionGate {Net=ModEntry._net,Cine=cine,Original=original});
        return true;
    }
    private static void TickNativeTransitionGates()
    {
        for(int i=NativeTransitionGates.Count-1;i>=0;i--)
        {
            var gate=NativeTransitionGates[i];
            if(gate.Cine.destroyed){NativeTransitionGates.RemoveAt(i);continue;}
            if(gate.Net==ModEntry._net && gate.Net?.IsAlive==true && HoldJoiningTransition(gate.Cine)){HeldTransitionFrames++;continue;}
            NativeTransitionGates.RemoveAt(i);gate.Original.DynamicInvoke(gate.Cine);
        }
    }
}
