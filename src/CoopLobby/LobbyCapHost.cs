using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Steamworks;

// Host gates of the two-player cap. Bookkeeping is by connection object (ConditionalWeakTable), never by id:
// ids 2..4 are reused as soon as CleanupHostClient/CleanupHostSteamClient free them (Tcp.cs:448-454,
// Steam.cs:864-870; IL 20292-20410, 19869-19989), and those mod paths also free the seat itself.
// A connection keeps the decision it got even after the fault gate trips (its later lines stay suppressed), so a
// refused peer can never receive ID or the cached run state half way; new connections are then left to the mod.
public static partial class LobbyCap
{
    sealed class Seat{public long Since;public bool Rejected;}
    sealed class Refusal
    {
        public readonly WeakReference<NetNode> Node;public readonly WeakReference<object> Conn;
        public Refusal(NetNode n,object c){Node=new(n);Conn=new(c);}
    }
    // A late-refused Steam connection is still registered in the mod, and the mod never drops it on its own (no
    // host-side Steam handshake or receive timeout, map.md 2.2.5, verify.md #12). Left alone it would get every
    // Steam broadcast (BroadcastLineSafe has no HandshakeComplete filter, IL 168285-168369), and that SteamID would
    // pass our registration gate as "known" with all its lines dropped. So it is handed back to the mod's cleanup.
    sealed class Eviction
    {
        public readonly WeakReference<NetNode> Node;public readonly WeakReference<object> Conn;public readonly int Id;public readonly long Due;
        public Eviction(NetNode n,object c,int id,long due){Node=new(n);Conn=new(c);Id=id;Due=due;}
    }
    static readonly ConditionalWeakTable<object,Seat> Seats=new();
    static readonly Refusal[] RefusedById=new Refusal[8];   // refused connection per id (TCP, or a late Steam refusal)
    static int _rejectedEver;                                // fast path: 0 until a connection with an id is refused
    static readonly object SteamSync=new();
    static readonly Dictionary<ulong,long> SteamSent=new();
    static readonly List<Eviction> Evictions=new();          // locked by itself; filled and drained on the main thread
    static long _lastRefusal,_refused,_dropped;
    static int _toastsDue;

    // NetNode.SendLineToClientSafe(ClientConnection client, string line) -> Task, private instance, 20 bytes
    // (IL 20411-20419). Threads: AcceptLoop pool thread for WELCOME/ID/replay (IL 166917-168109), the main thread for
    // broadcasts and forwards, CSF fan-out tasks. Priority.First and `before` CSF/colour: we must see the untouched
    // WELCOME and mark a refused connection before the ID line reaches CSF's StampConnectionId (Join.cs:84-95).
    // CSF's prefixes still run after ours returns false (HarmonyX, notes\cap\harmony-order.ps1): StampConnectionId
    // only records a join context for the refused connection object (never used: CSF peers need HandshakeComplete,
    // CinematicPresentation.cs:87, ElevatorProtocol.cs:133) and AddressWireToPeer only rewrites MOB lines or sets
    // the same CompletedTask, so the suppression stands. __result must never be null: AcceptLoop awaits it and an
    // exception there ends accepting for good (verify.md #4).
    static bool BeforeSendToClient(NetNode __instance,object client,ref string line,ref Task __result)
    {
        try
        {
            if(client==null)return true;
            if(Volatile.Read(ref _rejectedEver)!=0&&Seats.TryGetValue(client,out var seat)&&seat.Rejected){__result=Task.CompletedTask;return false;}
            if(!Installed||Gate.Tripped||!LobbyCapRules.IsWelcome(line)||Seats.TryGetValue(client,out _)||!__instance.IsHost)return true;
            JudgeTcp(__instance,client,ref line);
            Gate.Ok();
        }
        catch(Exception e){Gate.Fail(e);}
        return true;
    }
    static void JudgeTcp(NetNode net,object client,ref string line)
    {
        var refs=Refs(net);long now=Stopwatch.GetTimestamp();int self=-1;var others=new List<LobbyCapRules.Peer>(3);
        lock(refs.Lock)
        {
            foreach(DictionaryEntry e in refs.Tcp){if(ReferenceEquals(e.Value,client)){self=(int)e.Key;continue;}others.Add(TcpPeer(e.Value));}
            foreach(DictionaryEntry e in refs.Steam)others.Add(SteamPeer(e.Value));
        }
        bool slot=self>=0&&self<RefusedById.Length;
        if(!LobbyCapRules.SeatTaken(others,now,Ticks(LobbyCapRules.PendingSeconds)))
        {
            Seats.AddOrUpdate(client,new Seat{Since=now});
            if(slot)Volatile.Write(ref RefusedById[self],null);
            return;
        }
        // Build first, then mark: a throw in between would otherwise send a real WELCOME and suppress the rest.
        var reject=LobbyCapRules.RejectLine(line);
        Seats.AddOrUpdate(client,new Seat{Since=now,Rejected=true});
        Interlocked.Increment(ref _rejectedEver);
        if(slot)Volatile.Write(ref RefusedById[self],new Refusal(net,client));
        line=reject;
        NoteRefusal("TCP",self);
    }
    static LobbyCapRules.Peer TcpPeer(object c)
    {
        bool complete=(bool)_tcpComplete.GetValue(c);
        return Seats.TryGetValue(c,out var s)?new(s.Rejected,complete,true,s.Since):new(false,complete,false,0);
    }
    // Steam peers are never refused at registration (they are simply not registered); only a late refusal (at HELLO,
    // or the handshake backstop) marks a registered one, which is then evicted.
    static LobbyCapRules.Peer SteamPeer(object c)=>new(Refused(c),(bool)_steamComplete.GetValue(c),true,(long)_steamLastPacket.GetValue(c));

