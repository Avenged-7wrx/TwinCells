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
using HarmonyLib;

// Saved in coremod\config\CoopLobbyColor.json by DCCM's Config<T> (ModCore\Storage\Config.cs:67-73). color is the
// short palette key of the last EXPLICIT pick ("ice"), null until the player picks one; a granted colour is never
// saved. The first read of Value writes the file (Config.cs:87), so the file exists with color=null before any pick.
public sealed class LobbyColorConfig{public int version=1;public string color;}

// Part COLOR (SPEC.md): a player colour picked in the lobby, unique per player, host-authoritative, persisted.
//
// On only where this machine draws the lobby colours in the run. CSF reads COOP_SYNC_DISABLE_COLORS once at its own
// start and otherwise tints the bodies by net id (CSF Colors.cs:14-20, 37-45); the shipped launcher removes every
// COOP_SYNC_DISABLE_* variable and sets COOP_TWO_PLAYER_COLORS=1 (PLAY Launch-Coop.ps1:68, 71). Without the switch the
// lobby would offer colours the run never draws (LobbyPalette.cs:22-24), so Install refuses: no patch, no HI,
// PartRunning=false, and the UI shows the default pair, which is CSF's two-player look. With the switch the protocol goes
// live on the first frame on which our render tint owns the bodies (LobbyColorTint.Check), and the part switches itself
// off for the process if that ever stops. So PeerSupports on either machine means both screens draw the same table.
//
// Capability, soft in both directions: nothing in the HELLO/WELCOME handshake (LobbyColorRules header: a build-field
// token is rejected by every peer that does not strip it). Our RejectIncompatiblePeer postfix sees the client accept a
// WELCOME (false = accepted; on the client it is called only from HandleLine's WELCOME branch, IL 9994-10009) and starts
// a connection epoch; once the ID line is applied (net.id reads the ID field, IL 573-576, which is 0 from client start,
// IL 1052-1053 / 1242-1243, until HandleLine's ID branch sets it, IL 9951) the client says CPCOL1|HI and the host's SET
// is the support signal. A host without the part drops HI silently; a client
// without it never says HI and never gets a SET.
//
// Messages (LobbyColorRules header): our Priority.First HandleLine prefix consumes every CPCOL1| line (__result=true,
// forwardLine=null, return false); the base never parses them and its host router never forwards them (it forwards only
// when forwardLine!=null, TCP IL 166712-166731, Steam IL 166573-166592).
// Sending: the client uses NetNode.SendLineSafe (IL 18608: to the host over TCP or the Steam bridge); the host sends
// per completed connection with SendLineToClientSafe / SendLineToSteamClientSafe (IL 20411 / 18741), as CSF does
// (CinematicPresentation.cs:94-103). The host's own SendLineSafe would broadcast to every client (IL 18638-18644 ->
// BroadcastLineSafe IL 18672), including connections that never said HI; it is not used.
//
// Threads: HandleLine, its prefixes and RejectIncompatiblePeer run in MainThreadPump jobs on the game thread
// (TCP IL 169430-169434, Steam IL 169677; player-color verify.md item 5); still they only parse and enqueue. All
// state changes and sends happen in our ModEntry.OnFrameUpdate postfix.
public static partial class LobbyColor
{
    public const string Owner="CoopLobby.Color.20261110";
    const long ScanMs=100,LobbyResendMs=3000,RunResendMs=5000,PendingTimeoutMs=5000;
    const int InboxCap=64;
    static readonly object Gate=new();
    static readonly FaultGate Faults=new("LobbyColor");
    static bool _installed,_live,_off;

    // ---- bound members (Install) ----
    static MethodInfo _sendLineSafe,_sendTcp,_sendSteam,_snapshot;
    static PropertyInfo _snapHost,_snapClient,_tcpComplete,_steamComplete;
    static FieldInfo _clientsLock,_clients,_steamClients;
    static Func<bool> _inActualRun;
    static ModCore.Storage.Config<LobbyColorConfig> _config;
    static string[] _shortKeys;
    static int _preferred=-1;

