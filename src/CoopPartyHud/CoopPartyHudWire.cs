using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DeadCellsMultiplayerMod;
using R=CoopPartyHudRules;

// Wire of CoopPartyHud: the CPHUD1| lines (CoopPartyHudRules header). Pattern of CoopTransitionGateWire (U10) and
// CoopLobby LobbyColor.cs.
// Receiving: a Priority.First prefix on NetNode.HandleLine (IL:9853) consumes every CPHUD1| line (__result=true,
// forwardLine=null, return false): the base never parses them and its host router never forwards them. Lines are only
// parsed and queued here (HandleLine runs in MainThreadPump jobs); the frame tick applies them.
// Host side: the transport sender id must equal the payload sender and be a completed connection.
// A peer WITHOUT this part ignores CPHUD1 lines silently: HandleLine checks no tag that is a prefix of "CPHUD1|" and falls
// through to TryParsePositionLine (IL_2BAA), whose Int32.TryParse of field 0 ("CPHUD1", IL_0049) fails, so HandleLine
// returns true at IL_2D67 with forwardLine unset (nothing logged, nothing forwarded).
// Sending: the client uses NetNode.SendLineSafe (to the host); the host sends to every completed connection only
// (SendLineToClientSafe / SendLineToSteamClientSafe), never a broadcast to incomplete peers. Two players: the host does
// not relay one client's line to another client (with more clients each client sees the host's stats only).
public static class CoopPartyHudWire
{
    const BindingFlags S=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
    const BindingFlags I=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    static MethodInfo SendLine,SendTcp,SendSteam;
    static PropertyInfo TcpComplete,SteamComplete;
    static FieldInfo ClientsLock,Clients,SteamClients;
    static ConditionalWeakTable<NetNode,object> CsfRejected;static bool CsfLooked;
    static readonly ConcurrentQueue<R.Message> Inbox=new();static int InboxCount;
    static long Received,Dropped,Sent,SendFaults;
    public static long ReceivedCount=>Interlocked.Read(ref Received);
    public static long DroppedCount=>Interlocked.Read(ref Dropped);
    public static long SentCount=>Interlocked.Read(ref Sent);
    public static long SendFaultCount=>Interlocked.Read(ref SendFaults);

    public static MethodInfo Bind()
    {
        var net=typeof(NetNode);
        var handle=CoopPartyHudPins.Base(net,"HandleLine",false,new[]{typeof(string),typeof(int?),typeof(string).MakeByRefType()},typeof(bool),CoopPartyHudPins.HandleLineIl,CoopPartyHudPins.HandleLineSha);
        var tcp=net.GetNestedType("ClientConnection",BindingFlags.Public|BindingFlags.NonPublic)??throw new InvalidOperationException("Pinned type missing: NetNode.ClientConnection");
        var steam=net.GetNestedType("SteamClientConnection",BindingFlags.Public|BindingFlags.NonPublic)??throw new InvalidOperationException("Pinned type missing: NetNode.SteamClientConnection");
        SendLine=CoopPartyHudPins.Base(net,"SendLineSafe",false,new[]{typeof(string)},typeof(Task),CoopPartyHudPins.SendLineIl,CoopPartyHudPins.SendLineSha);
        SendTcp=CoopPartyHudPins.Base(net,"SendLineToClientSafe",false,new[]{tcp,typeof(string)},typeof(Task),CoopPartyHudPins.SendClientIl,CoopPartyHudPins.SendClientSha);
        // The third parameter is Nullable<Steamworks.EP2PSend>, matched by shape (CoopLobby LobbyColor.cs:98-104); null = reliable.
        var steamParams=net.GetMethods(I).Where(m=>m.Name=="SendLineToSteamClientSafe"&&m.DeclaringType==net).Select(m=>m.GetParameters()).SingleOrDefault();
        if(steamParams==null||steamParams.Length!=3||steamParams[0].ParameterType!=steam||steamParams[1].ParameterType!=typeof(string)||!steamParams[2].ParameterType.IsGenericType||
           steamParams[2].ParameterType.GetGenericTypeDefinition()!=typeof(Nullable<>)||steamParams[2].ParameterType.GetGenericArguments()[0].FullName!="Steamworks.EP2PSend")
            throw new InvalidOperationException("Pinned method mismatch: NetNode.SendLineToSteamClientSafe");
        SendSteam=CoopPartyHudPins.Base(net,"SendLineToSteamClientSafe",false,new[]{steam,typeof(string),steamParams[2].ParameterType},typeof(Task),CoopPartyHudPins.SendSteamIl,CoopPartyHudPins.SendSteamSha);
        ClientsLock=net.GetField("_clientsLock",I|BindingFlags.DeclaredOnly);
        Clients=net.GetField("_clients",I|BindingFlags.DeclaredOnly);
        SteamClients=net.GetField("_steamClients",I|BindingFlags.DeclaredOnly);
        if(ClientsLock?.FieldType!=typeof(object)||Clients?.FieldType!=typeof(Dictionary<,>).MakeGenericType(typeof(int),tcp)||SteamClients?.FieldType!=typeof(Dictionary<,>).MakeGenericType(typeof(int),steam))
            throw new InvalidOperationException("Pinned connection tables changed");
        TcpComplete=tcp.GetProperty("HandshakeComplete",I);SteamComplete=steam.GetProperty("HandshakeComplete",I);
        if(TcpComplete?.PropertyType!=typeof(bool)||SteamComplete?.PropertyType!=typeof(bool))throw new InvalidOperationException("Pinned property mismatch: HandshakeComplete");
        return handle;
    }

