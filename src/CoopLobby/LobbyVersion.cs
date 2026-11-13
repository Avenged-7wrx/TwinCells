using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using DeadCellsMultiplayerMod;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using HarmonyLib;
using TitleScreen=dc.pr.TitleScreen;

// Part VERSION of CoopLobby: the Twin Cells Alpha2 version gate (rules and the reason: LobbyVersionRules.cs header).
// Installed by CoopLobby.Initialize after UI, CAP and COLOR (CoopLobby.cs part table); kill switch COOP_LOBBY_VERSION_DISABLE=1
// (or COOP_LOBBY_DISABLE=1): then no token is advertised and none is required, and this peer looks like an Alpha1 peer on
// the wire (an Alpha2 partner refuses it with the message below).
// Patches (Harmony owner CoopLobby.Version.20261112, all bodies behind try/catch and the part's fault gate):
//  - NetNode.BuildHelloLine / BuildWelcomeLine: postfix Priority.First, appends the token after the base build.
//  - NetNode.TryValidateHandshakeLine (static, the HELLO/WELCOME validator RejectIncompatiblePeer calls): prefix
//    Priority.First strips the token and records whether it was there; prefix Priority.Last, after CSF's validators,
//    refuses a line without it (LobbyVersionRules.Require). Both run on the thread that runs RejectIncompatiblePeer, so the
//    record is [ThreadStatic] and is consumed by the next call of the Last prefix.
//  - LobbySession.NotifyProtocolMismatch (called by RejectIncompatiblePeer right after a refused validation, same
//    thread): prefix Priority.First before CAP's, for our refusal only: on the client the mod's generic "Co-op version
//    mismatch" toast and popup are replaced by our popup (shown from the frame postfix once the mod's disconnect path
//    cleared the waiting menu, as CAP does, LobbyCapRules.Popup); on the host by our notice (hint line in the lobby,
//    system message in a run) plus the mod's ConnectionUI.NotifyConnectionsChanged.
//  - ModEntry.OnFrameUpdate: postfix Priority.Normal (popup and notice ticks, main thread).
//  - TryHandleFastPathLine / CleanupClient / Dispose: observe one raw received WELCOME, mark its cleanup/cancellation.
//    The pinned network pump queues work, not its completion; critical disconnect can clear NetRef before the WELCOME
//    callback executes. Recover only that received version evidence on the frame thread after native cleanup. These
//    hooks never alter a line, a result, the network queue, or the existing handshake validation/disconnect behavior.
// Nothing else is changed: the refused peer leaves through the mod's own reject path (RejectIncompatiblePeer true ->
// HandleLine false), exactly as for any other refused handshake.
public static class LobbyVersion
{
    public const string Id="CoopLobby.Version.20261112";
    const string CsfId="local.deadcells.syncfixes.1",CapId="CoopLobby.Cap.20261110";
    static readonly FaultGate Gate=new("LobbyVersion");
    static readonly object InstallSync=new();
    static volatile bool Installed;
    static long _refused,_faults;
    public static long Refused=>Interlocked.Read(ref _refused);

    // Per validation call (RejectIncompatiblePeer -> TryValidateHandshakeLine -> NotifyProtocolMismatch, one thread).
    [ThreadStatic] static LobbyVersionRules.Seen _seen;
    [ThreadStatic] static string _raw;
    [ThreadStatic] static string _refusedBuild;
    [ThreadStatic] static int _refusedProtocol;

    // Main thread (NotifyProtocolMismatch and the frame postfix both run on it).
    static long _popupSince,_sweepUntil,_noticeAt;
    static bool _swept,_bypass;
    static object[] _mismatchArgs;
    static NetNode _popupNet;
    static NetNode _mismatchNet;
    static string _notice;
    static readonly ConditionalWeakTable<NetNode,LobbyVersionRules.ReceivedWelcome> ReceivedWelcomes=new();
    static int _receiptWork,_receiptFaulted;

    static MethodInfo _mismatch,_queueClear,_queueEnqueue;
    static FieldInfo _waitingMenu,_msgSync,_msgQueue,_msgText;
    static Func<string> _buildHello,_buildWelcome;
    static Func<TitleScreen> _titleScreen;
    static Action<TitleScreen,string,string,Action> _errorPopup;
    static Action<TitleScreen> _joinMenu;
    static Func<bool> _inRun;
    static Func<NetNode> _netRef;
    static string _localBuild;
    static int _localProtocol;
    static Func<string,string> _localize;
    static Action _connectionsChanged;