    // ---- per connection ----
    static readonly ConditionalWeakTable<object,LobbyColorRules.HostPeer> HostPeers=new();       // key: ClientConnection / SteamClientConnection
    static readonly ConditionalWeakTable<NetNode,LobbyColorRules.ClientLink> ClientLinks=new();
    struct Incoming{public NetNode Net;public object Connection;public int Sender;public LobbyColorRules.Message Msg;}
    static readonly ConcurrentQueue<Incoming> Inbox=new();static int _inboxCount;

    // ---- session (game thread) ----
    sealed class Seat{public int Id;public object Connection;public LobbyColorRules.HostPeer Peer;public long Seen;}
    static NetNode _session;static bool _sessionHost;static int _clientEpoch=-1;
    static int[] _table=LobbyColorRules.NewTable();static int _rev=1;
    static readonly Dictionary<int,Seat> _seats=new();
    static long _nextScan,_nextLobby;static bool _inLobby,_inRun,_unsupportedShown;
    static ConditionalWeakTable<NetNode,object> _csfRejected;static bool _csfLogged;

    public static void Install()
    {
        lock(Gate)
        {
            if(_installed)return;
            if(Environment.GetEnvironmentVariable("COOP_SYNC_DISABLE_COLORS")!="1")
                throw new InvalidOperationException("colour choice off: in-run body tint is owned by CoopSyncFixes (COOP_SYNC_DISABLE_COLORS is not 1; Launch-Coop.ps1 must set it after line 68)");
            // Before any connection: a WELCOME accepted before our postfix existed would leave that link without an epoch.
            if(ModEntry._net?.IsAlive==true)throw new InvalidOperationException("colour part must install before any connection");
            if(LobbyPalette.Count!=LobbyColorRules.Colors||!LobbyColorRules.ValidColor(LobbyPalette.HostDefault)||!LobbyColorRules.ValidColor(LobbyPalette.ClientDefault)||LobbyPalette.HostDefault==LobbyPalette.ClientDefault)
                throw new InvalidOperationException("palette does not match the colour rules");
            var net=typeof(NetNode);
            var reject=LobbyCore.Method(net,"RejectIncompatiblePeer",false,typeof(bool),typeof(string),typeof(string));
            var handle=LobbyCore.Method(net,"HandleLine",false,typeof(bool),typeof(string),typeof(int?),typeof(string).MakeByRefType());
            var frame=LobbyCore.Method(typeof(ModEntry),"OnFrameUpdate",false,typeof(void),typeof(double));
            var tcp=net.GetNestedType("ClientConnection",BindingFlags.Public|BindingFlags.NonPublic)??throw new InvalidOperationException("Pinned type missing: NetNode.ClientConnection");
            var steam=net.GetNestedType("SteamClientConnection",BindingFlags.Public|BindingFlags.NonPublic)??throw new InvalidOperationException("Pinned type missing: NetNode.SteamClientConnection");
            _sendLineSafe=LobbyCore.Method(net,"SendLineSafe",false,typeof(Task),typeof(string));
            _sendTcp=LobbyCore.Method(net,"SendLineToClientSafe",false,typeof(Task),tcp,typeof(string));
            var steamSend=net.GetMethod("SendLineToSteamClientSafe",LobbyCore.I)?.GetParameters();
            // The third parameter is Nullable<Steamworks.EP2PSend>; it is matched by shape, never named here (SPEC: no
            // Nullable<T> from the runtime corelib in this add-on's signatures). Passing null picks the reliable channel
            // for our tag (ResolveSteamSendType returns 0 for it, IL 18465-18499; null path IL 18772-18779).
            if(steamSend==null||steamSend.Length!=3||!steamSend[2].ParameterType.IsGenericType||steamSend[2].ParameterType.GetGenericTypeDefinition()!=typeof(Nullable<>)||
               steamSend[2].ParameterType.GetGenericArguments()[0].FullName!="Steamworks.EP2PSend")throw new InvalidOperationException("Pinned method mismatch: NetNode.SendLineToSteamClientSafe");
            _sendSteam=LobbyCore.Method(net,"SendLineToSteamClientSafe",false,typeof(Task),steam,typeof(string),steamSend[2].ParameterType);
            _clientsLock=LobbyCore.Field(net,"_clientsLock",typeof(object),false);
            _clients=LobbyCore.Field(net,"_clients",typeof(Dictionary<,>).MakeGenericType(typeof(int),tcp),false);
            _steamClients=LobbyCore.Field(net,"_steamClients",typeof(Dictionary<,>).MakeGenericType(typeof(int),steam),false);
            _tcpComplete=tcp.GetProperty("HandshakeComplete",LobbyCore.I);_steamComplete=steam.GetProperty("HandshakeComplete",LobbyCore.I);
            if(_tcpComplete?.PropertyType!=typeof(bool)||_steamComplete?.PropertyType!=typeof(bool))throw new InvalidOperationException("Pinned property mismatch: HandshakeComplete");
            var lobby=LobbyCore.ModType("DeadCellsMultiplayerMod.LobbySession");
            _snapshot=lobby.GetMethod("ReadSessionSnapshot",LobbyCore.S,null,Type.EmptyTypes,null)??throw new MissingMethodException("LobbySession","ReadSessionSnapshot");
            _snapHost=_snapshot.ReturnType.GetProperty("InHostStatusMenu");_snapClient=_snapshot.ReturnType.GetProperty("InClientWaitingMenu");
            if(_snapHost?.PropertyType!=typeof(bool)||_snapClient?.PropertyType!=typeof(bool))throw new InvalidOperationException("Pinned property mismatch: LobbySessionSnapshot menus");
            _inActualRun=LobbyCore.Delegate<Func<bool>>(LobbyCore.Method(lobby,"IsInActualRun",true,typeof(bool)));
            _shortKeys=LobbyPalette.Colors.Select(c=>LobbyColorRules.StoreKey(c.Key)).ToArray();
            if(_shortKeys.Distinct(StringComparer.Ordinal).Count()!=_shortKeys.Length)throw new InvalidOperationException("palette keys are not unique");

            Harmony harmony=null;var targets=new List<MethodBase>();
            try
            {
                // Created once: the constructor registers the instance as a DCCM event receiver (Config.cs:72).
                _config??=new ModCore.Storage.Config<LobbyColorConfig>("CoopLobbyColor");
                _preferred=LobbyColorRules.ParsePreference(_config.Value?.color,_shortKeys);
                // Throws when the body tint cannot bind: then nothing would draw the lobby colours in the run.
                LobbyColorTint.Install();
                harmony=new Harmony(Owner);
                void Patch(MethodBase target,string prefix,string postfix,int priority)
                {
                    targets.Add(target);
                    harmony.Patch(target,prefix:prefix==null?null:new HarmonyMethod(typeof(LobbyColor).GetMethod(prefix,LobbyCore.S)){priority=priority},
                        postfix:postfix==null?null:new HarmonyMethod(typeof(LobbyColor).GetMethod(postfix,LobbyCore.S)){priority=priority});
                    var info=Harmony.GetPatchInfo(target);
                    if(info==null||info.Prefixes.Count(p=>p.owner==Owner)!=(prefix==null?0:1)||info.Postfixes.Count(p=>p.owner==Owner)!=(postfix==null?0:1)||
                       info.Transpilers.Any(p=>p.owner==Owner)||info.Finalizers.Any(p=>p.owner==Owner))
                        throw new InvalidOperationException("colour patch on "+target.Name+" was not installed exactly once");
                }
                Patch(reject,null,nameof(AfterReject),Priority.Normal);
                Patch(handle,nameof(BeforeHandleLine),null,Priority.First);
                Patch(frame,null,nameof(AfterFrame),Priority.Normal);   // before the UI part's Priority.Last tick reads the state
                LookUpCsf();
                // Live on the first frame whose tint check passes (AfterFrame -> GoLive); until then the UI sees defaults.
                LobbyColorState.RequestHandler=null;LobbyColorState.Clear();LobbyColorState.PartRunning=false;
                _installed=true;
                LobbyCore.Info("Colour part installed: saved colour {Color}; {Tint}",_preferred<0?"none":_shortKeys[_preferred],LobbyColorTint.Describe());
            }
            catch
            {
                _live=false;LobbyColorState.PartRunning=false;LobbyColorState.RequestHandler=null;
                try{LobbyColorTint.Uninstall();}catch{}
                if(harmony!=null)foreach(var t in targets)try{harmony.Unpatch(t,HarmonyPatchType.All,Owner);}catch(Exception rollback){LobbyCore.Warn("Colour rollback of {Target} failed: {Error}",t.Name,rollback.Message);}
                throw;
            }
        }
    }

