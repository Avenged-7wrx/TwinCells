using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using Rr=LobbyRefreshRules;

// Part REFRESH of CoopLobby (Alpha2 unit U12, PLAN-ALPHA2.md issue 17): the base mod's periodic lobby menu rebuild.
//
// CoopAdvancedHardening.OnFrameUpdate calls LobbySession.RefreshRoomStatusMenuIfVisible every 0.5 s while a network
// session is alive (release IL 163843-163855); with a lobby flag set it enqueues a full ShowHostStatusMenu /
// ShowClientWaitingMenu rebuild at most once a second (IL 81517-81545, 177490-177505). Each rebuild clears and recreates
// the title menu and the ConnectionUI panel (7.75-30 ms in the 2026-11-12 playtest, 95 in 95 s), and launch never clears
// the flags, so it went on through the whole run (H:1471, during a cinematic). The mod already refreshes the menu on
// every event that changes it (RequestLobbyMenuRefresh: ready, remote ready, connect, disconnect, username, coop state,
// save slot, launch mode, generate payload; IL 80049-80061 and its 10 callers), so the periodic one is a backstop.
//   prefix   RefreshRoomStatusMenuIfVisible: in the run (InActualRun) or after the launch fired (AutoStartTriggered) the
//            original never runs; in the lobby it runs only when the cheap signature (LobbyRefreshRules.Compose) differs
//            from the one taken at the last real rebuild, when that rebuild is 10 s old, or when the host menu shows
//            Launch disabled although every player is ready (at most every 2 s). Otherwise the original runs unchanged.
//   postfix  ShowHostStatusMenu / ShowClientWaitingMenu: every real (outermost, flag set) rebuild, whatever its cause,
//            records the signature, the time and the host menu's Start button state.
// Installed from LobbyCap's static constructor (LobbyCapRefresh.cs: CoopLobby.cs, which lists the parts, is not owned by
// U12), i.e. right before CoopLobby.Initialize installs CAP. Kill switch COOP_LOBBY_REFRESH_DISABLE=1 (and
// COOP_LOBBY_DISABLE=1); with CAP switched off (COOP_LOBBY_CAP_DISABLE=1) this part is not installed either. Every body
// in try/catch behind its own fault gate: a fault or a trip lets the original run. The three patched methods are pinned
// by their release IL (378BE6B0) besides the whole-DLL pin of LobbyCore.CheckPins. Main thread only (both patched
// methods run there). Logs: one install line, one line when a run pauses the rebuild, a counters line at most every
// 5 minutes; the signature itself (player names) is never logged.
public static class LobbyRefresh
{
    public const string Owner="CoopLobby.Refresh.20261112";
    public const string Switch="REFRESH";
    static readonly (string Name,int Length,string Sha)[] IlPins={
        ("RefreshRoomStatusMenuIfVisible",102,"ADA61F1A8FD35243702159F2C56992109061E8E660FC58FD3C34C291CBE550E0"),
        ("ShowHostStatusMenu",743,"4292E5B460CCCA2DFADE1D17542FEFC66F0AEEB528600AD28B345BBA5A93C96A"),
        ("ShowClientWaitingMenu",511,"6A4CACD6928B4A3BF56FC7B6212E4C81B1A199638D4B350DF11BA1C39A1E8DC2"),
    };
    const double SummaryEverySeconds=300;
    static readonly object InstallSync=new();
    static readonly FaultGate Gate=new("LobbyRefresh");
    static bool _booted;
    static volatile bool _on;
    static readonly List<MethodBase> _patched=new();

    // bound members (exact signatures, release names)
    static MethodInfo _refresh,_showHost,_showClient,_snapshot;
    static FieldInfo _inHost,_inClient,_depth,_transport,_steamLobby,_lobbySync,_coopStates,_coopId,_coopHas,_pending,_bLabel,_bEnabled;
    static PropertyInfo _pInRun,_pAuto,_pReady;
    static Func<int?,int> _slot;
    static Func<dc.pr.TitleScreen,bool> _customSave;
    static Func<dc.pr.TitleScreen> _title;
    static Func<string> _startLabel;

