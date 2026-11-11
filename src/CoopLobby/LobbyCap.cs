using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DeadCellsMultiplayerMod;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using HarmonyLib;
using Steamworks;
using TitleScreen=dc.pr.TitleScreen;

// Part CAP (SPEC.md "Part CAP"; research wf-lobby2\player-cap map.md with verify.md corrections): two players in
// total, host + one client. Enforced by the HOST alone, so clients without this add-on are refused too:
//  - TCP: prefix on NetNode.SendLineToClientSafe decides at the first WELCOME of a connection (AcceptLoop sends
//    WELCOME, ID and the cached run state before it reads HELLO, release IL 166891-168200). A refused connection
//    gets one reject WELCOME and nothing after it (SendLineToClientSafe is the only host write path to a TCP
//    client, verify.md: broadcast 168447, forward 20281 and CSF SendWireFanout all go through it).
//  - Steam: prefix on NetNode.TryGetOrRegisterSteamClient (only caller: the bridge loop, IL 172906, which skips the
//    packet on false, IL 172907): an unknown SteamID while the seat is taken is never registered, so it gets no
//    id, no WELCOME/ID/run state and is never in the direct Steam broadcast fan-out (IL 168354-168369); it gets the
//    reject line through NetNode.SendLineToSteamBridgeSafe (not patched by CSF, verify.md missed #4).
//  - prefix on NetNode.HandleLine drops every line of a refused sender (its 0.5 s lobby heartbeat would rename
//    the host's partner and be forwarded, verify.md missed #1), and refuses late, with the reject line, a peer that
//    sends HELLO while another player is handshaken (a stale pending occupant that finished late, or a TCP gate that
//    never ran). That decision is taken before the HELLO branch, because the branch resends the full initial state
//    to a Steam peer after CompleteHostHandshake (IL 10118-10121). A late-refused Steam peer is unregistered about
//    2 s later through the mod's own CleanupHostSteamClient. A prefix on CompleteHostHandshake keeps refused peers
//    out and is the backstop for the late rule.
//  - client side (only with this add-on): prefix on LobbySession.NotifyProtocolMismatch turns the reject into the
//    localized "Lobby is full" popup. A 0.5.1 client without it shows the mod's version-mismatch popup, whose
//    detail line prints our reason (LobbyCapRules.RejectLine).
// Not done on purpose: closing sockets (the client closes after reading the reject; a host EOF first would let the
// client's critical disconnect job null NetRef and drop the reject line, verify.md #13), SetLobbyMemberLimit
// (breaks code joins and CoopSteamInviteFix invites, verify.md missed #2), ClientIds/MaxClientSlots/arrays,
// handshake formats, TryCreateLobbyForP2PHost, OpenSteamHostInviteOverlay, rich presence.
public static partial class LobbyCap
{
    public const string Id="CoopLobby.Cap.20261110";
    // Our HandleLine / SendLineToClientSafe prefixes sort before these owners (HarmonyMethod.before). Checked offline
    // on HarmonyX 2.16.1 with a dummy class (notes\cap\harmony-order.ps1): `before` puts a Priority.First prefix ahead
    // of another owner's Priority.First prefix registered earlier, and prefixes AFTER one that returned false still
    // run (only the original is skipped). Every place below that returns false is written for that.
    const string CsfId="local.deadcells.syncfixes.1", ColorId="CoopLobby.Color.20261110";
    static readonly FaultGate Gate=new("LobbyCap");
    static readonly object InstallSync=new();
    static volatile bool Installed;

    // Host notice (main thread). The mod shows system toasts only from MultiplayerUI.IOnHeroUpdate.OnHeroUpdate
    // (release IL 88092-88095) under HUD.ME.root (IL 87668-87681), so a toast pushed on the title screen would
    // surface at the start of the next run: CAP toasts only during a run. In the lobby the notice goes through the
    // core's one-shot hint-line text LobbyColorState.LastNotice, which the UI part reads every frame while a lobby
    // layer is up (LobbyUiView.Tick) and shows for 3 s; it is withdrawn if no layer takes it within
    // LobbyCapRules.LobbyNoticeKeepSeconds. Without the UI part the lobby case is a log line only. Notice /
    // NoticeRevision stay public for any other reader (by reflection: partial builds may lack this type).
    public static string Notice{get;private set;}
    public static int NoticeRevision{get;private set;}
    public static long RefusedPeers=>Interlocked.Read(ref _refused);
    public static long DroppedLines=>Interlocked.Read(ref _dropped);