    static void GoLive()
    {
        _live=true;
        LobbyColorState.Clear();LobbyColorState.RequestHandler=OnRequest;LobbyColorState.PartRunning=true;
        LobbyCore.Info("Colour choice on: this add-on draws the lobby colours in the run");
    }
    // Trip, fault or a tint we do not own: back to the state of a machine without this part, for the rest of the
    // process. The patches stay but are inert: no HI, no SET, our lines are still consumed silently.
    static void SwitchOff(string why)
    {
        _off=true;_live=false;LobbyColorState.PartRunning=false;LobbyColorState.RequestHandler=null;
        try{LobbyColorTint.Off();}catch{}
        try{EndSession();}catch{}
        LobbyColorState.Clear();
        LobbyCore.Warn("Colour choice switched off ({Why}); default colours from now on",why);
    }

    // ================= Harmony bodies (never throw) =================

    static void AfterReject(NetNode __instance,string expectedTag,bool __result)
    {
        try
        {
            // __result=true: rejected, HandleLine returns false and the link drops (IL 10007-10009).
            if(!_installed||__result||__instance==null||expectedTag!="WELCOME"||__instance.IsHost)return;
            ClientLinks.GetValue(__instance,_=>new LobbyColorRules.ClientLink()).NewEpoch();
        }
        catch(Exception e){if(Faults.Fail(e))SwitchOff("fault");}
    }
    static bool BeforeHandleLine(NetNode __instance,string line,int? senderId,ref string forwardLine,ref bool __result,bool __runOriginal)
    {
        if(line==null||!line.StartsWith(LobbyColorRules.Tag,StringComparison.Ordinal))return true;
        // The framework's HarmonyX 2.16.1 runs every prefix even after one returned false (checked offline,
        // notes\color\harmony-prefix-order.ps1). If an earlier prefix already consumed or rejected this line (the cap
        // part's rejected-sender drop) its verdict stands and we do not touch the line.
        if(!__runOriginal)return false;
        __result=true;forwardLine=null;
        try
        {
            // CSF's terminal rejection, kept even if a consuming prefix ran first (CSF CinematicPresentation.cs:224-226).
            if(__instance!=null&&_csfRejected!=null&&_csfRejected.TryGetValue(__instance,out _)){__result=false;return false;}
            if(!_live||Faults.Tripped||__instance==null||!ReferenceEquals(__instance,ModEntry._net)||!__instance.IsAlive)return false;
            if(!LobbyColorRules.TryParse(line,out var m))return false;
            if(__instance.IsHost)
            {
                // Transport identity only (senderId: the TCP connection id, or SteamClientConnection.AssignedId, IL
                // 172906-172917), never an id from the payload, and only a completed connection. The nonce is checked
                // in HostTick against the epoch the last HI set.
                if(m.Kind!=LobbyColorRules.Kind.Hi&&m.Kind!=LobbyColorRules.Kind.Req||!senderId.HasValue||!LobbyColorRules.ValidId(senderId.Value)||senderId.Value==LobbyColorRules.HostId)return false;
                var connection=Connection(__instance,senderId.Value,true,out _);
                if(connection!=null)Enqueue(new Incoming{Net=__instance,Connection=connection,Sender=senderId.Value,Msg=m});
            }
            else
            {
                // Only after a WELCOME was accepted on this node and the ID line applied (the causal proof of a finished
                // handshake, CSF ElevatorProtocol.cs:93-96); the epoch nonce is checked in ClientTick.
                if(m.Kind!=LobbyColorRules.Kind.Set||!ClientLinks.TryGetValue(__instance,out var link)||!link.WelcomeAccepted||
                   !LobbyColorRules.ValidId(__instance.id)||__instance.id==LobbyColorRules.HostId)return false;
                Enqueue(new Incoming{Net=__instance,Sender=LobbyColorRules.HostId,Msg=m});
            }
        }
        catch(Exception e){if(Faults.Fail(e))SwitchOff("fault");}
        return false;
    }
    static void Enqueue(Incoming item)
    {
        if(Interlocked.Increment(ref _inboxCount)>InboxCap){Interlocked.Decrement(ref _inboxCount);return;}
        Inbox.Enqueue(item);
    }
    static bool Dequeue(out Incoming item)
    {
        if(!Inbox.TryDequeue(out item))return false;
        Interlocked.Decrement(ref _inboxCount);return true;
    }