    // the last real rebuild (main thread)
    static bool _haveBuilt,_builtHost;static string _builtSig;static double _builtAt;static int _builtLaunch=-1;
    static double _lastBackstop=double.NaN,_lastSummary=double.NaN;static long _lastSummaryJudged=-1;static bool _inRunNoticed;
    // counters (main thread; read by the unit probe through Stats)
    static long _calls,_original,_skipRun,_skipLobby,_passFirst,_passChanged,_passFallback,_passLaunch,_rebuilds,_rebuildsHost,_rebuildsClient;

    static double Now=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;

    // From LobbyCap's static constructor, once. Never throws.
    internal static void Boot()
    {
        lock(InstallSync){if(_booted)return;_booted=true;}
        try
        {
            if(LobbyCore.Off(Switch)){LobbyCore.Info("LobbyRefresh switched off by COOP_LOBBY_REFRESH_DISABLE");return;}
            Install();
            LobbyCore.Info("LobbyRefresh installed: periodic lobby menu rebuild paused in the run; in the lobby only on a change, a stale Launch button or every {Seconds} s",Rr.FallbackSeconds);
        }
        catch(Exception e){LobbyCore.Warn("LobbyRefresh not installed: {Error}",e.GetBaseException().Message);}
    }

    static void Install()
    {
        LobbyCore.CheckPins();
        Bind();
        var harmony=new Harmony(Owner);
        try
        {
            _patched.Add(_refresh);harmony.Patch(_refresh,prefix:new HarmonyMethod(Own(nameof(BeforeRefresh))));
            _patched.Add(_showHost);harmony.Patch(_showHost,postfix:new HarmonyMethod(Own(nameof(AfterShowHost))));
            _patched.Add(_showClient);harmony.Patch(_showClient,postfix:new HarmonyMethod(Own(nameof(AfterShowClient))));
            foreach(var t in _patched)
            {
                var info=Harmony.GetPatchInfo(t);
                int pre=info==null?0:info.Prefixes.Count(p=>p.owner==Owner),post=info==null?0:info.Postfixes.Count(p=>p.owner==Owner);
                bool other=info!=null&&(info.Transpilers.Any(p=>p.owner==Owner)||info.Finalizers.Any(p=>p.owner==Owner));
                if(other||(t==_refresh?pre!=1||post!=0:pre!=0||post!=1))throw new InvalidOperationException("LobbyRefresh patch not installed exactly once on "+t.Name);
            }
            _on=true;
        }
        catch
        {
            foreach(var t in _patched)try{harmony.Unpatch(t,HarmonyPatchType.All,Owner);}catch(Exception r){LobbyCore.Warn("LobbyRefresh rollback of {Method} failed: {Error}",t.Name,r.Message);}
            _patched.Clear();
            throw;
        }
    }
    static MethodInfo Own(string name)=>typeof(LobbyRefresh).GetMethod(name,LobbyCore.S)??throw new MissingMethodException("LobbyRefresh",name);

