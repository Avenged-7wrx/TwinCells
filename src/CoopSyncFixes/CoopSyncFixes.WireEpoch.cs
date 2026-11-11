using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using Newtonsoft.Json;
using dc.pr;

public sealed partial class CoopSyncFixes
{
    private const string EpochTag="MOBEPOCH|";
    private static bool WireEnabled=>!Disabled("WIREEPOCH");
    private sealed class WireTicket
    {
        public readonly string RunNonce,ConnectionNonce;public readonly long Epoch;public readonly int Generation,Floor;
        public WireTicket(string run,long epoch,int generation,int floor,string connection)
        {RunNonce=run;Epoch=epoch;Generation=generation;Floor=floor;ConnectionNonce=connection;}
    }
    // Published on the game thread, then treated as immutable. No native objects
    // or mutable WorldJoin state are ever read by the socket/parser threads.
    private sealed class WireAuthority
    {
        public string RunNonce,Level;public Guid SessionId;public long RunId,Epoch;public int Sequence,Generation,Floor;public double Seed;
        public WireTicket Ticket(string connection)=>new(RunNonce,Epoch,Generation,Floor,connection);
    }
    private sealed class WirePeer {public WireTicket Ticket;public string RequestToken;public bool Open;}
    private sealed class WireState
    {
        public WireAuthority Host,Expected;public WireTicket ClientTicket;public bool ClientOpen;public long NextNotice;
        public readonly Dictionary<int,WirePeer> Peers=new();
    }
    private static readonly ConditionalWeakTable<NetNode,WireState> WireStates=new();
    private sealed class RebuildWireScope {public RebuildWireScope Previous;public int Floor;}
    [ThreadStatic] private static RebuildWireScope RebuildWire;
    private sealed class WireReceiveScope {public object Sync;public WireTicket Ticket;}
    private static long WireAccepted,WireRejected,WireUnstamped,WireSent;
    private static object WireSync(NetNode net)=>NetField(net,"_sync");
    private static bool SameWire(WireTicket a,WireTicket b)=>a!=null && b!=null && a.RunNonce==b.RunNonce &&
        a.Epoch==b.Epoch && a.Generation==b.Generation && a.Floor==b.Floor;
    private static bool MobLine(string line)=>line!=null && line.StartsWith("MOB",StringComparison.OrdinalIgnoreCase);
    private static void InitializeWireEpoch(Harmony harmony)
    {
        if(!WireEnabled)return;
        harmony.Patch(Mobs.GetMethod("RebuildMobArray",Flags),prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(BeginWireRebuild),Flags)),
            finalizer:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(EndWireRebuild),Flags)));
        Patch(harmony,Mobs,"AssignHostNetIdsForRebuildLocked",nameof(CaptureWireFloor));
        Patch(harmony,Mobs,"QueueHostMobRegistryAfterRebuild",nameof(PublishHostWireEpoch));
        Patch(harmony,Mobs,"ResetMobTrackingLocked",nameof(CloseClientWireForReset));
        harmony.Patch(typeof(NetNode).GetMethod("SendLineSafe",InstanceFlags),prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(StampProducedMobLine),Flags)));
        harmony.Patch(typeof(NetNode).GetMethod("BroadcastLineSafe",InstanceFlags),prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(FanoutWireBroadcast),Flags)));
        foreach(var method in new[] {"SendLineToClientSafe","SendLineToSteamClientSafe"})
            harmony.Patch(typeof(NetNode).GetMethod(method,InstanceFlags),prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(AddressWireToPeer),Flags)));
        foreach(var method in new[] {"ResolveSteamSendType","IsDroppableTcpRealtimeLine","IsRealtimeSteamLine"})
            harmony.Patch(typeof(NetNode).GetMethod(method,Flags),prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(ClassifyWireInner),Flags)));
        harmony.Patch(typeof(NetNode).GetMethod("TryHandleMobTrafficFastPath",InstanceFlags),prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(KeepWireEventOrdered),Flags)));
        harmony.Patch(typeof(NetNode).GetMethod("TryHandleMobTrafficLineCore",InstanceFlags),
            prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(ValidateWireReceive),Flags)),
            postfix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(PreserveWireRelay),Flags)),
            finalizer:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(ReleaseWireReceive),Flags)));
        harmony.Patch(typeof(NetNode).GetMethod("HandleLine",InstanceFlags),prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(ReadWireNotice),Flags)) {priority=HarmonyLib.Priority.First});
        foreach(var method in new[] {"CleanupHostClient","CleanupHostSteamClient"})
            harmony.Patch(typeof(NetNode).GetMethod(method,InstanceFlags),prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(RetireWirePeer),Flags)));
    }
    private static void BeginWireRebuild(out RebuildWireScope __state)
    {__state=new RebuildWireScope {Previous=RebuildWire};RebuildWire=__state;}
    private static Exception EndWireRebuild(Exception __exception,RebuildWireScope __state)
    {if(__state!=null)RebuildWire=__state.Previous;return __exception;}
    private static void CaptureWireFloor()
    {if(RebuildWire!=null)RebuildWire.Floor=Math.Max(1,(int)Field("nextRuntimeSyncId"));}
    private static void PublishHostWireEpoch()
    {
        var net=ModEntry._net;var level=Field("currentLevel") as Level;
        if(net==null || !net.IsHost || level?.map==null)return;
        var descriptor=Coordinator.GetMethod("GetCurrentHostDescriptor",Flags).Invoke(null,null);if(descriptor==null)return;
        var session=(Guid)Property(descriptor,"SessionId");var run=(long)Property(descriptor,"RunId");var sequence=(int)Property(descriptor,"Sequence");
        var epoch=HostEpochs.GetValue(net,_=>new HostEpoch());epoch.Epoch++;
        epoch.Pointer=level.HashlinkPointer;epoch.BuildGeneration=(int)Field("s_levelIdentityGeneration");
        var state=WireStates.GetValue(net,_=>new WireState());var old=state.Host;
        var authority=new WireAuthority {SessionId=session,RunId=run,Sequence=sequence,Epoch=epoch.Epoch,
            RunNonce=old!=null && old.SessionId==session && old.RunId==run && old.Sequence==sequence?old.RunNonce:Guid.NewGuid().ToString("N"),
            Generation=(int)Field("s_levelIdentityToken"),Floor=RebuildWire?.Floor>0?RebuildWire.Floor:Math.Max(1,(int)Field("nextRuntimeSyncId")),
            Level=level.map.id.ToString(),Seed=level.map.seed};
        lock(WireSync(net)){net.ClearMobSyncQueues();state.Host=authority;state.NextNotice=0;state.Peers.Clear();}
        CaptureBootstrapManifest(net,level);
    }
    private static void CloseClientWireForReset()
    {
        var net=ModEntry._net;if(net==null || net.IsHost || !WireStates.TryGetValue(net,out var state))return;
        lock(WireSync(net)){net.ClearMobSyncQueues();state.ClientTicket=null;state.ClientOpen=false;}
    }
    private static string NormalizeMobWireLine(string inner)=>!Disabled("ATTACKALIAS") && inner.StartsWith("MOBATTACK|",StringComparison.OrdinalIgnoreCase)?"MOBATK|"+inner.Substring(10):inner;
    private static string EncodeWire(WireTicket ticket,string inner)=>EpochTag+ticket.RunNonce+","+ticket.Epoch.ToString(CultureInfo.InvariantCulture)+","+
        ticket.Generation.ToString(CultureInfo.InvariantCulture)+","+ticket.Floor.ToString(CultureInfo.InvariantCulture)+","+(ticket.ConnectionNonce??"-")+"|"+NormalizeMobWireLine(inner);
    private static bool DecodeWire(string line,out WireTicket ticket,out string inner)
    {
        ticket=null;inner=null;if(line==null || !line.StartsWith(EpochTag,StringComparison.Ordinal))return false;
        var end=line.IndexOf('|',EpochTag.Length);if(end<0 || end-EpochTag.Length>192)return false;
        var fields=line.Substring(EpochTag.Length,end-EpochTag.Length).Split(',');
        if(fields.Length!=5 || !Guid.TryParseExact(fields[0],"N",out _) || fields[4]!="-" && !Guid.TryParseExact(fields[4],"N",out _) ||
            !long.TryParse(fields[1],NumberStyles.None,CultureInfo.InvariantCulture,out var epoch) || epoch<=0 ||
            !int.TryParse(fields[2],NumberStyles.None,CultureInfo.InvariantCulture,out var generation) || generation<=0 ||
            !int.TryParse(fields[3],NumberStyles.None,CultureInfo.InvariantCulture,out var floor) || floor<=0)return false;
        inner=line.Substring(end+1);if(!MobLine(inner) || inner.StartsWith(EpochTag,StringComparison.Ordinal) || inner.IndexOf('\n')>=0 && inner.IndexOf('\n')!=inner.Length-1 || inner.IndexOf('\r')>=0)return false;
        inner=NormalizeMobWireLine(inner);
        ticket=new WireTicket(fields[0],epoch,generation,floor,fields[4]);return true;
    }
    private static bool StampProducedMobLine(NetNode __instance,ref string line,ref Task __result)
    {
        if(!MobLine(line))return true;
        if(line.StartsWith(EpochTag,StringComparison.Ordinal))return true; // Immutable epoch already captured by the producer/relay.
        lock(WireSync(__instance))
        {
            var state=WireStates.GetValue(__instance,_=>new WireState());
            var ticket=__instance.IsHost?state.Host?.Ticket("-"):state.ClientOpen?state.ClientTicket:null;
            if(ticket!=null){line=EncodeWire(ticket,line);Interlocked.Increment(ref WireSent);return true;}
        }
        __result=Task.CompletedTask;return false;
    }
    private static bool FanoutWireBroadcast(NetNode __instance,string line,ref Task __result)
    {
        if(!line.StartsWith(EpochTag,StringComparison.Ordinal))return true;
        __result=SendWireFanout(__instance,line);return false;
    }
    private static Task SendWireFanout(NetNode net,string line)
    {
        var clients=new List<object>();var steam=(bool)NetField(net,"_useSteamTransport");
        lock(NetField(net,"_clientsLock"))foreach(DictionaryEntry pair in (IDictionary)NetField(net,steam?"_steamClients":"_clients"))clients.Add(pair.Value);
        var send=typeof(NetNode).GetMethod(steam?"SendLineToSteamClientSafe":"SendLineToClientSafe",InstanceFlags);var tasks=new List<Task>();
        foreach(var client in clients)tasks.Add((Task)send.Invoke(net,steam?new object[] {client,line,null}:new object[] {client,line}));
        return Task.WhenAll(tasks);
    }
    private static bool AddressWireToPeer(NetNode __instance,object client,ref string line,ref Task __result)
    {
        if(!MobLine(line))return true;
        // Direct native sends must not acquire a fresh epoch at an async callback.
        if(DecodeWire(line,out var ticket,out var inner) && HostConnections.TryGetValue(client,out var context))
        {
            lock(WireSync(__instance))if(WireStates.TryGetValue(__instance,out var state) && SameWire(state.Host?.Ticket(null),ticket))
            {
                foreach(var peer in state.Peers.Values)if(peer.Open && peer.Ticket.ConnectionNonce==context.Nonce && SameWire(peer.Ticket,ticket))
                {line=EncodeWire(new WireTicket(ticket.RunNonce,ticket.Epoch,ticket.Generation,ticket.Floor,context.Nonce),inner);return true;}
            }
        }
        __result=Task.CompletedTask;return false;
    }
    private static void ClassifyWireInner(ref string line)
    {if(DecodeWire(line,out _,out var inner))line=inner;}
    private static bool KeepWireEventOrdered(NetNode __instance,string line,ref bool __result)
    {
        if(__instance.IsHost && DecodeWire(line,out _,out var inner) && inner.StartsWith("MOBEVENT|",StringComparison.OrdinalIgnoreCase))
        {__result=false;return false;}return true;
    }
    private static bool ValidateWireReceive(NetNode __instance,ref string line,int? senderId,ref string forwardLine,ref bool __result,out WireReceiveScope __state)
    {
        __state=null;if(!MobLine(line))return true;
        __result=true;forwardLine=null;
        if(!DecodeWire(line,out var ticket,out var inner)){Interlocked.Increment(ref WireUnstamped);return false;}
        var sync=WireSync(__instance);Monitor.Enter(sync);__state=new WireReceiveScope {Sync=sync,Ticket=ticket};
        var state=WireStates.GetValue(__instance,_=>new WireState());bool valid;
        if(__instance.IsHost)
            valid=senderId.HasValue && state.Peers.TryGetValue(senderId.Value,out var peer) && peer.Open &&
                SameWire(state.Host?.Ticket(null),ticket) && SameWire(peer.Ticket,ticket) && peer.Ticket.ConnectionNonce==ticket.ConnectionNonce;
        else valid=SameWire(state.ClientTicket,ticket) && state.ClientTicket.ConnectionNonce==ticket.ConnectionNonce;
        if(!valid || !ValidateWirePayload(inner,ticket,senderId,__instance.IsHost))
        {Interlocked.Increment(ref WireRejected);return false;}
        line=inner;Interlocked.Increment(ref WireAccepted);return true;
    }
    private static void PreserveWireRelay(ref string forwardLine,WireReceiveScope __state)
    {if(__state!=null && MobLine(forwardLine))forwardLine=EncodeWire(__state.Ticket,forwardLine);}
    private static Exception ReleaseWireReceive(Exception __exception,WireReceiveScope __state)
    {if(__state?.Sync!=null)Monitor.Exit(__state.Sync);return __exception;}
    private static void RetireWirePeer(NetNode __instance,object __0)
    {
        if(!HostConnections.TryGetValue(__0,out var context) || !WireStates.TryGetValue(__instance,out var state))return;
        lock(WireSync(__instance))
        {var ids=new List<int>();foreach(var pair in state.Peers)if(pair.Value.Ticket.ConnectionNonce==context.Nonce)ids.Add(pair.Key);foreach(var id in ids)state.Peers.Remove(id);}
    }
}