    // ---- bound members (all verified against the pinned 0.5.1 DLL at install) ----
    static FieldInfo _clientsLock,_clients,_steamClients,_steamIds,_waitingMenu,_msgSync,_msgQueue,_msgText;
    static PropertyInfo _tcpComplete,_steamComplete,_steamLastPacket,_steamIdOf;
    static MethodInfo _sendTcp,_mismatch,_queueClear,_queueEnqueue,_cleanupSteam;
    static Func<string> _buildWelcome;
    static Func<NetNode,bool> _isCurrent;
    static Func<NetNode,ulong,string,EP2PSend,int,Task> _sendBridge;
    static int _hostToClient;
    static Func<TitleScreen> _titleScreen;
    static Action<TitleScreen,string,string,Action> _errorPopup;
    static Action<TitleScreen> _joinMenu;
    static Func<bool> _inRun;
    static Func<string,string> _localize;

    // NetNode's lock and peer tables are readonly fields set in its constructor (FieldInfo.IsInitOnly checked at
    // install), so they are read once per node. Dictionaries with the private connection types go through IDictionary.
    sealed class NodeRefs{public object Lock;public IDictionary Tcp,Steam;public Dictionary<ulong,int> SteamIds;}
    static readonly ConditionalWeakTable<NetNode,NodeRefs> Nodes=new();
    static NodeRefs Refs(NetNode n)=>Nodes.GetValue(n,k=>new NodeRefs{Lock=_clientsLock.GetValue(k),Tcp=(IDictionary)_clients.GetValue(k),
        Steam=(IDictionary)_steamClients.GetValue(k),SteamIds=(Dictionary<ulong,int>)_steamIds.GetValue(k)});
    static long Ticks(double seconds)=>LobbyCapRules.Ticks(seconds,Stopwatch.Frequency);

    public static void Install()
    {
        lock(InstallSync)
        {
            if(Installed)return;
            var harmony=new Harmony(Id);var patched=new List<MethodBase>();
            try
            {
                // The TCP gate has to see the first WELCOME of every connection, so nothing may be connected yet
                // (CSF refuses the same way, CinematicPresentation.cs:40).
                if(ModEntry._net!=null&&ModEntry._net.IsAlive)throw new InvalidOperationException("CAP must install before any network session");
                var targets=Bind();
                SelfTest();
                foreach(var (target,patch,post) in targets)
                {
                    patched.Add(target);     // before Patch: a throw half way is still rolled back (Unpatch by owner)
                    if(post)harmony.Patch(target,postfix:patch);else harmony.Patch(target,prefix:patch);
                }
                foreach(var (target,_,post) in targets)
                {
                    var info=Harmony.GetPatchInfo(target);
                    int n=info==null?0:(post?info.Postfixes:info.Prefixes).Count(p=>p.owner==Id);
                    if(n!=1)throw new InvalidOperationException("CAP patch not installed exactly once: "+target.Name);
                }
                Installed=true;
                LobbyCore.Info("CAP: two-player cap active; prefixes on HandleLine: {Owners}; on SendLineToClientSafe: {Send}",
                    Owners(targets[2].Target),Owners(targets[0].Target));
            }
            catch
            {
                foreach(var t in patched)try{harmony.Unpatch(t,HarmonyPatchType.All,Id);}catch(Exception r){LobbyCore.Warn("CAP rollback of {Target} failed: {Error}",t.Name,r.Message);}
                throw;
            }
        }
    }
    static string Owners(MethodBase m){var i=Harmony.GetPatchInfo(m);return i==null?"":string.Join(",",i.Prefixes.Select(p=>p.owner+"/"+p.priority));}