    static bool Rejected(NetNode net)
    {
        if(!CsfLooked)
        {
            CsfLooked=true;
            foreach(var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type csf;try{csf=asm.GetType("CoopSyncFixes",false);}catch{continue;}
                if(csf==null)continue;
                var f=csf.GetField("RejectedConnections",S);
                if(f!=null&&f.FieldType==typeof(ConditionalWeakTable<NetNode,object>))CsfRejected=(ConditionalWeakTable<NetNode,object>)f.GetValue(null);
                break;
            }
        }
        return CsfRejected!=null&&net!=null&&CsfRejected.TryGetValue(net,out _);
    }
    // HarmonyX runs every prefix even after one returned false (CoopLobby notes); an earlier consuming prefix's verdict stands.
    static bool BeforeHandleLine(NetNode __instance,string line,int? senderId,ref string forwardLine,ref bool __result,bool __runOriginal)
    {
        if(line==null||!line.StartsWith(R.Tag,StringComparison.Ordinal))return true;
        if(!__runOriginal)return false;
        __result=true;forwardLine=null;
        try
        {
            if(Rejected(__instance)){__result=false;return false;}
            if(!CoopPartyHudPatch.Running||__instance==null||!ReferenceEquals(__instance,ModEntry._net)||!__instance.IsAlive)return false;
            if(!R.TryParse(line,out var m)){Interlocked.Increment(ref Dropped);return false;}
            bool host=__instance.IsHost;
            bool completed=host&&senderId.HasValue&&senderId.Value==m.Sender&&Connection(__instance,m.Sender,out _)!=null;   // looked up only where it decides
            if(!R.AcceptLine(host,__instance.id,senderId,m.Sender,completed)){Interlocked.Increment(ref Dropped);return false;}
            if(Interlocked.Increment(ref InboxCount)>R.InboxCap){Interlocked.Decrement(ref InboxCount);Interlocked.Increment(ref Dropped);return false;}
            Inbox.Enqueue(m);Interlocked.Increment(ref Received);
        }
        catch(Exception error){CoopPartyHudPatch.Fault("stats line",error);}
        return false;
    }
    internal static bool Dequeue(out R.Message m)
    {
        if(!Inbox.TryDequeue(out m))return false;
        Interlocked.Decrement(ref InboxCount);return true;
    }
    internal static void Clear(){while(Dequeue(out _)){}}

    // The completed connection object for an id (TCP or Steam), under the mod's own _clientsLock.
    static object Connection(NetNode net,int id,out bool steam)
    {
        steam=false;
        lock(ClientsLock.GetValue(net))
        {
            foreach(var (field,isSteam) in new[]{(Clients,false),(SteamClients,true)})
            {
                if(!(field.GetValue(net) is IDictionary peers)||!peers.Contains(id))continue;
                var value=peers[id];
                if(value==null||!(bool)(isSteam?SteamComplete:TcpComplete).GetValue(value))continue;
                steam=isSteam;return value;
            }
        }
        return null;
    }
    // Every completed connection (host side).
    static List<(object Connection,bool Steam)> Completed(NetNode net)
    {
        var list=new List<(object,bool)>(2);
        lock(ClientsLock.GetValue(net))
        {
            foreach(var (field,isSteam) in new[]{(Clients,false),(SteamClients,true)})
            {
                if(!(field.GetValue(net) is IDictionary peers))continue;
                foreach(DictionaryEntry e in peers)
                    if(e.Value!=null&&(bool)(isSteam?SteamComplete:TcpComplete).GetValue(e.Value))list.Add((e.Value,isSteam));
            }
        }
        return list;
    }
    // Returns the number of connections the line was handed to. Send faults are counted, not fatal for the HUD.
    internal static int Send(NetNode net,string line)
    {
        try
        {
            if(!net.IsHost){SendLine.Invoke(net,new object[]{line});Interlocked.Increment(ref Sent);return 1;}
            int n=0;
            foreach(var (connection,steam) in Completed(net))
            {
                if(steam)SendSteam.Invoke(net,new object[]{connection,line,null});
                else SendTcp.Invoke(net,new object[]{connection,line});
                n++;
            }
            if(n>0)Interlocked.Increment(ref Sent);
            return n;
        }
        catch(Exception error){Interlocked.Increment(ref SendFaults);CoopPartyHudPatch.SendFault(error);return 0;}
    }
}