    // ================= frame tick (game thread, after ModEntry.OnFrameUpdate) =================

    static void AfterFrame()
    {
        if(!_installed||_off||Faults.Tripped)return;
        try
        {
            long now=Environment.TickCount64;
            var block=LobbyColorTint.Check(now);
            if(block!=null){SwitchOff("in-run body tint not drawn by this add-on: "+block);return;}
            if(!_live)GoLive();
            var net=ModEntry._net;
            var current=net!=null&&net.IsAlive?net:null;
            if(!ReferenceEquals(current,_session)){EndSession();if(current!=null)BeginSession(current);}
            if(_session!=null)
            {
                if(now>=_nextLobby||now<_nextLobby-ScanMs){_nextLobby=now+ScanMs;ReadLobby();}
                if(_sessionHost)HostTick(now);else ClientTick(now);
            }
            else while(Dequeue(out _)){}
            LobbyColorTint.Tick(now,_session!=null);
            Faults.Ok();
        }
        catch(Exception e){if(Faults.Fail(e))SwitchOff("fault");}
    }

    static void BeginSession(NetNode net)
    {
        _session=net;_sessionHost=net.IsHost;_table=LobbyColorRules.NewTable();_rev=1;_seats.Clear();_clientEpoch=-1;
        _nextScan=0;_nextLobby=0;_unsupportedShown=false;
        LobbyColorState.Clear();
        if(_sessionHost){_table[LobbyColorRules.HostId]=LobbyColorRules.HostColor(_preferred,LobbyPalette.HostDefault);Publish(false);LobbyColorState.HostAlone=true;}
        LookUpCsf();
    }
    static void EndSession()
    {
        if(_session==null&&_seats.Count==0)return;
        _session=null;_seats.Clear();_table=LobbyColorRules.NewTable();
        while(Dequeue(out _)){}
        LobbyColorState.Clear();
    }
    static void ReadLobby()
    {
        object snap=_snapshot.Invoke(null,null);
        _inLobby=(bool)_snapHost.GetValue(snap)||(bool)_snapClient.GetValue(snap);
        _inRun=_inActualRun();
    }
    static void Publish(bool peerSupports)
    {
        for(int id=1;id<=LobbyColorRules.MaxId;id++)LobbyColorState.Set(id,_table[id]);
        if(LobbyColorState.PeerSupports!=peerSupports){LobbyColorState.PeerSupports=peerSupports;LobbyColorState.Revision++;}
    }
    // Only while a lobby screen is up; the UI drops notices older than 1.5 s (LobbyColorState.Take), so an answer that
    // arrives in a run never surfaces in a later lobby.
    static void Notice(string text){if(_inLobby)LobbyColorState.Post(text);}
    static void TakenNotice(int index,int myId)
    {
        // Only when someone holds it. A denial without a holder (picks frozen for the run, the holder left between the
        // REQ and the SET) gets no text: LobbyText has no fitting key, and "taken by" would name the wrong player.
        int holder=LobbyColorRules.Holder(_table,index,myId);
        if(holder<1)return;
        string name=null;
        foreach(var p in LobbyModel.Players)if(p.Id==holder)name=p.Name;
        Notice(LobbyText.Format("colorTaken",string.IsNullOrEmpty(name)?LobbyText.Get(holder==LobbyColorRules.HostId?"host":"player2"):name));
    }
    static void UnsupportedNotice()
    {
        if(_unsupportedShown||!_inLobby)return;
        _unsupportedShown=true;Notice(LobbyText.Get("colorUnsupported"));
    }

