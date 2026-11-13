using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DeadCellsMultiplayerMod;
using HarmonyLib;

public sealed partial class CoopSyncFixes
{
    private static readonly Type ExitCoordinator=typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.MultiplayerModUI.LevelExit.LevelExitSync",true);
    private sealed class ExitPeers
    {
        public NetNode Net;public HashSet<int> Ids=new();public Dictionary<int,object> Connections=new();
        public Dictionary<int,ExitDownedFence> Downed=new();
    }
    private sealed class ExitDownedFence {public object Record;public long Updated;}
    private sealed class ExitCacheScope
    {
        public object Owner;public ExitPeers Peers;public ExitCacheScope Previous;
    }
    private static readonly ConditionalWeakTable<object,ExitPeers> LastExitPeers=new();
    [ThreadStatic]private static ExitCacheScope CurrentExitCache;
    private static object ExitField(object owner,string name)=>ExitCoordinator.GetField(name,InstanceFlags).GetValue(owner);
    private static object ExitCall(object owner,string name,params object[] args)=>ExitCoordinator.GetMethod(name,InstanceFlags).Invoke(owner,args);
    private static object ReadExitDownedRecord(NetNode net,int id)
    {
        if(net!=ModEntry._net || ModEntry.Instance==null)return null;
        return ((IDictionary)typeof(ModEntry).GetField("_remoteDowned",InstanceFlags).GetValue(ModEntry.Instance))[id];
    }
    private static long ExitDownedStamp(object record)=>record==null?0:(long)record.GetType().GetField("UpdatedAtTicks",InstanceFlags).GetValue(record);
    private static bool CurrentExitDowned(ExitPeers peers,int id)
    {
        if(!peers.Connections.ContainsKey(id) || !peers.Downed.TryGetValue(id,out var fence))return false;
        var record=ReadExitDownedRecord(peers.Net,id);
        return record!=null && (!ReferenceEquals(record,fence.Record) || ExitDownedStamp(record)>fence.Updated);
    }
    private static void FilterExitDowned(int userId,int localId,ref bool __result)
    {
        var scope=CurrentExitCache;
        if(__result && userId!=localId && scope!=null && !CurrentExitDowned(scope.Peers,userId))__result=false;
    }
    private static ExitPeers ReadExitPeers(NetNode net)
    {
        var peers=new ExitPeers {Net=net};
        // Copy IDs and transport incarnations under the same native network lock.
        // Release it before reading downed state or invoking any native game code.
        lock(NetField(net,"_clientsLock"))
        {
            net.CopyConnectedClientIdsTo(peers.Ids);
            // "_clients" then "_steamClients", each table in its own order, as before; without a boxed entry per client.
            CsfExitTables.Collect(NetField(net,"_clients"),peers.Ids,peers.Connections);
            CsfExitTables.Collect(NetField(net,"_steamClients"),peers.Ids,peers.Connections);
        }
        if(net.id>0)peers.Ids.Add(net.id);
        return peers;
    }
    private static bool ApplyExitPeers(object owner,ExitPeers peers)
    {
        var active=(HashSet<int>)ExitField(owner,"_activePlayerIds");var scratch=(HashSet<int>)ExitField(owner,"_activePlayerScratchIds");
        // UnionWith / SetEquals of the same sets, written as typed loops (no boxed enumerator): the same additions in the same
        // order, and the same answer (both sets use the default comparer).
        scratch.Clear();CsfExitTables.AddAll(scratch,peers.Ids);var changed=!CsfExitTables.SameSet(active,scratch);
        if(changed){active.Clear();CsfExitTables.AddAll(active,scratch);}
        var states=(IDictionary)ExitField(owner,"_playerStates");
        var observed=LastExitPeers.TryGetValue(owner,out var previous);
        foreach(var pair in peers.Connections)
        {
            var same=observed && previous.Net==peers.Net && previous.Connections.TryGetValue(pair.Key,out var old) && ReferenceEquals(old,pair.Value);
            if(!same)
            {
                changed=true;if(states.Contains(pair.Key))states.Remove(pair.Key);
                var record=ReadExitDownedRecord(peers.Net,pair.Key);peers.Downed[pair.Key]=new ExitDownedFence {Record=record,Updated=ExitDownedStamp(record)};
            }
            else peers.Downed[pair.Key]=previous.Downed[pair.Key];
        }
        LastExitPeers.AddOrUpdate(owner,peers);   // = Remove + Add, in place
        return changed;
    }
    // The connection tables of NetNode (Dictionary<int, private connection class>) walked with their own typed enumerator.
    private static class CsfExitTables
    {
        private static readonly MethodInfo WalkTyped=typeof(CsfExitTables).GetMethod(nameof(Walk),BindingFlags.Static|BindingFlags.NonPublic);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type,Action<object,HashSet<int>,Dictionary<int,object>>> Walkers=new();
        // For every entry of map, in its order, whose key is in ids: into[key] = value. A table of another shape takes the
        // non-generic walk the call site had before.
        internal static void Collect(object map,HashSet<int> ids,Dictionary<int,object> into)
        {
            var type=map.GetType();
            if(!Walkers.TryGetValue(type,out var walk))walk=Walkers.GetOrAdd(type,Bind(type));
            if(walk!=null){walk(map,ids,into);return;}
            foreach(DictionaryEntry pair in (IDictionary)map)if(ids.Contains((int)pair.Key))into[(int)pair.Key]=pair.Value;
        }
        private static Action<object,HashSet<int>,Dictionary<int,object>> Bind(Type type)
        {
            if(!type.IsGenericType || type.GetGenericTypeDefinition()!=typeof(Dictionary<,>))return null;
            var arguments=type.GetGenericArguments();
            if(arguments[0]!=typeof(int) || arguments[1].IsValueType)return null;
            return WalkTyped.MakeGenericMethod(arguments[1]).CreateDelegate<Action<object,HashSet<int>,Dictionary<int,object>>>();
        }
        private static void Walk<T>(object map,HashSet<int> ids,Dictionary<int,object> into) where T:class
        {foreach(var pair in (Dictionary<int,T>)map)if(ids.Contains(pair.Key))into[pair.Key]=pair.Value;}
        internal static void AddAll(HashSet<int> target,HashSet<int> source){foreach(var id in source)target.Add(id);}
        internal static bool SameSet(HashSet<int> a,HashSet<int> b)
        {
            if(a.Count!=b.Count)return false;
            foreach(var id in b)if(!a.Contains(id))return false;
            return true;
        }
    }
    private static bool RefreshHostExitPeers(object __instance,NetNode net,ref bool __result)
    {
        if(net?.IsHost!=true)return true;
        __result=ApplyExitPeers(__instance,ReadExitPeers(net));
        if(__result)ExitCall(__instance,"MarkExitUiStateDirty");
        return false;
    }
    private static void BeginExitCache(object __instance,NetNode net,ref ExitCacheScope __state)
    {
        if(net?.IsHost!=true)return;
        var peers=ReadExitPeers(net);var changed=ApplyExitPeers(__instance,peers);
        __state=new ExitCacheScope {Owner=__instance,Peers=peers,Previous=CurrentExitCache};CurrentExitCache=__state;
        changed|=(bool)ExitCall(__instance,"PrunePlayerStates",net.id);
        changed|=(bool)ExitCall(__instance,"RefreshDownedSignature",net.id);
        if(changed)ExitCall(__instance,"MarkExitUiStateDirty");
    }
    private static Exception EndExitCache(Exception __exception,ExitCacheScope __state)
    {
        if(__state!=null)CurrentExitCache=__state.Previous;
        return __exception;
    }
    private static bool CountHostExitPeers(object __instance,NetNode net,ref int __result)
    {
        if(net?.IsHost!=true)return true;
        var scope=CurrentExitCache;
        var peers=scope?.Owner==__instance && scope.Peers.Net==net?scope.Peers:ReadExitPeers(net);
        if(peers!=scope?.Peers && ApplyExitPeers(__instance,peers))ExitCall(__instance,"MarkExitUiStateDirty");
        var count=0;
        foreach(var id in peers.Ids)
        {
            var downed=(bool)ExitCoordinator.GetMethod("IsPlayerDownedForExit",Flags).Invoke(null,new object[]{id,net.id});
            if(!downed || id!=net.id && !CurrentExitDowned(peers,id))count++;
        }
        __result=Math.Max(1,count);return false;
    }
    private static void InitializeExitMembership(Harmony harmony)
    {
        if(Disabled("EXIT_MEMBERSHIP"))return;
        harmony.Patch(ExitCoordinator.GetMethod("RefreshActivePlayers",InstanceFlags),prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(RefreshHostExitPeers),Flags)));
        harmony.Patch(ExitCoordinator.GetMethod("ComputeExpectedPlayerCount",InstanceFlags),prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(CountHostExitPeers),Flags)));
        harmony.Patch(ExitCoordinator.GetMethod("IsPlayerDownedForExit",Flags),postfix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(FilterExitDowned),Flags)));
        harmony.Patch(ExitCoordinator.GetMethod("EnsureReadyStateCache",InstanceFlags),
            prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(BeginExitCache),Flags)),
            finalizer:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(EndExitCache),Flags)));
    }
}