    public static void Install()
    {
        lock(InstallSync)
        {
            if(Installed)return;
            var harmony=new Harmony(Id);var patched=new List<MethodBase>();
            try
            {
                if(ModEntry._net!=null&&ModEntry._net.IsAlive)throw new InvalidOperationException("VERSION must install before any network session");
                var targets=Bind();
                SelfTest();
                foreach(var (target,patch,post) in targets)
                {
                    patched.Add(target);
                    if(post)harmony.Patch(target,postfix:patch);else harmony.Patch(target,prefix:patch);
                }
                foreach(var g in targets.GroupBy(t=>(t.Target,t.Post)))
                {
                    var info=Harmony.GetPatchInfo(g.Key.Target);
                    int n=info==null?0:(g.Key.Post?info.Postfixes:info.Prefixes).Count(p=>p.owner==Id);
                    if(n!=g.Count())throw new InvalidOperationException("VERSION patch not installed exactly as planned: "+g.Key.Target.Name);
                }
                Installed=true;
                LobbyCore.Info("VERSION: Twin Cells Alpha2 gate active: token {Token}; validators on TryValidateHandshakeLine: {Owners}",LobbyVersionRules.Token,Owners(targets[2].Target));
            }
            catch
            {
                foreach(var t in patched)try{harmony.Unpatch(t,HarmonyPatchType.All,Id);}catch(Exception r){LobbyCore.Warn("VERSION rollback of {Target} failed: {Error}",t.Name,r.Message);}
                throw;
            }
        }
    }
    static string Owners(MethodBase m){var i=Harmony.GetPatchInfo(m);return i==null?"":string.Join(",",i.Prefixes.Select(p=>p.owner+"/"+p.priority));}