    // ---- host ----
    static void HostTick(long now)
    {
        // A queued HI can be older than our view of the seats: rescan before handling it.
        if(now>=_nextScan||now<_nextScan-ScanMs||!Inbox.IsEmpty){_nextScan=now+ScanMs;ScanSeats(now);}
        while(Dequeue(out var inc))
        {
            if(!ReferenceEquals(inc.Net,_session))continue;
            if(!_seats.TryGetValue(inc.Sender,out var seat)||!ReferenceEquals(seat.Connection,inc.Connection))continue;
            var peer=seat.Peer;
            if(inc.Msg.Kind==LobbyColorRules.Kind.Hi)
            {
                // A new epoch keeps the seat's row (a Steam reconnect on the same connection object keeps its colour).
                if(peer.OnHi(inc.Msg.Nonce,now)&&!LobbyColorRules.ValidColor(_table[seat.Id])){_table[seat.Id]=LobbyColorRules.JoinColor(_table,seat.Id,LobbyPalette.ClientDefault);_rev++;}
            }
            else if(peer.TakeReq(inc.Msg))
            {
                if(!_inRun)LobbyColorRules.TryGrant(_table,seat.Id,inc.Msg.Index);   // frozen in a run: the ack below answers "denied"
                _rev++;                                                     // the ack must reach the requester even when nothing changed
            }
        }
        bool any=false;
        foreach(var seat in _seats.Values)
        {
            any|=seat.Peer.Supports;
            if(!seat.Peer.Supports&&LobbyColorRules.Expired(now,seat.Seen,LobbyColorRules.HiTimeoutMs))UnsupportedNotice();
        }
        Publish(any);
        LobbyColorState.HostAlone=_seats.Count==0;    // the host picks freely while nobody holds a seat (core ColorOf/CanPick)
        // Every table change bumps _rev, so "changed" for a peer is "it has not seen this revision yet".
        foreach(var seat in _seats.Values)
        {
            var peer=seat.Peer;
            if(peer.Supports&&LobbyColorRules.ShouldSend(peer.SentRev!=_rev,_inLobby,now,peer.LastSent,LobbyResendMs,RunResendMs))SendSet(seat,now);
        }
    }
    // Completed connections by id (both transports), diffed by connection identity: ids are reused after a cleanup
    // (CleanupHostClient / CleanupHostSteamClient free them, player-color verify.md section 1).
    static void ScanSeats(long now)
    {
        var live=new List<(int Id,object Connection)>(2);
        lock(_clientsLock.GetValue(_session))
        {
            foreach(var (field,steam) in new[]{(_clients,false),(_steamClients,true)})
            {
                if(!(field.GetValue(_session) is IDictionary peers))continue;
                foreach(DictionaryEntry entry in peers)
                    if(entry.Key is int id&&entry.Value!=null&&(bool)(steam?_steamComplete:_tcpComplete).GetValue(entry.Value))live.Add((id,entry.Value));
            }
        }
        foreach(var id in _seats.Keys.ToList())
        {
            if(live.Exists(l=>l.Id==id&&ReferenceEquals(l.Connection,_seats[id].Connection)))continue;
            _seats.Remove(id);if(LobbyColorRules.ValidId(id)&&_table[id]>=0){_table[id]=-1;_rev++;}
        }
        foreach(var l in live)
        {
            if(_seats.ContainsKey(l.Id)||!LobbyColorRules.ValidId(l.Id)||l.Id==LobbyColorRules.HostId)continue;
            // Supports stays false until this connection says HI; after HiTimeoutMs it counts as a peer without the part.
            _seats[l.Id]=new Seat{Id=l.Id,Connection=l.Connection,Peer=HostPeers.GetValue(l.Connection,_=>new LobbyColorRules.HostPeer()),Seen=now==0?1:now};
        }
        // A supporting seat without a row (its row was dropped with an earlier seat of the same connection object).
        foreach(var seat in _seats.Values)
            if(seat.Peer.Supports&&!LobbyColorRules.ValidColor(_table[seat.Id])){_table[seat.Id]=LobbyColorRules.JoinColor(_table,seat.Id,LobbyPalette.ClientDefault);_rev++;}
    }
    static void SendSet(Seat seat,long now)
    {
        // Validate the incarnation again right before sending and target only that completed connection.
        var connection=Connection(_session,seat.Id,true,out bool steam);
        if(!ReferenceEquals(connection,seat.Connection))return;
        var line=LobbyColorRules.Set(_rev,_table,seat.Peer.HandledSeq,seat.Peer.Nonce);
        if(steam)_sendSteam.Invoke(_session,new object[]{connection,line,null});
        else _sendTcp.Invoke(_session,new object[]{connection,line});
        seat.Peer.SentRev=_rev;seat.Peer.LastSent=now==0?1:now;
    }