    static List<(MethodBase Target,HarmonyMethod Patch,bool Post)> Bind()
    {
        var node=typeof(NetNode);
        // Both connection classes are private nested types of NetNode (reflection on the pinned DLL,
        // notes\cap\sig-cap.ps1): patches take them as object, members are read by reflection.
        var tcp=node.GetNestedType("ClientConnection",BindingFlags.NonPublic)??throw new MissingMemberException("NetNode","ClientConnection");
        var steam=node.GetNestedType("SteamClientConnection",BindingFlags.NonPublic)??throw new MissingMemberException("NetNode","SteamClientConnection");
        var send=LobbyCore.Method(node,"SendLineToClientSafe",false,typeof(Task),tcp,typeof(string));
        var register=LobbyCore.Method(node,"TryGetOrRegisterSteamClient",false,typeof(bool),typeof(CSteamID),steam.MakeByRefType());
        var handle=LobbyCore.Method(node,"HandleLine",false,typeof(bool),typeof(string),typeof(int?),typeof(string).MakeByRefType());
        var complete=LobbyCore.Method(node,"CompleteHostHandshake",false,typeof(void),typeof(int));
        Names(send,"client","line");Names(handle,"line","senderId","forwardLine");     // bound by name below
        _sendBridge=LobbyCore.Delegate<Func<NetNode,ulong,string,EP2PSend,int,Task>>(LobbyCore.Method(node,"SendLineToSteamBridgeSafe",false,typeof(Task),typeof(ulong),typeof(string),typeof(EP2PSend),typeof(int)));
        _buildWelcome=LobbyCore.Delegate<Func<string>>(LobbyCore.Method(node,"BuildWelcomeLine",true,typeof(string)));
        _isCurrent=LobbyCore.Delegate<Func<NetNode,bool>>(LobbyCore.Method(node,"IsCurrentNetworkSession",false,typeof(bool)));
        // Host -> client Steam channel: SendLineToSteamClientSafe passes ldc.i4.1 (IL 18789); the const must agree.
        var channel=node.GetField("SteamP2PChannelHostToClient",LobbyCore.S);
        if(channel==null||!channel.IsLiteral||channel.FieldType!=typeof(int)||(int)channel.GetRawConstantValue()!=1)throw new InvalidOperationException("Pinned Steam channel mismatch");
        _hostToClient=1;
        _clientsLock=LobbyCore.Field(node,"_clientsLock",typeof(object),false);
        _clients=LobbyCore.Field(node,"_clients",typeof(Dictionary<,>).MakeGenericType(typeof(int),tcp),false);
        _steamClients=LobbyCore.Field(node,"_steamClients",typeof(Dictionary<,>).MakeGenericType(typeof(int),steam),false);
        _steamIds=LobbyCore.Field(node,"_steamClientIdsBySteam",typeof(Dictionary<ulong,int>),false);
        foreach(var f in new[]{_clientsLock,_clients,_steamClients,_steamIds})if(!f.IsInitOnly)throw new InvalidOperationException("Pinned NetNode table is not readonly: "+f.Name);
        _tcpComplete=Prop(tcp,"HandshakeComplete",typeof(bool));
        _steamComplete=Prop(steam,"HandshakeComplete",typeof(bool));
        _steamLastPacket=Prop(steam,"LastPacketReceivedTicks",typeof(long));    // Stopwatch ticks (ctor + MarkPacketReceived, IL 166167/166178)
        _steamIdOf=Prop(steam,"SteamId",typeof(CSteamID));
        _sendTcp=send;
        // Called (MethodInfo.Invoke: the parameter is the private connection type) to unregister a late-refused Steam
        // peer; private instance void (SteamClientConnection sender), IL 19869.
        _cleanupSteam=LobbyCore.Method(node,"CleanupHostSteamClient",false,typeof(void),steam);

        var lobby=LobbyCore.ModType("DeadCellsMultiplayerMod.LobbySession");
        _mismatch=LobbyCore.Method(lobby,"NotifyProtocolMismatch",true,typeof(void),typeof(string),typeof(int),typeof(string),typeof(int),typeof(NetRole));
        Names(_mismatch,"remoteBuild","remoteProtocol","localBuild","localProtocol","localRole");
        _errorPopup=LobbyCore.Delegate<Action<TitleScreen,string,string,Action>>(LobbyCore.Method(lobby,"ShowConnectionErrorPopup",true,typeof(void),typeof(TitleScreen),typeof(string),typeof(string),typeof(Action)));
        _joinMenu=LobbyCore.Delegate<Action<TitleScreen>>(LobbyCore.Method(lobby,"ShowJoinTransportMenu",true,typeof(void),typeof(TitleScreen)));
        _titleScreen=LobbyCore.Delegate<Func<TitleScreen>>(LobbyCore.Method(lobby,"GetTitleScreen",true,typeof(TitleScreen)));
        _inRun=LobbyCore.Delegate<Func<bool>>(LobbyCore.Method(lobby,"IsInActualRun",true,typeof(bool)));
        _localize=LobbyCore.Delegate<Func<string,string>>(LobbyCore.Method(lobby,"Localize",true,typeof(string),typeof(string)));
        _waitingMenu=LobbyCore.Field(lobby,"_inClientWaitingMenu",typeof(bool),true);

        var ui=typeof(MultiplayerUI);
        LobbyCore.Method(ui,"PushSystemMessage",true,typeof(void),typeof(string),typeof(double),typeof(double));   // called directly
        _msgSync=LobbyCore.Field(ui,"SystemMsgSync",typeof(object),true);
        var pending=ui.GetNestedType("PendingSystemMessage",BindingFlags.NonPublic)??throw new MissingMemberException("MultiplayerUI","PendingSystemMessage");
        _msgQueue=LobbyCore.Field(ui,"PendingSystemMessages",typeof(Queue<>).MakeGenericType(pending),true);
        _msgText=LobbyCore.Field(pending,"Text",typeof(string),false);
        _queueClear=_msgQueue.FieldType.GetMethod("Clear",Type.EmptyTypes)??throw new MissingMethodException("Queue","Clear");
        _queueEnqueue=_msgQueue.FieldType.GetMethod("Enqueue",new[]{pending})??throw new MissingMethodException("Queue","Enqueue");

        var frame=LobbyCore.Method(typeof(ModEntry),"OnFrameUpdate",false,typeof(void),typeof(double));
        return new()
        {
            (send,Pre(nameof(BeforeSendToClient),CsfId,ColorId),false),
            (register,Pre(nameof(BeforeRegisterSteam)),false),
            (handle,Pre(nameof(BeforeHandleLine),CsfId,ColorId),false),
            (complete,Pre(nameof(BeforeCompleteHandshake)),false),
            (_mismatch,Pre(nameof(BeforeProtocolMismatch)),false),
            (frame,new HarmonyMethod(Own(nameof(AfterFrame))){priority=Priority.Normal},true),
        };
    }
    static MethodInfo Own(string name)=>typeof(LobbyCap).GetMethod(name,LobbyCore.S)??throw new MissingMethodException("LobbyCap",name);
    static HarmonyMethod Pre(string name,params string[] before)=>new(Own(name)){priority=Priority.First,before=before.Length==0?null:before};
    static PropertyInfo Prop(Type t,string name,Type type)
    {
        var p=t.GetProperty(name,LobbyCore.I);
        if(p==null||p.PropertyType!=type||p.GetMethod==null)throw new InvalidOperationException("Pinned property mismatch: "+t.Name+"."+name);
        return p;
    }
    static void Names(MethodInfo m,params string[] names)
    {
        var ps=m.GetParameters();
        if(ps.Length!=names.Length)throw new InvalidOperationException("Pinned parameters changed: "+m.Name);
        for(int i=0;i<names.Length;i++)if(ps[i].Name!=names[i])throw new InvalidOperationException("Pinned parameter name changed: "+m.Name+"."+names[i]);
    }
    // The reject line built from the live WELCOME keeps exactly 3 fields. BuildWelcomeLine is a pure string builder
    // (IL 4544-4571) plus CSF's string-append postfixes, so calling it here touches no game state. (LobbyText is
    // not called here: it reads the game's language class, which is a main-thread runtime read, not an install one.)
    static void SelfTest()
    {
        var line=LobbyCapRules.RejectLine(_buildWelcome());
        var parts=line.TrimEnd('\n').Split('|');
        if(parts.Length!=3||parts[0]!="WELCOME"||parts[1]!="0"||!parts[2].StartsWith(LobbyCapRules.Reason,StringComparison.Ordinal)||line.IndexOf('\n')!=line.Length-1)
            throw new InvalidOperationException("CAP reject line self-test failed");
    }
}