    static List<(MethodBase Target,HarmonyMethod Patch,bool Post)> Bind()
    {
        var node=typeof(NetNode);
        var hello=LobbyCore.Method(node,"BuildHelloLine",true,typeof(string));
        var welcome=LobbyCore.Method(node,"BuildWelcomeLine",true,typeof(string));
        var validate=LobbyCore.Method(node,"TryValidateHandshakeLine",true,typeof(bool),typeof(string),typeof(string),typeof(int).MakeByRefType(),typeof(string).MakeByRefType(),typeof(string).MakeByRefType());
        Names(validate,"line","expectedTag","remoteProtocol","remoteBuild","failure");
        _buildHello=LobbyCore.Delegate<Func<string>>(hello);_buildWelcome=LobbyCore.Delegate<Func<string>>(welcome);
        var lobby=LobbyCore.ModType("DeadCellsMultiplayerMod.LobbySession");
        _mismatch=LobbyCore.Method(lobby,"NotifyProtocolMismatch",true,typeof(void),typeof(string),typeof(int),typeof(string),typeof(int),typeof(NetRole));
        Names(_mismatch,"remoteBuild","remoteProtocol","localBuild","localProtocol","localRole");
        _errorPopup=LobbyCore.Delegate<Action<TitleScreen,string,string,Action>>(LobbyCore.Method(lobby,"ShowConnectionErrorPopup",true,typeof(void),typeof(TitleScreen),typeof(string),typeof(string),typeof(Action)));
        _joinMenu=LobbyCore.Delegate<Action<TitleScreen>>(LobbyCore.Method(lobby,"ShowJoinTransportMenu",true,typeof(void),typeof(TitleScreen)));
        _titleScreen=LobbyCore.Delegate<Func<TitleScreen>>(LobbyCore.Method(lobby,"GetTitleScreen",true,typeof(TitleScreen)));
        _inRun=LobbyCore.Delegate<Func<bool>>(LobbyCore.Method(lobby,"IsInActualRun",true,typeof(bool)));
        _netRef=LobbyCore.Delegate<Func<NetNode>>(LobbyCore.Method(lobby,"get_NetRef",true,typeof(NetNode)));
        _localize=LobbyCore.Delegate<Func<string,string>>(LobbyCore.Method(lobby,"Localize",true,typeof(string),typeof(string)));
        _waitingMenu=LobbyCore.Field(lobby,"_inClientWaitingMenu",typeof(bool),true);
        var connection=LobbyCore.ModType("DeadCellsMultiplayerMod.MultiplayerModUI.Connection.ConnectionUI");
        _connectionsChanged=LobbyCore.Delegate<Action>(LobbyCore.Method(connection,"NotifyConnectionsChanged",true,typeof(void)));
        var ui=typeof(MultiplayerUI);
        LobbyCore.Method(ui,"PushSystemMessage",true,typeof(void),typeof(string),typeof(double),typeof(double));   // called directly
        _msgSync=LobbyCore.Field(ui,"SystemMsgSync",typeof(object),true);
        var pending=ui.GetNestedType("PendingSystemMessage",BindingFlags.NonPublic)??throw new MissingMemberException("MultiplayerUI","PendingSystemMessage");
        _msgQueue=LobbyCore.Field(ui,"PendingSystemMessages",typeof(Queue<>).MakeGenericType(pending),true);
        _msgText=LobbyCore.Field(pending,"Text",typeof(string),false);
        _queueClear=_msgQueue.FieldType.GetMethod("Clear",Type.EmptyTypes)??throw new MissingMethodException("Queue","Clear");
        _queueEnqueue=_msgQueue.FieldType.GetMethod("Enqueue",new[]{pending})??throw new MissingMethodException("Queue","Enqueue");
        var frame=LobbyCore.Method(typeof(ModEntry),"OnFrameUpdate",false,typeof(void),typeof(double));
        var receive=LobbyCore.Method(node,"TryHandleFastPathLine",false,typeof(bool),typeof(string),typeof(int?));
        Names(receive,"line","senderId");
        var cleanup=LobbyCore.Method(node,"CleanupClient",false,typeof(void));
        var dispose=LobbyCore.Method(node,"Dispose",false,typeof(void));
        return new()
        {
            (hello,new HarmonyMethod(Own(nameof(Advertise))){priority=Priority.First},true),
            (welcome,new HarmonyMethod(Own(nameof(Advertise))){priority=Priority.First},true),
            (validate,new HarmonyMethod(Own(nameof(StripToken))){priority=Priority.First},false),
            (validate,new HarmonyMethod(Own(nameof(RequireToken))){priority=Priority.Last,after=new[]{CsfId}},false),
            (_mismatch,new HarmonyMethod(Own(nameof(BeforeProtocolMismatch))){priority=Priority.First,before=new[]{CapId}},false),
            (frame,new HarmonyMethod(Own(nameof(AfterFrame))){priority=Priority.Normal},true),
            (receive,new HarmonyMethod(Own(nameof(ObserveReceivedWelcome))){priority=Priority.First},false),
            (cleanup,new HarmonyMethod(Own(nameof(AfterClientCleanup))){priority=Priority.Last},true),
            (dispose,new HarmonyMethod(Own(nameof(BeforeNodeDispose))){priority=Priority.First},false),
        };
    }
    static MethodInfo Own(string name)=>typeof(LobbyVersion).GetMethod(name,LobbyCore.S)??throw new MissingMethodException("LobbyVersion",name);
    static void Names(MethodInfo m,params string[] names)
    {
        var ps=m.GetParameters();
        if(ps.Length!=names.Length)throw new InvalidOperationException("Pinned parameters changed: "+m.Name);
        for(int i=0;i<names.Length;i++)if(ps[i].Name!=names[i])throw new InvalidOperationException("Pinned parameter name changed: "+m.Name+"."+names[i]);
    }
    // The base lines are pure string builders (CAP SelfTest does the same with BuildWelcomeLine); at install time no other
    // part has patched them yet (CoopLobby initializes before CoopSyncFixes). Our advertise/strip must round-trip exactly.
    static void SelfTest()
    {
        foreach(var build in new[]{_buildHello,_buildWelcome})
        {
            var line=build();var with=LobbyVersionRules.Advertise(line);var probe=with;
            if(!LobbyVersionRules.HasToken(with)||LobbyVersionRules.Strip(ref probe,out _)!=LobbyVersionRules.Seen.Present||probe!=line.TrimEnd('\r','\n'))
                throw new InvalidOperationException("VERSION token self-test failed");
        }
        var hello=_buildHello().TrimEnd('\r','\n').Split('|');
        if(hello.Length!=3||hello[0]!="HELLO"||hello[1]!="19")throw new InvalidOperationException("VERSION local handshake shape changed");
        _localBuild=hello[2];_localProtocol=19;
    }