    // ---- client ----
    static void ClientTick(long now)
    {
        if(!ClientLinks.TryGetValue(_session,out var link)){while(Dequeue(out _)){}return;}
        if(link.Epoch!=_clientEpoch)
        {
            // A new epoch (Steam's repeated WELCOME, a reconnect on this node): keep showing the last table until this
            // epoch's first SET or the HI timeout, so the lobby does not flicker; only the request in flight is void.
            _clientEpoch=link.Epoch;
            if(LobbyColorState.Pending>=0){LobbyColorState.Pending=-1;LobbyColorState.Revision++;}
        }
        int me=_session.id;
        bool identified=LobbyColorRules.ValidId(me)&&me!=LobbyColorRules.HostId;
        if(identified&&link.HiDue(now)){link.SentHi(now);_sendLineSafe.Invoke(_session,new object[]{LobbyColorRules.Hi(link.Nonce)});}
        if(link.GiveUpDue(now))
        {
            // No SET for this epoch: the host has no colour part (or it is off there). Defaults, as without the part.
            link.GaveUp=true;_table=LobbyColorRules.NewTable();LobbyColorState.Clear();UnsupportedNotice();
            LobbyCore.Info("Colour choice: the host did not answer; default colours");
        }
        while(Dequeue(out var inc))
        {
            if(!ReferenceEquals(inc.Net,_session)||!identified||!link.TakeSet(inc.Msg))continue;
            var m=inc.Msg;
            _table=m.Table;
            Publish(true);
            var answer=LobbyColorRules.Resolve(LobbyColorState.Pending,link.PendingSeq,m.Ack,_table,me);
            if(answer!=LobbyColorRules.Answer.None)
            {
                int asked=LobbyColorState.Pending;LobbyColorState.Pending=-1;LobbyColorState.Revision++;
                if(link.PendingExplicit)
                {
                    if(answer==LobbyColorRules.Answer.Granted)Save(asked);   // an explicit pick the host granted
                    else TakenNotice(asked,me);
                }
                link.PendingSeq=0;link.PendingSince=0;
            }
            if(!link.InitialSent)
            {
                // The host placed us on the client default or the next free colour; now ask for the saved pick.
                link.InitialSent=true;
                if(LobbyColorRules.NeedsInitialRequest(_preferred,_table[me]))SendRequest(link,_preferred,false,now);
            }
        }
        if(LobbyColorState.Pending>=0&&LobbyColorRules.Expired(now,link.PendingSince,PendingTimeoutMs))
        {LobbyColorState.Pending=-1;link.PendingSeq=0;link.PendingSince=0;LobbyColorState.Revision++;}
    }
    static void SendRequest(LobbyColorRules.ClientLink link,int index,bool explicitPick,long now)
    {
        if(link.Nonce==null||!link.Answered)return;
        int seq=link.NextSeq(now,explicitPick);
        LobbyColorState.Pending=index;LobbyColorState.Revision++;
        _sendLineSafe.Invoke(_session,new object[]{LobbyColorRules.Req(seq,index,link.Nonce)});
    }

