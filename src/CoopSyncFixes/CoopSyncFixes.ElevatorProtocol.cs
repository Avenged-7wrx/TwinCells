using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using Newtonsoft.Json;

public sealed partial class CoopSyncFixes
{
    // Enabled by the integrated entry point before either peer connects.
    const string ElevatorCapability="+csfelev1", ElevatorTag="CSFEL1|";
    const BindingFlags ElevatorInstanceFlags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static readonly ConditionalWeakTable<NetNode,ElevatorSession> ElevatorSessions=new();
    sealed class ElevatorMessage
    {
        public int V=1,Sender,Cx,Cy,Left,Right,Top,Bottom,Width,MoveDir;
        public long Epoch,Sequence,Request,BuildSerial;
        public string Kind,Token,Floor,Boot,Track,ConnectionNonce;
        [JsonIgnore] public object ReceivedConnection;
        [JsonIgnore] public long ReceivedAt;
        public double Xr,Yr,Ty,Dy,Bdy,Speed;
    }
    sealed class ElevatorPeer
    {
        public object Connection;public bool Steam;public string Token,Floor;
        public long LastRequest,BuildSerial,LastSeen;
        public object PositionRecord;public long PositionSequence,PositionSeenAt;
    }
    sealed class ElevatorSession
    {
        public readonly ConcurrentQueue<ElevatorMessage> Incoming=new();public int Queued;
        public readonly Dictionary<int,ElevatorPeer> Peers=new();
        public readonly Dictionary<string,ElevatorMessage> Latest=new();
        public readonly Dictionary<string,long> AcceptedSequences=new();
        public IntPtr Pointer;public int Build;public string Floor,Token,Boot=Guid.NewGuid().ToString("N"),RemoteBoot;
        public long Epoch,RemoteEpoch,Sequence,Request,LastSend,LastSubscribe,LocalBuildSerial;
        public bool WelcomeAccepted;
    }
    static void InitializeElevatorProtocol(Harmony harmony)
    {
        InitializeReceiveAgeBridge(harmony);
        // The elevator and cinematic tokens must be removed before the integrated
        // base validator inspects its own suffix.
        foreach(var name in new[]{"BuildHelloLine","BuildWelcomeLine"})
            harmony.Patch(typeof(NetNode).GetMethod(name,Flags),postfix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(AdvertiseElevators),Flags)){priority=HarmonyLib.Priority.Last});
        harmony.Patch(typeof(NetNode).GetMethod("TryValidateHandshakeLine",Flags),
            prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(RequireElevators),Flags)){priority=HarmonyLib.Priority.First});
        harmony.Patch(typeof(NetNode).GetMethod("HandleLine",ElevatorInstanceFlags),
            prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(ReadElevatorWire),Flags)){priority=HarmonyLib.Priority.First});
        harmony.Patch(typeof(NetNode).GetMethod("RejectIncompatiblePeer",ElevatorInstanceFlags),
            postfix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(ObserveElevatorHandshake),Flags)));
    }
    static void ObserveElevatorHandshake(NetNode __instance,string expectedTag,bool __result)
    {
        if(ElevatorModuleInitialized && !__instance.IsHost && expectedTag=="WELCOME" && !__result)
            ElevatorSessions.GetValue(__instance,_=>new ElevatorSession()).WelcomeAccepted=true;
    }
    static void AdvertiseElevators(ref string __result)
    {if(ElevatorModuleInitialized)__result=__result.TrimEnd('\r','\n')+ElevatorCapability+"\n";}
    static bool RequireElevators(ref string line,out int remoteProtocol,out string remoteBuild,out string failure,ref bool __result)
    {
        remoteProtocol=0;remoteBuild="unknown";failure="";
        if(!ElevatorModuleInitialized || !TryStripElevatorCapability(ref line))
        {__result=false;failure="Both peers require the explicit experimental elevator protocol";return false;}
        return true;
    }
    static bool TryStripElevatorCapability(ref string line)
    {
        var clean=(line??"").TrimEnd('\r','\n');
        var parts=clean.Split('|');int at=parts.Length==3?parts[2].IndexOf(ElevatorCapability,StringComparison.Ordinal):-1;
        // Each optional experiment removes only its own complete token. This commutes
        // with +csfcine1 regardless of which Priority.First validator runs first.
        if(at<0 ||
            parts[2].IndexOf(ElevatorCapability,at+ElevatorCapability.Length,StringComparison.Ordinal)>=0 ||
            at+ElevatorCapability.Length<parts[2].Length && parts[2][at+ElevatorCapability.Length]!='+')
            return false;
        parts[2]=parts[2].Remove(at,ElevatorCapability.Length);line=string.Join("|",parts);return true;
    }
    static bool ReadElevatorWire(NetNode __instance,string line,int? senderId,ref string forwardLine,ref bool __result)
    {
        if(line==null || !line.StartsWith(ElevatorTag,StringComparison.Ordinal))return true;
        forwardLine=null;__result=true; // Never forward an extension message through the base router.
        try
        {
            if(!CurrentElevatorNode(__instance) || line.Length>8192)return false;
            if(!TakeReceivedLineAge(__instance,line,senderId,out var receivedAt))return false;
            // ID is the causal proof that the strict handshake finished. An extension
            // packet before ID is consumed without parsing or touching game objects.
            if(!__instance.IsHost && (!ElevatorSessions.TryGetValue(__instance,out var negotiated) || !negotiated.WelcomeAccepted ||
                !ClientConnections.TryGetValue(__instance,out _) || __instance.id<=1))return false;
            if(__instance.IsHost && (!senderId.HasValue || senderId.Value<=1))return false;
            var value=JsonConvert.DeserializeObject<ElevatorMessage>(Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(ElevatorTag.Length).TrimEnd('\r','\n'))));
            if(value==null || value.V!=1 || !Guid.TryParseExact(value.Token,"N",out _) ||
                value.BuildSerial<=0 || string.IsNullOrEmpty(value.Floor) || value.Floor.Length>1024 ||
                (__instance.IsHost ? value.Kind!="SUB" && value.Kind!="ACT" : value.Kind!="ACK" && value.Kind!="STATE"))return false;
            value.Sender=__instance.IsHost?senderId.Value:1;
            if(__instance.IsHost)
            {
                value.ReceivedConnection=ElevatorConnection(__instance,value.Sender,out _);
                if(value.ReceivedConnection==null || !HostConnections.TryGetValue(value.ReceivedConnection,out var context) || context.Nonce!=value.ConnectionNonce)return false;
            }
            else if(!ClientConnections.TryGetValue(__instance,out var context) || context.Nonce!=value.ConnectionNonce)return false;
            value.ReceivedAt=receivedAt; // Local monotonic time; never trust or serialize a peer timestamp.
            if(!FreshElevatorMessage(value,Environment.TickCount64))return false;
            var session=ElevatorSessions.GetValue(__instance,_=>new ElevatorSession());
            if(Interlocked.Increment(ref session.Queued)>256){Interlocked.Decrement(ref session.Queued);return false;}
            try{session.Incoming.Enqueue(value);}catch{Interlocked.Decrement(ref session.Queued);throw;}
        }
        catch { /* Malformed extension data cannot disconnect the underlying game session. */ }
        return false;
    }
    static string ElevatorEncode(ElevatorMessage message) => ElevatorTag+Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message,Formatting.None)))+"\n";
    static bool CurrentElevatorNode(NetNode net) => ElevatorModuleInitialized && net!=null &&
        ReferenceEquals(net,ModEntry._net) && !RejectedConnections.TryGetValue(net,out _) && net.IsAlive;
    static bool FreshElevatorMessage(ElevatorMessage message,long now) => message.ReceivedAt>0 &&
        now>=message.ReceivedAt && now-message.ReceivedAt<=(message.Kind=="SUB"?3000:1500);
    static object ElevatorConnection(NetNode net,int id,out bool steam)
    {
        steam=false;var gate=typeof(NetNode).GetField("_clientsLock",ElevatorInstanceFlags).GetValue(net);
        lock(gate)
        {
            foreach(var field in new[]{"_clients","_steamClients"})
            {
                var values=(IDictionary)typeof(NetNode).GetField(field,ElevatorInstanceFlags).GetValue(net);
                if(values==null || !values.Contains(id))continue;
                var connection=values[id];
                if(!(bool)connection.GetType().GetProperty("HandshakeComplete").GetValue(connection))continue;
                steam=field=="_steamClients";return connection;
            }
        }
        return null;
    }
    static void ElevatorSendPeer(NetNode net,int id,ElevatorPeer peer,ElevatorMessage message)
    {
        if(!CurrentElevatorNode(net))return;
        var connection=ElevatorConnection(net,id,out var steam);
        if(connection==null || !ReferenceEquals(connection,peer.Connection) || steam!=peer.Steam)return;
        if(!HostConnections.TryGetValue(connection,out var context))return;
        message.ConnectionNonce=context.Nonce;message.BuildSerial=peer.BuildSerial;
        var method=typeof(NetNode).GetMethod(steam?"SendLineToSteamClientSafe":"SendLineToClientSafe",ElevatorInstanceFlags);
        method.Invoke(net,steam?new object[]{connection,ElevatorEncode(message),null}:new object[]{connection,ElevatorEncode(message)});
    }
    static void ElevatorSendHost(NetNode net,ElevatorMessage message)
    {
        if(!CurrentElevatorNode(net) || !ClientConnections.TryGetValue(net,out var context) || !ElevatorSessions.TryGetValue(net,out var session) || !session.WelcomeAccepted)return;
        message.ConnectionNonce=context.Nonce;
        message.BuildSerial=session.LocalBuildSerial;
        typeof(NetNode).GetMethod("SendLineSafe",ElevatorInstanceFlags).Invoke(net,new object[]{ElevatorEncode(message)});
    }
}