    static void Fault(Exception e){Interlocked.Increment(ref _faults);Gate.Fail(e);}

    // ---- handshake (any thread that runs RejectIncompatiblePeer) ----
    static void Advertise(ref string __result)
    {
        if(!Installed||Gate.Tripped)return;
        try{__result=LobbyVersionRules.Advertise(__result);}catch(Exception e){Fault(e);}
    }
    static void StripToken(ref string line)
    {
        _seen=LobbyVersionRules.Seen.None;_raw=null;
        if(!Installed||Gate.Tripped)return;
        try{var l=line;var seen=LobbyVersionRules.Strip(ref l,out var raw);line=l;_raw=raw;_seen=seen;}
        catch(Exception e){_seen=LobbyVersionRules.Seen.None;Fault(e);}
    }
    static bool RequireToken(ref string line,ref int remoteProtocol,ref string remoteBuild,ref string failure,ref bool __result)
    {
        var seen=_seen;var raw=_raw;_seen=LobbyVersionRules.Seen.None;_raw=null;_refusedBuild=null;
        if(!Installed||Gate.Tripped)return true;
        try
        {
            var go=LobbyVersionRules.Require(seen,raw,ref line,ref remoteProtocol,ref remoteBuild,ref failure,ref __result,out var refused);
            if(refused)
            {
                Interlocked.Increment(ref _refused);
                _refusedBuild=remoteBuild;_refusedProtocol=remoteProtocol;
                LobbyCore.Warn("VERSION: refused a peer without {Token}: build field {Build} (protocol {Protocol})",LobbyVersionRules.Token,remoteBuild,remoteProtocol);
            }
            return go;
        }
        catch(Exception e){Fault(e);return true;}
    }

    // ---- our refusal reaches the UI (main thread, inside HandleLine) ----
    static bool BeforeProtocolMismatch(string remoteBuild,int remoteProtocol,string localBuild,int localProtocol,NetRole localRole)
    {
        // Any native mismatch owns its UI (including CAP and CSF). Do not recover a second popup after its cleanup.
        var net=ModEntry._net;
        if(localRole==NetRole.Client&&net!=null&&ReceivedWelcomes.TryGetValue(net,out var receipt))receipt.MarkHandled();
        if(_bypass)return true;
        var mine=_refusedBuild;_refusedBuild=null;
        try
        {
            if(!Installed||Gate.Tripped||mine==null||!string.Equals(mine,remoteBuild,StringComparison.Ordinal)||remoteProtocol!=_refusedProtocol)return true;
            if(localRole==NetRole.Client)
            {
                QueueVersionPopup(net,remoteBuild,remoteProtocol,localBuild,localProtocol);
                return false;
            }
            var text=LobbyVersionRules.HostNotice(LobbyText.Russian);
            if(_inRun())MultiplayerUI.PushSystemMessage(text,8.0,1.5);
            else if(LobbyColorState.LastNotice==null){LobbyColorState.Post(text);_notice=text;_noticeAt=Stopwatch.GetTimestamp();}
            try{_connectionsChanged();}catch(Exception e){LobbyCore.Warn("VERSION: NotifyConnectionsChanged failed: {Error}",e.Message);}
            LobbyCore.Info("VERSION: refused a client with another Twin Cells version ({Build}); notice {Where}",remoteBuild,_inRun()?"in the run":"on the lobby hint line");
            return false;
        }
        catch(Exception e){Fault(e);return true;}
    }

