using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using DeadCellsMultiplayerMod;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using TitleScreen=dc.pr.TitleScreen;

// Main-thread side of the cap: the refused client's "Lobby is full" popup and the host's notice.
public static partial class LobbyCap
{
    // Main thread only (NotifyProtocolMismatch runs inside HandleLine on the main thread; AfterFrame is a postfix).
    static long _popupSince,_sweepUntil,_lobbyNoticeAt;
    static bool _swept,_bypass;
    static object[] _mismatchArgs;
    static NetNode _popupNet;           // only while a popup is pending (EndPopup clears it: no dead node kept alive)
    static string _lobbyNotice;

    // LobbySession.NotifyProtocolMismatch(string remoteBuild, int remoteProtocol, string localBuild, int localProtocol,
    // NetRole localRole), internal static (IL 81546-81610), called by RejectIncompatiblePeer (IL 4742-4748) from the
    // WELCOME branch of the client's HandleLine (IL 10006) on the main thread. For our reject (protocol 0 + marker)
    // the original is skipped: no "Co-op version mismatch" toast and popup. RejectIncompatiblePeer still returns true,
    // so HandleLine returns false and the mod's own disconnect path runs unchanged (CleanupClient ->
    // NotifyRemoteDisconnected). NetRole is a public mod enum (Host=1, Client=2) and is named directly. Nobody else
    // patches this method (verify.md #10); CSF's RejectIncompatiblePeer postfixes see the same true result.
    static bool BeforeProtocolMismatch(string remoteBuild,int remoteProtocol,string localBuild,int localProtocol,NetRole localRole)
    {
        if(_bypass)return true;
        try
        {
            if(!Installed||Gate.Tripped||localRole!=NetRole.Client||!LobbyCapRules.IsRejectBuild(remoteProtocol,remoteBuild))return true;
            _mismatchArgs=new object[]{remoteBuild,remoteProtocol,localBuild,localProtocol,localRole};
            _popupSince=Stopwatch.GetTimestamp();_popupNet=ModEntry._net;_sweepUntil=0;_swept=false;
            LobbyShared.PopupPending=true;           // the UI keeps the stale waiting panel hidden until our popup commits
            LobbyCore.Info("CAP: the host refused this connection: lobby is full (2 players)");
            return false;
        }
        catch(Exception e){Gate.Fail(e);return true;}
    }

    // ModEntry.OnFrameUpdate(double) postfix, main thread, also on the title screen.
    static void AfterFrame()
    {
        if(!Installed)return;
        // Before the trip check: a refused Steam peer goes back to the mod's cleanup even after the part switched off.
        try{TickEvictions();}catch(Exception e){Gate.Fail(e);}
        try
        {
            if(Gate.Tripped){WithdrawLobbyNotice(true);if(_popupSince!=0){EndPopup();FallbackMismatch();}return;}
            TickHostNotice();
            TickClientPopup();
        }
        catch(Exception e)
        {
            if(Gate.Fail(e)&&_popupSince!=0){EndPopup();FallbackMismatch();}
        }
    }
    static void EndPopup(){_popupSince=0;_popupNet=null;LobbyShared.PopupPending=false;}