    // ---- local pick (LobbyColorState.Request -> here; UI click, game thread) ----
    static void OnRequest(int index)
    {
        if(!_live||Faults.Tripped||_session==null||!LobbyColorRules.ValidColor(index))return;
        try
        {
            ReadLobby();
            if(!_inLobby||_inRun)return;              // picks only on the two lobby screens, frozen in a run
            long now=Environment.TickCount64;
            if(_sessionHost)
            {
                // The host's own pick goes through the same grant rule as a remote REQ, in order on this thread.
                int before=_table[LobbyColorRules.HostId];
                if(!LobbyColorRules.TryGrant(_table,LobbyColorRules.HostId,index)){TakenNotice(index,LobbyColorRules.HostId);return;}
                if(before!=index){_rev++;Publish(LobbyColorState.PeerSupports);}
                Save(index);
            }
            else
            {
                if(!ClientLinks.TryGetValue(_session,out var link)||!link.Answered)return;
                int me=_session.id;
                if(LobbyColorState.Pending==index)return;
                if(LobbyColorState.Pending<0&&LobbyColorRules.ValidId(me)&&_table[me]==index){Save(index);return;}   // already ours: just remember it
                SendRequest(link,index,true,now);      // saved when the host's SET grants it (ClientTick)
            }
        }
        catch(Exception e){if(Faults.Fail(e))SwitchOff("fault");}
    }
    // Only explicit picks are saved, once they are granted (map.md section 5, verify.md item 8); a colour the host
    // assigned on its own (client default, next free one) never is.
    static void Save(int index)
    {
        _preferred=index;
        try{var v=_config.Value;v.version=1;v.color=_shortKeys[index];_config.Save();}
        catch(Exception e){LobbyCore.Warn("Colour preference not saved: {Error}",e.Message);}
    }