    // ---- receive receipts (no game/UI/logging on the receive or cleanup threads) ----
    static void ReceiptFault(){Volatile.Write(ref _receiptFaulted,1);Volatile.Write(ref _receiptWork,1);}
    static void ObserveReceivedWelcome(NetNode __instance,string line)
    {
        if(!Installed||Gate.Tripped||Volatile.Read(ref _receiptFaulted)!=0)return;
        try
        {
            if(__instance==null||__instance.IsHost||line==null||!line.StartsWith("WELCOME|",StringComparison.Ordinal))return;
            ReceivedWelcomes.GetValue(__instance,static _=>new LobbyVersionRules.ReceivedWelcome()).Observe(line);
        }
        catch{ReceiptFault();}
    }
    static void AfterClientCleanup(NetNode __instance)
    {
        if(!Installed||Gate.Tripped||Volatile.Read(ref _receiptFaulted)!=0)return;
        try
        {
            if(__instance!=null&&ReceivedWelcomes.TryGetValue(__instance,out var r)&&r.Cleaned(Stopwatch.GetTimestamp()))Volatile.Write(ref _receiptWork,1);
        }
        catch{ReceiptFault();}
    }
    static void BeforeNodeDispose(NetNode __instance)
    {
        try{if(__instance!=null&&ReceivedWelcomes.TryGetValue(__instance,out var r))r.Cancel();}
        catch{ReceiptFault();}
    }
    static void TickReceivedWelcome()
    {
        if(Volatile.Read(ref _receiptWork)==0)return; // Ordinary frames: one atomic read, no lookup/lock/allocation.
        Interlocked.Exchange(ref _receiptWork,0);
        if(Volatile.Read(ref _receiptFaulted)!=0){LobbyCore.Warn("VERSION: received-WELCOME recovery disabled after a receipt fault");return;}
        var net=ModEntry._net;
        if(net==null||net.IsHost||!ReceivedWelcomes.TryGetValue(net,out var r))return;
        if(r.Expire(Stopwatch.GetTimestamp(),Ticks(10)))
        {LobbyCore.Warn("VERSION: received-WELCOME recovery expired before native disconnect completed");return;}
        var active=_netRef();
        var context=LobbyVersionRules.RecoveryContext(net,ModEntry._net,active,_inRun(),net.HasRemote);
        if(context==LobbyVersionRules.ReceiptContext.Drop){r.MarkHandled();return;}
        // CleanupClient queues NotifyRemoteDisconnected. Wait for that native action to clear NetRef; Steam IsAlive
        // is deliberately not used here (a closed Steam peer can retain an uncancelled transport CTS).
        if(context==LobbyVersionRules.ReceiptContext.WaitForNativeDisconnect){Volatile.Write(ref _receiptWork,1);return;}
        if(!r.Take(out var build,out var protocol))return;
        if(_popupSince!=0||LobbyShared.PopupPending)return;
        Interlocked.Increment(ref _refused);
        LobbyCore.Warn("VERSION: recovered a received WELCOME without {Token} after early disconnect: build field {Build} (protocol {Protocol})",LobbyVersionRules.Token,build,protocol);
        // Base values were captured from the unpatched pinned builder at install, before any network session.
        QueueVersionPopup(net,build,protocol,_localBuild,_localProtocol);
    }
    static void QueueVersionPopup(NetNode net,string remoteBuild,int remoteProtocol,string localBuild,int localProtocol)
    {
        _mismatchArgs=new object[]{remoteBuild,remoteProtocol,localBuild,localProtocol,NetRole.Client};
        _mismatchNet=net;
        _popupSince=Stopwatch.GetTimestamp();_popupNet=net;_sweepUntil=0;_swept=false;
        LobbyShared.PopupPending=true;
        LobbyCore.Info("VERSION: the host has another Twin Cells version ({Build}); showing the version popup",remoteBuild);
    }