    static void Bind()
    {
        var lobby=LobbyCore.ModType("DeadCellsMultiplayerMod.LobbySession");
        _refresh=LobbyCore.Method(lobby,"RefreshRoomStatusMenuIfVisible",true,typeof(void));
        _showHost=LobbyCore.Method(lobby,"ShowHostStatusMenu",true,typeof(void),typeof(dc.pr.TitleScreen));
        _showClient=LobbyCore.Method(lobby,"ShowClientWaitingMenu",true,typeof(void),typeof(dc.pr.TitleScreen));
        foreach(var m in new[]{_refresh,_showHost,_showClient})PinIl(m);
        _inHost=LobbyCore.Field(lobby,"_inHostStatusMenu",typeof(bool),true);
        _inClient=LobbyCore.Field(lobby,"_inClientWaitingMenu",typeof(bool),true);
        _depth=LobbyCore.Field(lobby,"_menuRebuildDepth",typeof(int),true);
        _transport=LobbyCore.Field(lobby,"_menuTransport",null,true);
        if(!_transport.FieldType.IsEnum||Enum.GetUnderlyingType(_transport.FieldType)!=typeof(int))throw new InvalidOperationException("Pinned field mismatch: LobbySession._menuTransport");
        _steamLobby=LobbyCore.Field(lobby,"_steamLobbyId",typeof(ulong),true);
        _lobbySync=LobbyCore.Field(lobby,"Sync",typeof(object),true);
        _coopStates=LobbyCore.Field(lobby,"_remoteCoopStates",null,true);
        var ct=_coopStates.FieldType;
        if(!_lobbySync.IsInitOnly||!_coopStates.IsInitOnly||!ct.IsGenericType||ct.GetGenericTypeDefinition()!=typeof(Dictionary<,>)||ct.GetGenericArguments()[0]!=typeof(int)||ct.GetGenericArguments()[1].Name!="RemoteCoopState")
            throw new InvalidOperationException("Pinned field mismatch: LobbySession._remoteCoopStates");
        var state=ct.GetGenericArguments()[1];
        _coopId=LobbyCore.Field(state,"CoopId",typeof(string),false);
        _coopHas=LobbyCore.Field(state,"HasContinueSave",typeof(bool),false);
        _snapshot=LobbyCore.Method(lobby,"ReadSessionSnapshot",true,null);
        var snap=_snapshot.ReturnType;
        if(!snap.IsValueType||snap.Name!="LobbySessionSnapshot")throw new InvalidOperationException("Pinned method mismatch: LobbySession.ReadSessionSnapshot");
        _pInRun=Prop(snap,"InActualRun");_pAuto=Prop(snap,"AutoStartTriggered");_pReady=Prop(snap,"LocalReady");
        _slot=LobbyCore.Delegate<Func<int?,int>>(LobbyCore.Method(lobby,"ResolveSaveSlotNumber",true,typeof(int),typeof(int?)));
        _customSave=LobbyCore.Delegate<Func<dc.pr.TitleScreen,bool>>(LobbyCore.Method(lobby,"ResolveCurrentSaveIsCustom",true,typeof(bool),typeof(dc.pr.TitleScreen)));
        _title=LobbyCore.Delegate<Func<dc.pr.TitleScreen>>(LobbyCore.Method(lobby,"GetTitleScreen",true,typeof(dc.pr.TitleScreen)));
        _startLabel=LobbyCore.Delegate<Func<string>>(LobbyCore.Method(lobby,"GetStartNormalModeButtonLabel",true,typeof(string)));
        var ui=LobbyCore.ModType("DeadCellsMultiplayerMod.MultiplayerModUI.Connection.ConnectionUI");
        var pb=ui.GetNestedType("PendingButton",BindingFlags.NonPublic|BindingFlags.Public)??throw new MissingMemberException("ConnectionUI","PendingButton");
        _pending=LobbyCore.Field(ui,"PendingButtons",typeof(List<>).MakeGenericType(pb),true);
        _bLabel=LobbyCore.Field(pb,"Label",typeof(string),false);
        _bEnabled=LobbyCore.Field(pb,"Enabled",typeof(bool),false);
    }
    static PropertyInfo Prop(Type t,string name)
    {
        var p=t.GetProperty(name,BindingFlags.Public|BindingFlags.Instance);
        if(p==null||p.PropertyType!=typeof(bool)||p.GetMethod==null)throw new InvalidOperationException("Pinned property mismatch: "+t.Name+"."+name);
        return p;
    }
    static void PinIl(MethodInfo m)
    {
        foreach(var p in IlPins)
            if(p.Name==m.Name)
            {
                var il=m.GetMethodBody()?.GetILAsByteArray();
                if(il==null||il.Length!=p.Length||Convert.ToHexString(SHA256.HashData(il))!=p.Sha)throw new InvalidOperationException("pinned IL changed: LobbySession."+m.Name);
                return;
            }
        throw new InvalidOperationException("no IL pin for LobbySession."+m.Name);
    }

