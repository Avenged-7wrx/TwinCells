using System;
using System.Collections;
using System.Collections.Generic;
using DeadCellsMultiplayerMod;
using Newtonsoft.Json;
using Serilog;

public sealed partial class CoopSyncFixes
{
    private static bool FillWireWorldHeader(NetNode net,WorldHeader header)
    {
        if(!WireEnabled)return true;
        lock(WireSync(net))
        {
            if(!WireStates.TryGetValue(net,out var state) || state.Host==null || state.Host.Epoch!=header.Epoch ||
                state.Host.Generation!=header.Generation)return false;
            header.HostRunNonce=state.Host.RunNonce;header.Floor=state.Host.Floor;return true;
        }
    }
    private static bool ValidWireHeader(NetNode net,WorldHeader header)
    {
        if(!WireEnabled)return true;
        if(!Guid.TryParseExact(header.HostRunNonce,"N",out _) || header.Floor<=0)return false;
        if(!WireStates.TryGetValue(net,out var known))return true;
        lock(WireSync(net))
        {
            var expected=known.Expected;
            if(expected==null || expected.SessionId!=header.SessionId || expected.RunId!=header.RunId || expected.Sequence!=header.Sequence)return true;
            if(header.HostRunNonce!=expected.RunNonce || header.Epoch<expected.Epoch)return false;
            return header.Epoch>expected.Epoch || header.Generation==expected.Generation && header.Floor==expected.Floor && header.Level==expected.Level &&
                BitConverter.DoubleToInt64Bits(header.Seed)==BitConverter.DoubleToInt64Bits(expected.Seed);
        }
    }
    private static void AdoptWireManifest(NetNode net,WorldJoin join)
    {
        if(!WireEnabled)return;
        var header=join.Header;
        if(!ValidWireHeader(net,header) || !LocalActivationCurrent(join))throw new InvalidOperationException("Stale wire activation snapshot");
        var context=ClientConnections.GetValue(net,_=>throw new InvalidOperationException("Missing wire context"));
        var ticket=new WireTicket(header.HostRunNonce,header.Epoch,header.Generation,header.Floor,context.Nonce);
        lock(WireSync(net))
        {
            var state=WireStates.GetValue(net,_=>new WireState());net.ClearMobSyncQueues();
            state.ClientTicket=ticket;state.ClientOpen=false;
            state.Expected=new WireAuthority {RunNonce=header.HostRunNonce,SessionId=header.SessionId,RunId=header.RunId,Sequence=header.Sequence,
                Epoch=header.Epoch,Generation=header.Generation,Floor=header.Floor,Level=header.Level,Seed=header.Seed};
        }
    }
    private static bool OpenClientWire(NetNode net,WorldJoin join)
    {
        if(!WireEnabled)return true;
        lock(WireSync(net))
        {
            var state=WireStates.GetValue(net,_=>new WireState());
            if(state.ClientTicket==null || state.ClientTicket.Epoch!=join.Header.Epoch || state.ClientTicket.RunNonce!=join.Header.HostRunNonce)
                return false;
            state.ClientOpen=true;return true;
        }
    }
    private static void GrantPeerWire(NetNode net,int id,object connection,WorldHeader header,bool open)
    {
        if(!WireEnabled || !HostConnections.TryGetValue(connection,out var context))return;
        var ticket=new WireTicket(header.HostRunNonce,header.Epoch,header.Generation,header.Floor,context.Nonce);
        lock(WireSync(net))
        {
            var state=WireStates.GetValue(net,_=>new WireState());
            if(!SameWire(state.Host?.Ticket(null),ticket))return;
            state.Peers[id]=new WirePeer {Ticket=ticket,RequestToken=header.RequestToken,Open=open};
        }
    }
    private static bool WireConsumeReady(NetNode net)
    {
        if(!WireEnabled || net==null || net.IsHost)return true;
        lock(WireSync(net))return WireStates.TryGetValue(net,out var state) && state.ClientTicket!=null && state.ClientOpen;
    }
    private static bool NeedsWireRefresh(NetNode net,WorldJoin prior,dc.pr.Level level)
    {
        if(!WireEnabled)return false;
        lock(WireSync(net))
        {
            if(!WireStates.TryGetValue(net,out var state))return true;
            var expected=state.Expected;
            if(expected!=null && expected.Level==level.map.id.ToString() &&
                BitConverter.DoubleToInt64Bits(expected.Seed)==BitConverter.DoubleToInt64Bits(level.map.seed) &&
                expected.Generation==(int)Field("s_levelIdentityToken") &&
                (expected.Epoch>(prior.Header?.Epoch??prior.ExpectedWireEpoch) || expected.RunNonce!=(prior.Header?.HostRunNonce??prior.ExpectedWireRun)))return true;
            return prior.Complete && state.ClientTicket==null;
        }
    }
    private static void CaptureExpectedWire(NetNode net,WorldJoin join)
    {
        if(!WireEnabled)return;
        lock(WireSync(net))if(WireStates.TryGetValue(net,out var state) && state.Expected!=null)
        {join.ExpectedWireEpoch=state.Expected.Epoch;join.ExpectedWireRun=state.Expected.RunNonce;}
    }
    private static void TickWireNotices(NetNode net)
    {
        if(!WireEnabled || net==null || !net.IsHost || !net.IsAlive || !WireStates.TryGetValue(net,out var state))return;
        WireAuthority authority;lock(WireSync(net))
        {if(Environment.TickCount64<state.NextNotice || state.Host==null)return;state.NextNotice=Environment.TickCount64+1000;authority=state.Host;}
        var clients=new List<object>();
        lock(NetField(net,"_clientsLock"))foreach(DictionaryEntry pair in (IDictionary)NetField(net,(bool)NetField(net,"_useSteamTransport")?"_steamClients":"_clients"))clients.Add(pair.Value);
        foreach(var connection in clients)
            if(HostConnections.TryGetValue(connection,out var context))
                _=SendWorld(net,connection,"CSFEPOCH|"+context.Nonce+"|"+JsonConvert.SerializeObject(authority));
    }
    private static bool ReadWireNotice(NetNode __instance,string line,ref bool __result,ref string forwardLine)
    {
        if(line==null || !line.StartsWith("CSFEPOCH|",StringComparison.Ordinal))return true;
        __result=true;forwardLine=null;if(__instance.IsHost)return false;
        try
        {
            var parts=line.Split(new[] {'|'},3);
            if(parts.Length!=3 || parts[2].Length>4096 || !ClientConnections.TryGetValue(__instance,out var context) || context.Nonce!=parts[1])return false;
            var authority=JsonConvert.DeserializeObject<WireAuthority>(parts[2]);
            var descriptor=Coordinator.GetMethod("GetCurrentRemoteDescriptor",Flags).Invoke(null,null);
            if(authority==null || !Guid.TryParseExact(authority.RunNonce,"N",out _) || authority.Epoch<=0 || authority.Generation<=0 || authority.Floor<=0 ||
                !double.IsFinite(authority.Seed) || string.IsNullOrEmpty(authority.Level) || authority.Level.Length>128 || descriptor==null ||
                authority.SessionId!=(Guid)Property(descriptor,"SessionId") || authority.RunId!=(long)Property(descriptor,"RunId") || authority.Sequence!=(int)Property(descriptor,"Sequence"))return false;
            lock(WireSync(__instance))
            {
                var state=WireStates.GetValue(__instance,_=>new WireState());var previous=state.Expected;
                if(previous!=null && previous.SessionId==authority.SessionId && previous.RunId==authority.RunId && previous.Sequence==authority.Sequence &&
                    (authority.RunNonce!=previous.RunNonce || authority.Epoch<=previous.Epoch))return false;
                state.Expected=authority;
                if(state.ClientTicket!=null && !SameWire(state.ClientTicket,authority.Ticket(null)))
                {__instance.ClearMobSyncQueues();state.ClientTicket=null;state.ClientOpen=false;}
            }
        }
        catch(Exception error){Log.Warning(error,"[CoopSyncFixes] Rejected epoch announcement");}
        return false;
    }
}