    // NetNode.TryGetOrRegisterSteamClient(CSteamID remoteSteamId, out SteamClientConnection connection) -> bool,
    // private instance (IL 19555-19686). Thread: the Steam bridge loop (pool thread, IL 172906), on EVERY packet,
    // so a known SteamID costs one dictionary lookup under the mod's own lock and nothing else. A known SteamID
    // (same-person reconnect included, IL 19583-19599) is always left to the mod. Nobody else patches this method
    // (verify.md #10). The caller ignores `connection` when we return false (IL 172907), so it is not touched.
    static bool BeforeRegisterSteam(NetNode __instance,CSteamID __0,ref bool __result)
    {
        try
        {
            if(!Installed||Gate.Tripped||!__instance.IsHost)return true;
            ulong key=__0.m_SteamID;var refs=Refs(__instance);
            List<LobbyCapRules.Peer> others;long now;
            lock(refs.Lock)
            {
                if(refs.SteamIds.ContainsKey(key))return true;
                now=Stopwatch.GetTimestamp();others=new List<LobbyCapRules.Peer>(3);
                foreach(DictionaryEntry e in refs.Steam)others.Add(SteamPeer(e.Value));
                foreach(DictionaryEntry e in refs.Tcp)others.Add(TcpPeer(e.Value));
            }
            if(!LobbyCapRules.SeatTaken(others,now,Ticks(LobbyCapRules.PendingSeconds))||!_isCurrent(__instance))return true;
            // From here the peer is refused even if the reject line cannot be sent (fail closed: it then times out on
            // its own retries, "Can't find lobby", instead of becoming a third player).
            if(SteamResendDue(key,now)){SendSteamReject(__instance,key);NoteRefusal("Steam",0);}
            __result=false;Gate.Ok();
            return false;
        }
        catch(Exception e){Gate.Fail(e);return true;}
    }
    // BuildWelcomeLine runs CSF's token postfixes, so the reject carries the same tokens as a real WELCOME.
    // SendLineToSteamBridgeSafe(ulong,string,EP2PSend,int) (IL 18804-18857) encodes UTF-8 and its TrySend serializes
    // Steam calls itself (verify.md missed #4); reliable, host->client channel 1.
    static void SendSteamReject(NetNode net,ulong steamId)
    {
        try{_sendBridge(net,steamId,RejectNow(),EP2PSend.k_EP2PSendReliable,_hostToClient);}
        catch(Exception e){Gate.Fail(e);}
    }
    static string RejectNow()
    {
        try{return LobbyCapRules.RejectLine(_buildWelcome());}
        catch(Exception e){Gate.Fail(e);return LobbyCapRules.RejectLine(null);}
    }
    static bool SteamResendDue(ulong key,long now)
    {
        lock(SteamSync)
        {
            LobbyCapRules.Prune(SteamSent,now,Ticks(60),32);
            if(SteamSent.TryGetValue(key,out var last)&&!LobbyCapRules.RateAllows(last,now,Ticks(LobbyCapRules.SteamResendSeconds)))return false;
            SteamSent[key]=now;return true;
        }
    }

