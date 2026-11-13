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
using R=CoopTransitionGateRules;

// Wire of CoopTransitionGate: the CPTG1| lines (CoopTransitionGateRules header).
// Receiving: a Priority.First prefix on NetNode.HandleLine (IL:9853) consumes every CPTG1| line (__result=true,
// forwardLine=null, return false, as CoopLobby's CPCOL1 and CSF's CSFCINEANIM1): the base never parses them and its host
// router never forwards them. A host without this part ignores the unknown tag silently. Lines are only parsed and queued
// here; the frame tick applies them. CSF's terminal rejection of a connection (CoopSyncFixes.RejectedConnections) stands.
// Host side: the transport sender id must equal the payload sender and be a completed connection.
// Sending: the client uses NetNode.SendLineSafe (to the host); the host sends to the partner's completed connection only
// (SendLineToClientSafe / SendLineToSteamClientSafe, as CoopLobby and CSF do), never a broadcast to incomplete peers.
public static class CoopTransitionGateWire
{
    const BindingFlags S=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
    const BindingFlags I=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    static MethodInfo SendLine,SendTcp,SendSteam;
    static PropertyInfo TcpComplete,SteamComplete;
    static FieldInfo ClientsLock,Clients,SteamClients;
    static ConditionalWeakTable<NetNode,object> CsfRejected;static bool CsfLooked;
    internal static readonly ConcurrentQueue<R.Message> Inbox=new();static int InboxCount;
    static long Received,Dropped,Sent,SendFaults;
    public static long ReceivedCount=>Interlocked.Read(ref Received);
    public static long DroppedCount=>Interlocked.Read(ref Dropped);
    public static long SentCount=>Interlocked.Read(ref Sent);

    public static MethodInfo Bind()
    {
        var net=typeof(NetNode);
        var handle=CoopTransitionGatePins.Base(net,"HandleLine",false,new[]{typeof(string),typeof(int?),typeof(string).MakeByRefType()},typeof(bool),CoopTransitionGatePins.HandleLineIl,CoopTransitionGatePins.HandleLineSha);
        var tcp=net.GetNestedType("ClientConnection",BindingFlags.Public|BindingFlags.NonPublic)??throw new InvalidOperationException("Pinned type missing: NetNode.ClientConnection");
        var steam=net.GetNestedType("SteamClientConnection",BindingFlags.Public|BindingFlags.NonPublic)??throw new InvalidOperationException("Pinned type missing: NetNode.SteamClientConnection");
        SendLine=CoopTransitionGatePins.Base(net,"SendLineSafe",false,new[]{typeof(string)},typeof(Task),CoopTransitionGatePins.SendLineIl,CoopTransitionGatePins.SendLineSha);
        SendTcp=CoopTransitionGatePins.Base(net,"SendLineToClientSafe",false,new[]{tcp,typeof(string)},typeof(Task),CoopTransitionGatePins.SendClientIl,CoopTransitionGatePins.SendClientSha);
        // The third parameter is Nullable<Steamworks.EP2PSend>, matched by shape (CoopLobby LobbyColor.cs:98-104); null = reliable.
        var steamParams=net.GetMethods(I).Where(m=>m.Name=="SendLineToSteamClientSafe"&&m.DeclaringType==net).Select(m=>m.GetParameters()).SingleOrDefault();
        if(steamParams==null||steamParams.Length!=3||steamParams[0].ParameterType!=steam||steamParams[1].ParameterType!=typeof(string)||!steamParams[2].ParameterType.IsGenericType||
           steamParams[2].ParameterType.GetGenericTypeDefinition()!=typeof(Nullable<>)||steamParams[2].ParameterType.GetGenericArguments()[0].FullName!="Steamworks.EP2PSend")
            throw new InvalidOperationException("Pinned method mismatch: NetNode.SendLineToSteamClientSafe");
        SendSteam=CoopTransitionGatePins.Base(net,"SendLineToSteamClientSafe",false,new[]{steam,typeof(string),steamParams[2].ParameterType},typeof(Task),CoopTransitionGatePins.SendSteamIl,CoopTransitionGatePins.SendSteamSha);
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
            {
                // CoopPack: the CSF part of this assembly while it is present; a switched-off or failed CSF reads as absent, as a missing module does standalone.
                Type csf=null;try{csf=global::CoopPackRuntime.PackParts.FindType("CoopSyncFixes","CoopSyncFixes");}catch{}
                if(csf!=null)
                {
                    var f=csf.GetField("RejectedConnections",S);
                    if(f!=null&&f.FieldType==typeof(ConditionalWeakTable<NetNode,object>))CsfRejected=(ConditionalWeakTable<NetNode,object>)f.GetValue(null);
                }
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
            if(!CoopTransitionGatePatch.Running||__instance==null||!ReferenceEquals(__instance,ModEntry._net)||!__instance.IsAlive)return false;
            if(!R.TryParse(line,out var m)){Interlocked.Increment(ref Dropped);return false;}
            if(__instance.IsHost)
            {
                if(!senderId.HasValue||senderId.Value!=m.Sender||senderId.Value==__instance.id||Connection(__instance,m.Sender,out _)==null){Interlocked.Increment(ref Dropped);return false;}
            }
            else if(__instance.id<=0||m.Sender==__instance.id){Interlocked.Increment(ref Dropped);return false;}
            if(Interlocked.Increment(ref InboxCount)>R.InboxCap){Interlocked.Decrement(ref InboxCount);Interlocked.Increment(ref Dropped);return false;}
            Inbox.Enqueue(m);Interlocked.Increment(ref Received);
        }
        catch(Exception error){CoopTransitionGatePatch.Fault("Gate line",error);}
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
    internal static void Send(NetNode net,int partner,string line)
    {
        try
        {
            if(!net.IsHost){SendLine.Invoke(net,new object[]{line});Interlocked.Increment(ref Sent);return;}
            var connection=Connection(net,partner,out bool steam);
            if(connection==null)return;
            if(steam)SendSteam.Invoke(net,new object[]{connection,line,null});
            else SendTcp.Invoke(net,new object[]{connection,line});
            Interlocked.Increment(ref Sent);
        }
        catch(Exception error){Interlocked.Increment(ref SendFaults);CoopTransitionGatePatch.Fault("Gate send",error);}
    }
    public static long SendFaultCount=>Interlocked.Read(ref SendFaults);
}