    // ---- helpers ----
    // The connection object for an id (TCP or Steam), under the mod's own _clientsLock (as CSF's ElevatorConnection).
    static object Connection(NetNode net,int id,bool requireComplete,out bool steam)
    {
        steam=false;
        lock(_clientsLock.GetValue(net))
        {
            foreach(var (field,isSteam) in new[]{(_clients,false),(_steamClients,true)})
            {
                if(!(field.GetValue(net) is IDictionary peers)||!peers.Contains(id))continue;
                var value=peers[id];
                if(value==null||requireComplete&&!(bool)(isSteam?_steamComplete:_tcpComplete).GetValue(value))continue;
                steam=isSteam;return value;
            }
        }
        return null;
    }
    // CSF's RejectedConnections (Join.cs:18, private static ConditionalWeakTable<NetNode,object>). CSF may initialize
    // after us, so this is looked up again at every session start. Without CSF there is nothing to honour.
    static void LookUpCsf()
    {
        if(_csfRejected!=null)return;
        foreach(var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type csf;try{csf=asm.GetType("CoopSyncFixes",false);}catch{continue;}
            if(csf==null)continue;
            var f=csf.GetField("RejectedConnections",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public);
            if(f!=null&&f.FieldType==typeof(ConditionalWeakTable<NetNode,object>)){_csfRejected=(ConditionalWeakTable<NetNode,object>)f.GetValue(null);return;}
            if(!_csfLogged){_csfLogged=true;LobbyCore.Warn("CoopSyncFixes found without its RejectedConnections table; relying on its own HandleLine guard");}
            return;
        }
    }
}
