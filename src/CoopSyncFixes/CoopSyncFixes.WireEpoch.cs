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
        // Header text of this ticket, derived from the five values above on first use (CsfWireText.Of).
        // Immutable once published; two threads that race publish equal objects.
        internal CsfWireText Text;
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
        // Host.Ticket("-") of the current Host values, kept between lines (CsfWire.HostStamp). Read and written under WireSync only.
        public WireTicket CsfStamp;
    }
    private static readonly ConditionalWeakTable<NetNode,WireState> WireStates=new();
    private sealed class RebuildWireScope {public RebuildWireScope Previous;public int Floor;}
    [ThreadStatic] private static RebuildWireScope RebuildWire;
    private sealed class WireReceiveScope {public object Sync;public WireTicket Ticket;}
    private static long WireAccepted,WireRejected,WireUnstamped,WireSent;
    private static object WireSync(NetNode net)=>CsfWire.Sync(net);
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
    private static string NormalizeMobWireLine(string inner)
    {
        // Only a MOBATTACK| line can come out different, so only such a line reads the switch (per call, as before).
        // A null line stays on the original expression: it throws or returns null exactly as it did.
        if(inner!=null && !inner.StartsWith("MOBATTACK|",StringComparison.OrdinalIgnoreCase))return inner;
        return !Disabled("ATTACKALIAS") && inner.StartsWith("MOBATTACK|",StringComparison.OrdinalIgnoreCase)?"MOBATK|"+inner.Substring(10):inner;
    }
    // Same text as before (tag, run, epoch, generation, floor, connection or "-", '|', inner line): the part in front of
    // the inner line is kept per ticket, so a line costs one string.
    private static string EncodeWire(WireTicket ticket,string inner)
    {
        var text=CsfWireText.Of(ticket);var body=NormalizeMobWireLine(inner);var line=string.Concat(text.Header,body);
        // The classifier prefixes and the per-peer address decode this very string next, on this thread: tell DecodeWire
        // its answer for this string object. Decoded==null means DecodeWire does not read the header back as written
        // (then nothing is remembered and DecodeWire works the line out itself).
        if(text.Decoded!=null && body!=null)CsfWireLocal.Get().Keep(line,text.Decoded,body,CsfWire.InnerFits(body));
        return line;
    }
    // Accept and reject rules are the original ones; see CsfWire.ReadHeader (header) and CsfWire.InnerFits (inner line).
    // On a reject after the header the out line is the unchanged rest of the line, on a header reject it is null (as before).
    // The ticket is shared between lines with the same header: a WireTicket is never changed and never compared by reference.
    private static bool DecodeWire(string line,out WireTicket ticket,out string inner)
    {
        ticket=null;inner=null;if(line==null || !line.StartsWith(EpochTag,StringComparison.Ordinal))return false;
        var local=CsfWireLocal.Get();
        if(!local.Find(line,out var decoded,out var raw,out var fits))
        {
            var end=line.IndexOf('|',EpochTag.Length);if(end<0 || end-EpochTag.Length>192)return false;
            decoded=local.Header(line,end);if(decoded==null)return false;
            raw=line.Substring(end+1);fits=CsfWire.InnerFits(raw);local.Keep(line,decoded,raw,fits);
        }
        inner=raw;if(!fits)return false;
        inner=NormalizeMobWireLine(raw);ticket=decoded;return true;
    }
    private static bool StampProducedMobLine(NetNode __instance,ref string line,ref Task __result)
    {
        if(!MobLine(line))return true;
        if(line.StartsWith(EpochTag,StringComparison.Ordinal))return true; // Immutable epoch already captured by the producer/relay.
        lock(WireSync(__instance))
        {
            var state=WireStates.GetValue(__instance,_=>new WireState());
            var ticket=__instance.IsHost?CsfWire.HostStamp(state):state.ClientOpen?state.ClientTicket:null;
            if(ticket!=null){line=EncodeWire(ticket,line);Interlocked.Increment(ref WireSent);return true;}
        }
        __result=Task.CompletedTask;return false;
    }
    private static bool FanoutWireBroadcast(NetNode __instance,string line,ref Task __result)
    {
        if(!line.StartsWith(EpochTag,StringComparison.Ordinal))return true;
        __result=SendWireFanout(__instance,line);return false;
    }
    // Same steps in the same order: transport flag, client snapshot under _clientsLock (table order), the send method,
    // one MethodInfo.Invoke per client with the same arguments, Task.WhenAll. The call stays MethodInfo.Invoke on purpose:
    // it reaches the Harmony prefixes of the send method whenever they were installed, and it wraps an exception of the
    // send in TargetInvocationException. (Measured on .NET 10: a System.Reflection.MethodInvoker or an emitted direct call
    // that was first used while the send method was unpatched does NOT reach a prefix installed later.)
    // The buffers are this thread's and are out of their slot while in use.
    private static Task SendWireFanout(NetNode net,string line)
    {
        var local=CsfWireLocal.Get();var scratch=local.TakeFanout();
        try
        {
            var steam=CsfWire.Steam(net);int count;
            lock(CsfWire.ClientsLock(net))
            {
                var values=CsfWire.Clients(net,steam).Values;count=values.Count;
                if(scratch.Clients.Length<count)scratch.Clients=new object[count];
                values.CopyTo(scratch.Clients,0);
            }
            var send=CsfWire.Sender(steam);var arguments=steam?scratch.Triple:scratch.Pair;
            for(var index=0;index<count;index++)
            {
                // Invoke copies the arguments before the call and keeps nothing; the array is filled again for every client.
                arguments[0]=scratch.Clients[index];arguments[1]=line;if(steam)arguments[2]=null;
                scratch.Tasks.Add((Task)send.Invoke(net,arguments));
            }
            return Task.WhenAll(scratch.Tasks);
        }
        finally{scratch.Clear();local.GiveFanout(scratch);}
    }
    private static bool AddressWireToPeer(NetNode __instance,object client,ref string line,ref Task __result)
    {
        if(!MobLine(line))return true;
        // Direct native sends must not acquire a fresh epoch at an async callback.
        if(DecodeWire(line,out var ticket,out var inner) && HostConnections.TryGetValue(client,out var context))
        {
            lock(WireSync(__instance))if(WireStates.TryGetValue(__instance,out var state) && CsfWire.SameHost(state.Host,ticket))
            {
                // The matching peer's own ticket has the run, epoch, generation and floor of the decoded one (SameWire)
                // and this connection's nonce, so its header is the text the per-line ticket used to produce.
                foreach(var peer in state.Peers.Values)if(peer.Open && peer.Ticket.ConnectionNonce==context.Nonce && SameWire(peer.Ticket,ticket))
                {line=EncodeWire(peer.Ticket,inner);return true;}
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
        // The scope object is this thread's reusable one (a fresh one if that is in use). It is taken before the lock, so
        // nothing that can fail sits between Monitor.Enter and __state: the finalizer always sees the entered object.
        var scope=CsfWireLocal.Get().TakeScope();
        var sync=WireSync(__instance);Monitor.Enter(sync);scope.Sync=sync;scope.Ticket=ticket;__state=scope;
        var state=WireStates.GetValue(__instance,_=>new WireState());bool valid;
        if(__instance.IsHost)
            valid=senderId.HasValue && state.Peers.TryGetValue(senderId.Value,out var peer) && peer.Open &&
                CsfWire.SameHost(state.Host,ticket) && SameWire(peer.Ticket,ticket) && peer.Ticket.ConnectionNonce==ticket.ConnectionNonce;
        else valid=SameWire(state.ClientTicket,ticket) && state.ClientTicket.ConnectionNonce==ticket.ConnectionNonce;
        if(!valid || !ValidateWirePayload(inner,ticket,senderId,__instance.IsHost))
        {Interlocked.Increment(ref WireRejected);return false;}
        line=inner;Interlocked.Increment(ref WireAccepted);return true;
    }
    private static void PreserveWireRelay(ref string forwardLine,WireReceiveScope __state)
    {if(__state!=null && MobLine(forwardLine))forwardLine=EncodeWire(__state.Ticket,forwardLine);}
    private static Exception ReleaseWireReceive(Exception __exception,WireReceiveScope __state)
    {
        var sync=__state?.Sync;
        if(sync!=null)
        {
            // Leave the monitor that the prefix entered, then hand the emptied scope back to this thread (prefix, original
            // and finalizer run on one thread).
            __state.Sync=null;__state.Ticket=null;Monitor.Exit(sync);CsfWireLocal.Get().GiveScope(__state);
        }
        return __exception;
    }
    private static void RetireWirePeer(NetNode __instance,object __0)
    {
        if(!HostConnections.TryGetValue(__0,out var context) || !WireStates.TryGetValue(__instance,out var state))return;
        lock(WireSync(__instance))
        {var ids=new List<int>();foreach(var pair in state.Peers)if(pair.Value.Ticket.ConnectionNonce==context.Nonce)ids.Add(pair.Key);foreach(var id in ids)state.Peers.Remove(id);}
    }

    // Helpers of the per-line wire path. No state of their own except looked-up reflection handles of the base mod.
    private static class CsfWire
    {
        // Direct reads of three private NetNode fields. A null node goes through the reflective read, as before.
        [UnsafeAccessor(UnsafeAccessorKind.Field,Name="_sync")] private static extern ref object SyncField(NetNode net);
        [UnsafeAccessor(UnsafeAccessorKind.Field,Name="_clientsLock")] private static extern ref object ClientsLockField(NetNode net);
        [UnsafeAccessor(UnsafeAccessorKind.Field,Name="_useSteamTransport")] private static extern ref bool SteamField(NetNode net);
        internal static object Sync(NetNode net)=>net==null?NetField(net,"_sync"):SyncField(net);
        internal static object ClientsLock(NetNode net)=>net==null?NetField(net,"_clientsLock"):ClientsLockField(net);
        internal static bool Steam(NetNode net)=>net==null?(bool)NetField(net,"_useSteamTransport"):SteamField(net);
        // The client tables have a private element type, so they stay a reflective read; only the field lookup is kept.
        private static FieldInfo TcpClients,SteamClients;
        internal static IDictionary Clients(NetNode net,bool steam)
        {
            var field=steam?(SteamClients??=typeof(NetNode).GetField("_steamClients",InstanceFlags)):
                (TcpClients??=typeof(NetNode).GetField("_clients",InstanceFlags));
            return (IDictionary)field.GetValue(net);
        }
        // The two per-client send methods, looked up once instead of per line.
        private static MethodInfo TcpSend,SteamSend;
        internal static MethodInfo Sender(bool steam)=>steam?
            (SteamSend??=typeof(NetNode).GetMethod("SendLineToSteamClientSafe",InstanceFlags)):
            (TcpSend??=typeof(NetNode).GetMethod("SendLineToClientSafe",InstanceFlags));

        // SameWire(host?.Ticket(null),ticket) without the temporary ticket.
        internal static bool SameHost(WireAuthority host,WireTicket ticket)=>host!=null && ticket!=null && host.RunNonce==ticket.RunNonce &&
            host.Epoch==ticket.Epoch && host.Generation==ticket.Generation && host.Floor==ticket.Floor;
        // state.Host?.Ticket("-"), made again only when one of the four Host values is another one. Caller holds WireSync.
        internal static WireTicket HostStamp(WireState state)
        {
            var host=state.Host;if(host==null)return null;
            var stamp=state.CsfStamp;
            if(stamp==null || !ReferenceEquals(stamp.RunNonce,host.RunNonce) || stamp.Epoch!=host.Epoch || stamp.Generation!=host.Generation || stamp.Floor!=host.Floor)
                state.CsfStamp=stamp=host.Ticket("-");
            return stamp;
        }
        // DecodeWire's test of the inner line, on the text before the attack alias is applied: a MOB line, no nested
        // epoch tag, a line feed only as the last character, no carriage return.
        internal static bool InnerFits(string inner)
        {
            if(!MobLine(inner) || inner.StartsWith(EpochTag,StringComparison.Ordinal))return false;
            var feed=inner.IndexOf('\n');
            return (feed<0 || feed==inner.Length-1) && inner.IndexOf('\r')<0;
        }
        // DecodeWire's header rules for line[EpochTag.Length..end) (end = first '|' after the tag, at most 192 chars
        // behind it): exactly five comma fields; run and connection in Guid "N" form as Guid.TryParseExact reads it
        // (connection may be "-"); epoch, generation and floor as long/int.TryParse with NumberStyles.None reads them,
        // each above zero. Returns the ticket DecodeWire builds from the five fields, or null for a reject.
        internal static WireTicket ReadHeader(string line,int end)
        {
            var rest=line.AsSpan(EpochTag.Length,end-EpochTag.Length);
            var cut=rest.IndexOf(',');if(cut<0)return null;var run=rest.Slice(0,cut);rest=rest.Slice(cut+1);
            cut=rest.IndexOf(',');if(cut<0)return null;var epochText=rest.Slice(0,cut);rest=rest.Slice(cut+1);
            cut=rest.IndexOf(',');if(cut<0)return null;var generationText=rest.Slice(0,cut);rest=rest.Slice(cut+1);
            cut=rest.IndexOf(',');if(cut<0)return null;var floorText=rest.Slice(0,cut);var connection=rest.Slice(cut+1);
            if(connection.IndexOf(',')>=0)return null;
            var open=connection.Length==1 && connection[0]=='-';
            if(!Guid.TryParseExact(run,"N",out _) || !open && !Guid.TryParseExact(connection,"N",out _) ||
                !long.TryParse(epochText,NumberStyles.None,CultureInfo.InvariantCulture,out var epoch) || epoch<=0 ||
                !int.TryParse(generationText,NumberStyles.None,CultureInfo.InvariantCulture,out var generation) || generation<=0 ||
                !int.TryParse(floorText,NumberStyles.None,CultureInfo.InvariantCulture,out var floor) || floor<=0)return null;
            return new WireTicket(run.ToString(),epoch,generation,floor,open?"-":connection.ToString());
        }
    }
    // The text EncodeWire puts in front of the inner line for one ticket, and what DecodeWire reads back from that text.
    private sealed class CsfWireText
    {
        public readonly string Header;
        // DecodeWire's ticket for any line that starts with Header, when DecodeWire reads Header as one whole valid
        // header (its first '|' after the tag is its last character, at most 192 characters between, fields valid).
        // Otherwise null: for example a run or connection text that is not a Guid, or that holds '|' or ','.
        public readonly WireTicket Decoded;
        private CsfWireText(string header,WireTicket decoded){Header=header;Decoded=decoded;}
        public static CsfWireText Of(WireTicket ticket)
        {
            var text=Volatile.Read(ref ticket.Text);if(text!=null)return text;
            var header=EpochTag+ticket.RunNonce+","+ticket.Epoch.ToString(CultureInfo.InvariantCulture)+","+
                ticket.Generation.ToString(CultureInfo.InvariantCulture)+","+ticket.Floor.ToString(CultureInfo.InvariantCulture)+","+(ticket.ConnectionNonce??"-")+"|";
            var end=header.IndexOf('|',EpochTag.Length);
            text=new CsfWireText(header,end==header.Length-1 && end-EpochTag.Length<=192?CsfWire.ReadHeader(header,end):null);
            Volatile.Write(ref ticket.Text,text);return text;
        }
    }
    // Scratch of the wire path, one instance per thread; only the owner thread reads or writes it.
    private sealed class CsfWireLocal
    {
        [ThreadStatic] private static CsfWireLocal Slot;
        public static CsfWireLocal Get(){var local=Slot;if(local==null)Slot=local=new CsfWireLocal();return local;}

        // What DecodeWire returns for the two stamped lines this thread handled last, by string OBJECT (a string never
        // changes, so the same object always decodes the same): the ticket, the inner line before the attack alias, and
        // the verdict of the inner-line test. Entries come from DecodeWire itself and from EncodeWire for the line it
        // just built. The alias is applied on every use, so the ATTACKALIAS switch is read as often as before.
        private string LineA,RawA,LineB,RawB;private WireTicket TicketA,TicketB;private bool FitsA,FitsB,ReplaceB;
        public bool Find(string line,out WireTicket ticket,out string raw,out bool fits)
        {
            if(line!=null && ReferenceEquals(line,LineA)){ticket=TicketA;raw=RawA;fits=FitsA;ReplaceB=true;return true;}
            if(line!=null && ReferenceEquals(line,LineB)){ticket=TicketB;raw=RawB;fits=FitsB;ReplaceB=false;return true;}
            ticket=null;raw=null;fits=false;return false;
        }
        // Replaces the entry that was used longer ago (the broadcast line stays while its per-peer copies come and go).
        // A line of more than MemoLimit chars is not remembered: an entry stays reachable for as long as this thread
        // lives, and the transport accepts lines of more than a million chars. DecodeWire then works such a line out
        // from its text on every call, as it always did (an entry is only a shortcut to that same answer).
        private const int MemoLimit=16384;
        public void Keep(string line,WireTicket ticket,string raw,bool fits)
        {
            if(line.Length>MemoLimit)return;
            if(ReplaceB){LineB=line;TicketB=ticket;RawB=raw;FitsB=fits;ReplaceB=false;}
            else{LineA=line;TicketA=ticket;RawA=raw;FitsA=fits;ReplaceB=true;}
        }
        // The last header this thread decoded successfully, as text (tag to '|') and as ticket: lines of one epoch and
        // one connection all start with the same text.
        private string Head;private WireTicket HeadTicket;
        public WireTicket Header(string line,int end)
        {
            var head=Head;
            if(head!=null && head.Length==end+1 && string.CompareOrdinal(line,0,head,0,end+1)==0)return HeadTicket;
            var ticket=CsfWire.ReadHeader(line,end);
            if(ticket!=null){Head=line.Substring(0,end+1);HeadTicket=ticket;}
            return ticket;
        }
        // Reusable objects. Each is taken out of its slot while in use, so a nested use on the same thread gets a fresh one.
        private WireReceiveScope Scope;private CsfWireFanout Fanout;private List<NetNode.MobStateSnapshot> States;
        public WireReceiveScope TakeScope(){var scope=Scope;Scope=null;return scope??new WireReceiveScope();}
        public void GiveScope(WireReceiveScope scope){Scope=scope;}
        public CsfWireFanout TakeFanout(){var fanout=Fanout;Fanout=null;return fanout??new CsfWireFanout();}
        public void GiveFanout(CsfWireFanout fanout){Fanout=fanout;}
        public List<NetNode.MobStateSnapshot> TakeStates(){var states=States;States=null;return states??new List<NetNode.MobStateSnapshot>();}
        // A list that one very large line made big is dropped instead of kept for the life of the thread.
        public void GiveStates(List<NetNode.MobStateSnapshot> states){states.Clear();if(states.Capacity<=256)States=states;}
    }
    // Buffers of one SendWireFanout call: the client snapshot, the per-client tasks and the argument arrays of the send.
    private sealed class CsfWireFanout
    {
        public object[] Clients=new object[4];public readonly List<Task> Tasks=new(4);public readonly object[] Pair=new object[2],Triple=new object[3];
        public void Clear(){Array.Clear(Clients,0,Clients.Length);Tasks.Clear();Array.Clear(Pair,0,Pair.Length);Array.Clear(Triple,0,Triple.Length);}
    }
}