    // ---- prefix: RefreshRoomStatusMenuIfVisible ------------------------------------------------------------------
    static bool BeforeRefresh()
    {
        if(!_on||Gate.Tripped)return true;
        try
        {
            _calls++;
            bool host=(bool)_inHost.GetValue(null),client=(bool)_inClient.GetValue(null);
            Rr.Verdict v;
            if(!host&&!client)v=Rr.Verdict.Original;
            else
            {
                object snap=_snapshot.Invoke(null,null);
                bool inRun=(bool)_pInRun.GetValue(snap),auto=(bool)_pAuto.GetValue(snap);
                if(inRun||auto)v=Rr.Decide(true,inRun,auto,false,false,0,false,0);   // SkipRun: no signature read in the run
                else
                {
                    string sig=Read(host,snap,out bool allReady);
                    double now=Now;
                    bool stale=Rr.LaunchStale(host&&_builtHost,_builtLaunch,allReady);
                    v=Rr.Decide(true,false,false,_haveBuilt,string.Equals(sig,_builtSig,StringComparison.Ordinal),now-_builtAt,stale,now-_lastBackstop);
                    if(v==Rr.Verdict.PassLaunchStale)_lastBackstop=now;
                }
            }
            Count(v);
            Gate.Ok();
            return Rr.Runs(v);
        }
        catch(Exception e){Fault(e);return true;}
    }
    static void Count(Rr.Verdict v)
    {
        switch(v)
        {
            case Rr.Verdict.Original:_original++;return;
            case Rr.Verdict.SkipRun:_skipRun++;
                if(!_inRunNoticed){_inRunNoticed=true;LobbyCore.Info("LobbyRefresh: periodic lobby menu rebuild paused for the run (lobby flags still set: host={Host} client={Client})",(bool)_inHost.GetValue(null),(bool)_inClient.GetValue(null));}
                break;
            case Rr.Verdict.SkipLobby:_skipLobby++;break;
            case Rr.Verdict.PassFirst:_passFirst++;break;
            case Rr.Verdict.PassChanged:_passChanged++;break;
            case Rr.Verdict.PassFallback:_passFallback++;break;
            case Rr.Verdict.PassLaunchStale:_passLaunch++;break;
        }
        if(v!=Rr.Verdict.SkipRun)_inRunNoticed=false;
        long judged=_skipRun+_skipLobby+_passFirst+_passChanged+_passFallback+_passLaunch;
        double now=Now;
        if(double.IsNaN(_lastSummary))_lastSummary=now;                         // the first line comes after a full period
        else if(judged!=_lastSummaryJudged&&now-_lastSummary>=SummaryEverySeconds)
        {
            _lastSummary=now;_lastSummaryJudged=judged;
            LobbyCore.Info("LobbyRefresh: periodic calls={Calls} judged={Judged} allowed={Allowed} (first={First} changed={Changed} every10s={Fallback} launch={Launch}) skipped run={Run} lobby={Lobby}; menu rebuilds={Rebuilds} (host={Host} client={Client})",
                _calls,judged,_passFirst+_passChanged+_passFallback+_passLaunch,_passFirst,_passChanged,_passFallback,_passLaunch,_skipRun,_skipLobby,_rebuilds,_rebuildsHost,_rebuildsClient);
        }
    }