    static void TickHostNotice()
    {
        WithdrawLobbyNotice(false);
        if(Volatile.Read(ref _toastsDue)==0||Interlocked.Exchange(ref _toastsDue,0)==0)return;
        var text=LobbyText.Get("lobbyFullHost");
        Notice=text;NoticeRevision++;
        if(_inRun()){MultiplayerUI.PushSystemMessage(text,8.0,1.5);return;}   // shown by the run's hero update
        // Lobby: the UI part's Tick (OnFrameUpdate postfix, Priority.Last, so after this Priority.Normal one in the
        // same frame) takes it from LastNotice. A colour notice already waiting there is not overwritten.
        if(LobbyColorState.LastNotice==null){LobbyColorState.Post(text);_lobbyNotice=text;_lobbyNoticeAt=Stopwatch.GetTimestamp();}
        LobbyCore.Info("CAP: lobby-full notice handed to the lobby hint line (no toast outside a run)");
    }
    // Takes our notice back when no lobby layer consumed it in time (UI part off, or the host is not on a lobby
    // screen), so it cannot surface on a later screen. Only our own string object is removed, never a colour notice.
    static void WithdrawLobbyNotice(bool now)
    {
        if(_lobbyNotice==null)return;
        if(!now&&Stopwatch.GetTimestamp()-_lobbyNoticeAt<Ticks(LobbyCapRules.LobbyNoticeKeepSeconds))return;
        if(ReferenceEquals(LobbyColorState.LastNotice,_lobbyNotice))LobbyColorState.LastNotice=null;
        _lobbyNotice=null;
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
        // A new connection attempt made a new NetNode meanwhile: the popup belongs to the old one.
        var net=ModEntry._net;
        if(net!=null&&!ReferenceEquals(net,_popupNet)&&net.IsAlive){EndPopup();_mismatchArgs=null;LobbyCore.Info("CAP: lobby-full popup dropped, a new connection started");return;}
        var screen=_titleScreen();
        bool waiting=(bool)_waitingMenu.GetValue(null);
        switch(LobbyCapRules.Popup(_popupSince,now,waiting,screen!=null,Ticks(LobbyCapRules.PopupSettleSeconds),Ticks(LobbyCapRules.PopupFallbackSeconds),Ticks(LobbyCapRules.PopupGiveUpSeconds)))
        {
            case LobbyCapRules.PopupStep.GiveUp:
                EndPopup();LobbyCore.Warn("CAP: no title screen for the lobby-full popup; using the mod's mismatch path");FallbackMismatch();return;
            case LobbyCapRules.PopupStep.Show:
                EndPopup();
                try
                {
                    // The mod's generic popup (LobbySession.cs:1222-1256, IL 77519): clears the menu, clears both lobby
                    // flags, so the 1 s room auto-refresh and RequestLobbyMenuRefresh cannot repaint the stale
                    // "Connected" menu over it (RunLaunchFlow.Compat.cs:421-439, ReadySync.cs:112-133).
                    _errorPopup(screen,LobbyText.Get("lobbyFull"),LobbyText.Get("lobbyFullText"),()=>BackToJoin(screen));
                    _mismatchArgs=null;
                }
                catch(Exception e){LobbyCore.Warn("CAP: lobby-full popup failed ({Error}); using the mod's mismatch path",e.Message);FallbackMismatch();return;}
                _sweepUntil=now+Ticks(LobbyCapRules.ToastSweepSeconds);_swept=SweepDisconnectToast();
                return;
        }
    }
    // OK goes back to the join choices, exactly as the mod's mismatch popup does (RunLaunchFlow.Compat.cs:475-479).
    static void BackToJoin(TitleScreen screen)
    {
        try{screen.clearMenu();_joinMenu(screen);}
        catch(Exception e){LobbyCore.Warn("CAP: return to the join menu failed: {Error}",e.Message);}
    }
    // Leaves the game as the mod alone would: replays the skipped NotifyProtocolMismatch with its own arguments
    // (shows "Co-op version mismatch" with our reason in the detail line).
    static void FallbackMismatch()
    {
        var args=_mismatchArgs;_mismatchArgs=null;
        if(args==null)return;
        _bypass=true;
        try{_mismatch.Invoke(null,args);}
        catch(Exception e){LobbyCore.Warn("CAP: mismatch fallback failed: {Error}",e.GetBaseException().Message);}
        finally{_bypass=false;}
    }

    // The refused client's disconnect path also queues "Host disconnected from server." (NotifyRemoteDisconnected
    // client branch, IL 72429-72433). On the title screen the toast is not drawn (no HUD) and would pop up at the
    // start of the next run, so the newest such entry is taken out of the pending queue once, under the same lock
    // PushSystemMessage uses (IL 87633-87652; Text is the trimmed message, IL 87644).
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
        LobbyCore.Info("CAP: dropped the queued 'Host disconnected' toast of the refused join");
        return true;
    }
}