    // NetNode.HandleLine(string line, int? senderId, out string forwardLine) -> bool, private instance, 11632 bytes
    // (IL 9853). Thread: main (network queue, verify.md #6: TCP IL 166702, Steam IL 166548). A consumed line is
    // __result=true, forwardLine=null (the original also starts with forwardLine=null, IL 9908-9910), never false as
    // HandleLine's own result: on TCP false cancels the receive loop (Tcp.cs:392-396), on Steam it runs
    // CleanupHostSteamClient at once (IL 166548-166566), whose TryClosePeer can drop the reject still in flight.
    // Two jobs:
    //  1. drop every line of a refused sender.
    //  2. the late seat decision at HELLO. The HELLO branch (IL 10029-10124) calls CompleteHostHandshake (IL 10057) and
    //     then, on a Steam host, Task.Run(SendInitialStateToSteamClient(conn, forceSend:true)) (IL 10118-10121 ->
    //     IL 166298-166305), which sends a real WELCOME, ID and the cached run state through the ungated
    //     SendLineToSteamClientSafe. Skipping only CompleteHostHandshake still sends all of that, so the whole HELLO
    //     line is consumed here instead.
    // `before` CSF and colour so their line readers run after these decisions. They still run (HarmonyX), but on the
    // host they only consume their own tags: CSFEPOCH returns at once on the host (WireControl.cs:109-110), CSFEL1
    // needs a handshaken sender (ElevatorProtocol.cs:97-106), and no CSF prefix reads HELLO. A refused peer on its
    // title screen does not send those tags.
    static bool BeforeHandleLine(NetNode __instance,string line,int? senderId,ref bool __result,ref string forwardLine)
    {
        if(!senderId.HasValue)return true;
        bool hello=LobbyCapRules.IsHello(line);
        if(!hello&&Volatile.Read(ref _rejectedEver)==0)return true;     // fast path until the first refusal
        try
        {
            int id=senderId.Value;
            if((uint)id>=(uint)RefusedById.Length||!__instance.IsHost)return true;
            if(Volatile.Read(ref _rejectedEver)!=0&&DropRefused(__instance,id)){__result=true;forwardLine=null;Interlocked.Increment(ref _dropped);return false;}
            if(!hello||!Installed||Gate.Tripped)return true;
            var conn=CurrentConn(__instance,id,out bool steam);
            bool refuse=LobbyCapRules.RefuseAtHello(true,true,true,conn!=null,conn!=null&&Refused(conn),conn!=null&&OtherComplete(__instance,conn));
            Gate.Ok();
            if(!refuse)return true;
            RefuseLate(__instance,conn,id,steam,"at HELLO");
            __result=true;forwardLine=null;
            return false;
        }
        catch(Exception e){Gate.Fail(e);return true;}
    }
    static bool DropRefused(NetNode net,int id)
    {
        var rec=Volatile.Read(ref RefusedById[id]);
        if(rec==null)return false;
        if(!rec.Node.TryGetTarget(out var node)){Interlocked.CompareExchange(ref RefusedById[id],null,rec);return false;}
        if(!ReferenceEquals(node,net))return false;
        var current=CurrentConn(net,id,out _);
        bool currentRejected=current!=null&&Refused(current);
        bool recorded=rec.Conn.TryGetTarget(out var gone)&&Refused(gone);
        if(LobbyCapRules.DropLine(true,true,current!=null,currentRejected,recorded))return true;
        Interlocked.CompareExchange(ref RefusedById[id],null,rec);     // the record no longer applies (see DropLine)
        return false;
    }
    // The connection that holds an id now (CompleteHostHandshake looks Steam up first on a Steam node, IL 4772-4793).
    static object CurrentConn(NetNode net,int id,out bool steam)
    {
        var refs=Refs(net);
        lock(refs.Lock)
        {
            if(refs.Steam.Contains(id)){steam=true;return refs.Steam[id];}
            steam=false;return refs.Tcp.Contains(id)?refs.Tcp[id]:null;
        }
    }
    static bool OtherComplete(NetNode net,object self)
    {
        var refs=Refs(net);
        lock(refs.Lock)
        {
            foreach(DictionaryEntry e in refs.Tcp)if(!ReferenceEquals(e.Value,self)&&!Refused(e.Value)&&(bool)_tcpComplete.GetValue(e.Value))return true;
            foreach(DictionaryEntry e in refs.Steam)if(!ReferenceEquals(e.Value,self)&&!Refused(e.Value)&&(bool)_steamComplete.GetValue(e.Value))return true;
        }
        return false;
    }
    static bool Refused(object c)=>Seats.TryGetValue(c,out var s)&&s.Rejected;
    static void MarkRefused(NetNode net,object conn,int id)
    {
        Seats.AddOrUpdate(conn,new Seat{Since=Stopwatch.GetTimestamp(),Rejected=true});
        Interlocked.Increment(ref _rejectedEver);
        if(id>=0&&id<RefusedById.Length)Volatile.Write(ref RefusedById[id],new Refusal(net,conn));
    }