    // ---- postfixes: ShowHostStatusMenu / ShowClientWaitingMenu -------------------------------------------------------
    static void AfterShowHost()=>Built(true);
    static void AfterShowClient()=>Built(false);
    static void Built(bool host)
    {
        if(!_on||Gate.Tripped)return;
        try
        {
            if((int)_depth.GetValue(null)>0)return;                    // nested call: returned before it rebuilt (IL 71625-71628, 71879-71882)
            if(!(bool)(host?_inHost:_inClient).GetValue(null))return;  // the rebuild failed (caught by the mod) before it set its flag
            object snap=_snapshot.Invoke(null,null);
            _builtSig=Read(host,snap,out _);_builtAt=Now;_haveBuilt=true;_builtHost=host;
            _builtLaunch=host?LaunchNow():-1;
            _rebuilds++;if(host)_rebuildsHost++;else _rebuildsClient++;
            Gate.Ok();
        }
        catch(Exception e){Fault(e);}
    }

    // The signature of what the lobby menus show (LobbyRefreshRules.Compose) and whether every player is ready now.
    static string Read(bool host,object snap,out bool allReady)
    {
        var remotes=new List<Rr.Remote>(2);
        var net=ModEntry._net;
        if(net!=null&&net.IsAlive&&net.TryGetRemoteUserSnapshots(out var list))
        {
            try
            {
                foreach(var r in list)
                {
                    bool ready=net.TryGetRemoteReady(r.Id,out var rr)&&rr;
                    remotes.Add(new Rr.Remote{Id=r.Id,Name=r.Username,Ready=ready});
                }
            }
            finally{NetNode.ReleaseConsumedList(list);}
        }
        var coop=new List<Rr.CoopState>(2);
        lock(_lobbySync.GetValue(null))
            foreach(DictionaryEntry e in (IDictionary)_coopStates.GetValue(null))
                coop.Add(new Rr.CoopState{Id=(int)e.Key,HasContinueSave=e.Value!=null&&(bool)_coopHas.GetValue(e.Value),CoopId=e.Value==null?null:(string)_coopId.GetValue(e.Value)});
        allReady=Rr.AllReady((bool)_pReady.GetValue(snap),remotes.Select(r=>r.Ready));
        return Rr.Compose(host,snap?.ToString(),Convert.ToInt32(_transport.GetValue(null)),(ulong)_steamLobby.GetValue(null)!=0UL,_slot(null),_customSave(_title()),remotes,coop);
    }
    // The host menu's Start button as last built (ConnectionUI.PendingButtons stay valid until the next BeginMenu).
    static int LaunchNow()
    {
        if(!(_pending.GetValue(null) is IList list))return -1;
        var labels=new List<string>(list.Count);var enabled=new List<bool>(list.Count);
        foreach(var b in list){labels.Add(b==null?null:(string)_bLabel.GetValue(b));enabled.Add(b!=null&&(bool)_bEnabled.GetValue(b));}
        return Rr.LaunchState(labels,enabled,_startLabel());
    }

    static void Fault(Exception e)
    {
        try{if(Gate.Fail(e))LobbyCore.Warn("LobbyRefresh off for this session: the base mod's periodic lobby menu rebuild runs unchanged");}catch{}
    }

    // Read-only view for the unit probe (experiments\alpha2-perf-lobby\probe): main thread.
    public static Dictionary<string,long> Stats()=>new(StringComparer.Ordinal)
    {
        ["on"]=_on?1:0,["tripped"]=Gate.Tripped?1:0,["faults"]=Gate.Faults,["calls"]=_calls,["original"]=_original,["skipRun"]=_skipRun,["skipLobby"]=_skipLobby,
        ["passFirst"]=_passFirst,["passChanged"]=_passChanged,["passFallback"]=_passFallback,["passLaunch"]=_passLaunch,
        ["rebuilds"]=_rebuilds,["rebuildsHost"]=_rebuildsHost,["rebuildsClient"]=_rebuildsClient,["builtLaunch"]=_builtLaunch,["haveBuilt"]=_haveBuilt?1:0,
    };
}