    static long Ticks(double seconds)=>LobbyCapRules.Ticks(seconds,Stopwatch.Frequency);
    static void AfterFrame()
    {
        if(!Installed)return;
        try
        {
            if(Gate.Tripped){WithdrawNotice(true);if(_popupSince!=0){EndPopup();FallbackMismatch();}return;}
            WithdrawNotice(false);
            TickReceivedWelcome();
            TickClientPopup();
        }
        catch(Exception e){Fault(e);if(Gate.Tripped&&_popupSince!=0){EndPopup();FallbackMismatch();}}
    }
    static void EndPopup(){_popupSince=0;_popupNet=null;LobbyShared.PopupPending=false;}
    static void WithdrawNotice(bool now)
    {
        if(_notice==null)return;
        if(!now&&Stopwatch.GetTimestamp()-_noticeAt<Ticks(LobbyCapRules.LobbyNoticeKeepSeconds))return;
        if(ReferenceEquals(LobbyColorState.LastNotice,_notice))LobbyColorState.LastNotice=null;
        _notice=null;
    }
    static void TickClientPopup()
    {
        long now=Stopwatch.GetTimestamp();
        if(_sweepUntil!=0)
        {
            if(now>=_sweepUntil)_sweepUntil=0;
            else if(!_swept)_swept=SweepDisconnectToast();
        }
        if(_popupSince==0)return;
        var net=ModEntry._net;
        if(!PopupOwnerCurrent(_popupNet))
        {EndPopup();_mismatchArgs=null;_mismatchNet=null;LobbyCore.Info("VERSION: version popup dropped, the connection changed or was cancelled");return;}
        var screen=_titleScreen();
        bool waiting=(bool)_waitingMenu.GetValue(null);
        switch(LobbyCapRules.Popup(_popupSince,now,waiting,screen!=null,Ticks(LobbyCapRules.PopupSettleSeconds),Ticks(LobbyCapRules.PopupFallbackSeconds),Ticks(LobbyCapRules.PopupGiveUpSeconds)))
        {
            case LobbyCapRules.PopupStep.GiveUp:
                EndPopup();LobbyCore.Warn("VERSION: no title screen for the version popup; using the mod's mismatch path");FallbackMismatch();return;
            case LobbyCapRules.PopupStep.Show:
                EndPopup();
                try
                {
                    bool ru=LobbyText.Russian;
                    _errorPopup(screen,LobbyVersionRules.Title(ru),LobbyVersionRules.ClientText(ru),()=>BackToJoin(screen));
                    _mismatchArgs=null;_mismatchNet=null;
                    // The pinned void helper catches its own UI exceptions: this is request completion, not render
                    // proof. Native QA also forbids its failure log and inspects the captured popup image.
                    LobbyCore.Info("VERSION: version popup request returned");
                }
                catch(Exception e){LobbyCore.Warn("VERSION: version popup failed ({Error}); using the mod's mismatch path",e.Message);FallbackMismatch();return;}
                _sweepUntil=now+Ticks(LobbyCapRules.ToastSweepSeconds);_swept=SweepDisconnectToast();
                return;
        }
    }
    static void BackToJoin(TitleScreen screen)
    {
        try{screen.clearMenu();_joinMenu(screen);}
        catch(Exception e){LobbyCore.Warn("VERSION: return to the join menu failed: {Error}",e.Message);}
    }
    // Leaves the game as the mod alone would: replays the skipped NotifyProtocolMismatch with its own arguments.
    static bool PopupOwnerCurrent(NetNode owner)
        =>LobbyVersionRules.PopupOwnerCurrent(owner,ModEntry._net,owner!=null&&ReceivedWelcomes.TryGetValue(owner,out var r)&&r.Cancelled);
    static void FallbackMismatch()
    {
        var args=_mismatchArgs;var owner=_mismatchNet;_mismatchArgs=null;_mismatchNet=null;
        if(args==null)return;
        // EndPopup clears _popupNet before several fault paths call here. Retain and re-check the independent
        // mismatch owner, including Dispose cancellation, so a fault cannot revive an obsolete session's UI.
        try{if(!PopupOwnerCurrent(owner)){LobbyCore.Info("VERSION: mismatch fallback dropped, the connection changed or was cancelled");return;}}
        catch(Exception e){LobbyCore.Warn("VERSION: mismatch fallback owner check failed: {Error}",e.Message);return;}
        _bypass=true;
        try{_mismatch.Invoke(null,args);}
        catch(Exception e){LobbyCore.Warn("VERSION: mismatch fallback failed: {Error}",e.GetBaseException().Message);}
        finally{_bypass=false;}
    }
    // The refused client's disconnect path also queues "Host disconnected from server."; on the title screen the toast
    // would surface at the start of the next run, so the newest such entry is taken out once (as CAP does).
    static bool SweepDisconnectToast()
    {
        var target=_localize("Host disconnected from server.")?.Trim();
        if(string.IsNullOrEmpty(target))return true;
        var queue=_msgQueue.GetValue(null);
        lock(_msgSync.GetValue(null))
        {
            var items=new List<object>();var texts=new List<string>();
            foreach(var m in (IEnumerable)queue){items.Add(m);texts.Add(_msgText.GetValue(m) as string);}
            int i=LobbyCapRules.NewestIndex(texts,target);
            if(i<0)return false;
            _queueClear.Invoke(queue,null);
            for(int k=0;k<items.Count;k++)if(k!=i)_queueEnqueue.Invoke(queue,new[]{items[k]});
        }
        LobbyCore.Info("VERSION: dropped the queued 'Host disconnected' toast of the refused join");
        return true;
    }
}