    // NetNode.CompleteHostHandshake(int senderId) -> void, private instance (IL 4751-4835). Thread: main (HELLO
    // branch of HandleLine, IL 10057). A refused connection never completes, so it never counts in
    // ConnectedClientCount (boss HP, exit gating, HUD; map.md 1.3) and never reaches NotifyRemoteConnected; the mod's
    // 8 s TCP handshake timeout (IL 169284) frees its id. Backstop only: the late decision is normally taken at HELLO
    // (BeforeHandleLine), and nothing between that prefix and this call can complete another peer (both run inside the
    // same HandleLine call on the main thread, the only place HandshakeComplete is set). So RefuseLate here means our
    // HELLO check faulted; it still refuses (fail closed), though on Steam the HELLO branch's own initial-state resend
    // (IL 10118-10121) then follows the reject line. Nobody else patches this method.
    static bool BeforeCompleteHandshake(NetNode __instance,int __0)
    {
        try
        {
            if(!Installed||!__instance.IsHost)return true;
            var conn=CurrentConn(__instance,__0,out bool steam);
            if(conn==null)return true;                                      // the original finds nothing either
            switch(LobbyCapRules.Handshake(Refused(conn),Gate.Tripped,OtherComplete(__instance,conn)))
            {
                case LobbyCapRules.HandshakeVerdict.Refuse:
                    LobbyCore.Warn("CAP: refused peer {Id} tried to complete the handshake; kept out",__0);
                    return false;
                case LobbyCapRules.HandshakeVerdict.RefuseLate:
                    RefuseLate(__instance,conn,__0,steam,"at the handshake, backstop");
                    return false;
            }
            Gate.Ok();
            return true;
        }
        catch(Exception e){Gate.Fail(e);return true;}
    }

