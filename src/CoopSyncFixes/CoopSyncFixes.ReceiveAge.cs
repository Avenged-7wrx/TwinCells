using System;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using DeadCellsMultiplayerMod;
using HarmonyLib;

public sealed partial class CoopSyncFixes
{
    // Metadata only. Never inspect native Game/Hero/Level on a transport read thread.
    static readonly ConditionalWeakTable<string,ReceivedLineStamp> ReceivedLineStamps=new();
    const BindingFlags ReceiveAgeInstance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static bool ReceiveAgeInitialized;
    static long ReceiveAgeStamped,ReceiveAgeRejected;
    sealed class ReceivedLineStamp
    {
        public NetNode Net;public int? Sender;public object Lifetime,Transport,HostId;
        public bool Host,Steam;public long At;public int Invalid,Consumed;
    }
    static object ReceiveAgeField(NetNode net,string name)=>typeof(NetNode).GetField(name,ReceiveAgeInstance).GetValue(net);
    static object ReceiveAgeLifetime(NetNode net)
    {
        var source=ReceiveAgeField(net,"_cts") as CancellationTokenSource;
        return source!=null && !source.IsCancellationRequested?source:null;
    }
    static void InitializeReceiveAgeBridge(Harmony harmony)
    {
        if(ReceiveAgeInitialized)return;
        var method=typeof(NetNode).GetMethod("TryHandleFastPathLine",ReceiveAgeInstance,null,new[]{typeof(string),typeof(int?)},null)
            ??throw new MissingMethodException("Pinned receive boundary TryHandleFastPathLine");
        harmony.Patch(method,prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(ObserveReceivedLineAge),Flags)));
        ReceiveAgeInitialized=true;
    }
    static bool ReceiveAgeTag(string line)=>line!=null && (line.StartsWith("CSFEL1|",StringComparison.Ordinal) ||
        line.StartsWith("CSFCINEANIM1|",StringComparison.Ordinal) || line.StartsWith("ATK|",StringComparison.OrdinalIgnoreCase));
    static void ObserveReceivedLineAge(NetNode __instance,string line,int? senderId)
    {
        if(!ReceiveAgeInitialized || !ReceiveAgeTag(line))return;
        try{RecordReceivedLineAge(__instance,line,senderId,Environment.TickCount64);}
        catch{Interlocked.Increment(ref ReceiveAgeRejected);} // An observer cannot alter base fast-path behavior.
    }
    static object ReceiveAgeHostConnection(NetNode net,int id,out bool steam)
    {
        steam=false;
        lock(ReceiveAgeField(net,"_clientsLock"))foreach(var name in new[]{"_clients","_steamClients"})
        {
            var values=ReceiveAgeField(net,name) as IDictionary;
            if(values==null || !values.Contains(id))continue;
            var value=values[id];if(value==null || !(bool)value.GetType().GetProperty("HandshakeComplete").GetValue(value))return null;
            steam=name=="_steamClients";return value;
        }
        return null;
    }
    static bool CaptureReceiveTransport(NetNode net,int? sender,out object transport,out object hostId,out bool steam)
    {
        hostId=null;steam=(bool)ReceiveAgeField(net,"_useSteamTransport");
        if(net.IsHost){transport=sender.HasValue?ReceiveAgeHostConnection(net,sender.Value,out steam):null;return transport!=null;}
        transport=ReceiveAgeField(net,steam?"_steamBridge":"_client");
        if(steam)hostId=ReceiveAgeField(net,"_steamHostId");
        return transport!=null;
    }
    static void RecordReceivedLineAge(NetNode net,string line,int? sender,long at)
    {
        if(net==null || line==null || at<=0 || !ReferenceEquals(net,ModEntry._net) || RejectedConnections.TryGetValue(net,out _) || !net.IsAlive)return;
        var lifetime=ReceiveAgeLifetime(net);
        if(lifetime==null || !CaptureReceiveTransport(net,sender,out var transport,out var hostId,out var steam))return;
        var stamp=new ReceivedLineStamp {Net=net,Sender=sender,Lifetime=lifetime,Transport=transport,HostId=hostId,Steam=steam,Host=net.IsHost,At=at};
        var actual=ReceivedLineStamps.GetValue(line,_=>stamp);
        // TCP/Steam allocate a fresh line string per receive. Reusing the same object
        // is ambiguous; never refresh its age or rebind it to a replacement connection.
        if(!ReferenceEquals(stamp,actual)){Interlocked.Exchange(ref actual.Invalid,1);return;}
        Interlocked.Increment(ref ReceiveAgeStamped);
    }
    static bool TakeReceivedLineAge(NetNode net,string line,int? sender,out long receivedAt)
    {
        receivedAt=0;
        if(net==null || line==null || !ReceivedLineStamps.TryGetValue(line,out var stamp) ||
            Volatile.Read(ref stamp.Invalid)!=0 || Interlocked.Exchange(ref stamp.Consumed,1)!=0 ||
            !ReferenceEquals(stamp.Net,net) || stamp.Sender!=sender || !ReferenceEquals(net,ModEntry._net) ||
            RejectedConnections.TryGetValue(net,out _) || !net.IsAlive || stamp.Host!=net.IsHost ||
            !ReferenceEquals(stamp.Lifetime,ReceiveAgeLifetime(net)) ||
            !CaptureReceiveTransport(net,sender,out var transport,out var hostId,out var steam) ||
            stamp.Steam!=steam || !ReferenceEquals(stamp.Transport,transport) || !Equals(stamp.HostId,hostId))
        {Interlocked.Increment(ref ReceiveAgeRejected);return false;}
        receivedAt=stamp.At;return true;
    }
}