    // Late refusal of a connection the WELCOME/registration gate let in: a stale pending occupant that finished late,
    // or a TCP gate that never ran. Main thread. The peer gets the reject line now: the client re-validates every
    // WELCOME it reads (HandleLine WELCOME branch, IL 9995-10009) and leaves on it. Send first, then mark: our
    // SendLineToClientSafe prefix suppresses lines to refused connections and judges an unjudged WELCOME, so an
    // unjudged connection is recorded as judged before the send. A failed send still refuses (TCP: the mod's 8 s
    // handshake timeout closes it; Steam: the eviction below unregisters it).
    static void RefuseLate(NetNode net,object conn,int id,bool steam,string where)
    {
        bool judged=Seats.TryGetValue(conn,out _);
        LobbyCore.Warn(steam||judged?"CAP: peer {Id} sent HELLO while another player is in; refused late ({Where})"
            :"CAP: TCP peer {Id} was never judged at WELCOME (SendLineToClientSafe gate not effective); refused ({Where})",id,where);
        if(!judged)Seats.AddOrUpdate(conn,new Seat{Since=Stopwatch.GetTimestamp()});
        try
        {
            if(steam){ulong key=((CSteamID)_steamIdOf.GetValue(conn)).m_SteamID;SteamResendDue(key,Stopwatch.GetTimestamp());SendSteamReject(net,key);}
            else _sendTcp.Invoke(net,new[]{conn,RejectNow()});
        }
        catch(Exception e){Gate.Fail(e);}
        MarkRefused(net,conn,id);
        if(steam)lock(Evictions)Evictions.Add(new Eviction(net,conn,id,Stopwatch.GetTimestamp()+Ticks(LobbyCapRules.SteamEvictSeconds)));
        NoteRefusal(steam?"Steam (late)":"TCP (late)",id);
    }

    // Main thread (AfterFrame), also after a fault trip: a late-refused Steam peer is handed back to the mod's own
    // NetNode.CleanupHostSteamClient(SteamClientConnection) (private instance, IL 19869-19989; called, not patched).
    // That method removes _steamClients[AssignedId] and _steamClientIdsBySteam[SteamId] BY KEY (IL 19888-19899), so
    // it is called only while this exact object still holds its id. That check cannot go stale before the call:
    // every other caller runs on the main thread too (HandleLine-false path IL 166566, session-fail grace queued with
    // EnqueueMainThreadCoalesced IL 184071), and registration on the bridge thread only takes free ids. It runs
    // SteamEvictSeconds after the reject because its TryClosePeer (IL 19925) could drop the reliable reject still in
    // flight. The peer never completed, so no remote-left/disconnected job is queued (IL 19974-19977). CSF makes the
    // same call for a timed-out join (JoinLease.cs:62-63). Afterwards the SteamID meets the registration gate again:
    // refused (rate limited) while the seat is taken, admitted once it is free.
    static void TickEvictions()
    {
        Eviction[] due;
        lock(Evictions)
        {
            if(Evictions.Count==0)return;
            long now=Stopwatch.GetTimestamp();
            if(!Evictions.Exists(e=>now>=e.Due))return;
            due=Evictions.FindAll(e=>now>=e.Due).ToArray();
            Evictions.RemoveAll(e=>now>=e.Due);
        }
        foreach(var e in due)
        {
            try
            {
                if(!e.Node.TryGetTarget(out var net)||!e.Conn.TryGetTarget(out var conn)||!Refused(conn)||!_isCurrent(net))continue;
                var refs=Refs(net);bool held;
                lock(refs.Lock)held=refs.Steam.Contains(e.Id)&&ReferenceEquals(refs.Steam[e.Id],conn);
                if(!held)continue;                                          // the mod already let it go
                _cleanupSteam.Invoke(net,new[]{conn});
                LobbyCore.Info("CAP: unregistered the late-refused Steam peer {Id}",e.Id);
            }
            catch(Exception x){LobbyCore.Warn("CAP: unregistering the refused Steam peer {Id} failed: {Error}",e.Id,x.GetBaseException().Message);}
        }
    }

    // Worker or main thread. One host notice per burst of refusals (the main-thread tick shows it).
    static void NoteRefusal(string transport,int id)
    {
        long now=Stopwatch.GetTimestamp();long total=Interlocked.Increment(ref _refused);
        long previous=Interlocked.Exchange(ref _lastRefusal,now);
        if(LobbyCapRules.NewBurst(previous,now,Ticks(LobbyCapRules.BurstSeconds)))Interlocked.Exchange(ref _toastsDue,1);
        LobbyCore.Info("CAP: lobby full, refused a third player over {Transport} (id {Id}); refusals so far {Total}",transport,id,total);
    }
}
