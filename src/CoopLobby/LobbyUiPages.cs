using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;
using HarmonyLib;

// One captured Multiplayer page: the PendingButtons / PendingInfos a LobbySession.Show* call just committed, recognised
// against the mod's own strings for that page (LobbyUiPageRules.Match). Refs / InfoRefs are the PendingButton /
// PendingInfo objects themselves: BeginMenu clears both lists and every page builder adds new objects
// (ConnectionUI.BeginMenu IL 93658-93662, AddPendingButton newobj IL 93679, AddPendingInfo newobj IL 93710), so while
// the lists still hold exactly these objects no other menu has been started since this capture.
sealed class UiPageSnap
{
    public UiPageKind Kind;public ConnectionUI Ui;public bool Hub;
    public object[] Refs,InfoRefs;public LobbyUiButton[] Buttons;public bool[] Fields;public string[] Infos;
    public UiPagePlan Plan;public string Sig;
}

// The other Multiplayer pages in the lobby's look (architecture B like the lobby: the original ConnectionUI keeps
// building every page, hidden; our layer is drawn under ConnectionUI.root and calls the captured OnClick closures):
//   hub (ShowMultiplayerMenu), host transport (ShowHostTransportMenu), Steam host mode (ShowSteamHostModeMenu),
//   join transport with the friend lobbies (ShowJoinTransportMenu), LAN setup (ShowConnectionMenu).
// Gate: we draw over a page only while we KNOW it is up:
//   - captured by our own postfix on its Show* (prefix records _panelRoot first; the postfix requires a fresh
//     _panelRoot, i.e. CommitMenu -> onResize -> BuildFullScreenPanel ran inside this call, IL 93723-93748 /
//     96351-96362, a _menuRoot built on it by RebuildMenuScreen, IL 93861-93875, and the exact page shape);
//   - still current: same ConnectionUI, _mode Menu, _menuVisible, !_keepLobbyVisible (the lobby gate needs it set),
//     _hubLayout as committed, and PendingButtons / PendingInfos still hold the captured objects (see UiPageSnap).
// Any other menu commit (popups, the lobby, an uncovered caller) starts with BeginMenu and replaces the objects;
// ShowLobbyMode sets _mode Lobby (IL 93769-93770); DismissMenuUi clears _menuVisible (IL 95383-95385). P3 (frame) and
// P4 (render backstop) then remove our layer before the frame is drawn, and the new page's own fresh _panelRoot is
// visible already (we never hid it). The text prompt is no menu commit (OpenTextPrompt -> RebuildTextPromptUi only,
// IL 99897-99986): its _promptRoot is added to ConnectionUI.root after our layer (IL 101607-101615) with a full-window
// Interactive (IL 101725-101733), so the original prompt is drawn on top and takes every click, as over the original
// page. Escape stays the original: RebuildMenuScreen (still run, hidden) sets _menuEscapeAction from the captured
// buttons (IL 93901-93934) and TickMenuEscape runs from ConnectionUI.update without looking at _panelRoot
// (IL 96600-96645).
// Faults: own FaultGate. A fault puts the original page back at once and leaves that exact capture original; 8 in a
// row (or 64) switch the pages off for the session. The lobby screens are not affected by a pages fault.
static class LobbyUiPages
{
    static readonly FaultGate Gate=new("LobbyUi.Pages");
    static volatile bool _on;
    static FieldInfo _menuVisible,_hubLayout,_menuRoot,_fieldStyle,_menuSelection,_friendLobbies;
    static readonly List<(UiPageKind Kind,MethodInfo Target,string Postfix)> _targets=new();
    static LobbyUiPageView _view;static UiPageSnap _page;
    static ConnectionUI _ui;static dc.h2d.Object _hidden;
    static readonly List<LobbyUiPageView> _grave=new();
    static int _clickDepth,_callback;static string _blocked;static double _lastTick,_titleAt;
    static readonly HashSet<UiPageKind> _announced=new(),_refused=new();
    // friend lobby list page of the join page; kept while the join page is re-shown (friend list refresh)
    internal static int FriendPage;
    internal static bool HasView=>_view!=null;
    // Read-only state for the native QA probe (ui-pages-state reads these by name; the add-on itself never uses them).
    internal static string ShownKind=>_view!=null&&_page!=null?_page.Kind.ToString():"None";
    internal static int ShownFriends=>_view!=null&&_page?.Plan!=null?_page.Plan.Friends:-1;
    static bool On=>_on&&!Gate.Tripped&&LobbyUi.Running;
    static double Now=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;

    // ---- install (LobbyUi.Install: Bind before any patch, Patches after the lobby's as their own all-or-nothing step;
    // any failure here leaves the pages original and the lobby screens installed as validated) ---------------------
    public static void Bind()
    {
        var ui=typeof(ConnectionUI);
        _menuVisible=LobbyCore.Field(ui,"_menuVisible",typeof(bool),false);
        _hubLayout=LobbyCore.Field(ui,"_hubLayout",typeof(bool),false);
        _menuRoot=LobbyCore.Field(ui,"_menuRoot",typeof(dc.h2d.Object),false);
        var pb=ui.GetNestedType("PendingButton",BindingFlags.NonPublic|BindingFlags.Public)??throw new MissingMemberException("ConnectionUI","PendingButton");
        _fieldStyle=LobbyCore.Field(pb,"FieldStyle",typeof(bool),false);
        var lobby=LobbyCore.ModType("DeadCellsMultiplayerMod.LobbySession");
        _menuSelection=LobbyCore.Field(lobby,"_menuSelection",typeof(NetRole),true);
        var steam=LobbyCore.ModType("DeadCellsMultiplayerMod.SteamConnect");
        var fli=steam.GetNestedType("FriendLobbyInfo",BindingFlags.NonPublic|BindingFlags.Public)??throw new MissingMemberException("SteamConnect","FriendLobbyInfo");
        _friendLobbies=LobbyCore.Field(lobby,"_steamFriendLobbies",typeof(List<>).MakeGenericType(fli),true);
        var ts=typeof(dc.pr.TitleScreen);
        _targets.Clear();
        _targets.Add((UiPageKind.Hub,LobbyCore.Method(lobby,"ShowMultiplayerMenu",true,typeof(void),ts),nameof(AfterHub)));
        _targets.Add((UiPageKind.HostTransport,LobbyCore.Method(lobby,"ShowHostTransportMenu",true,typeof(void),ts),nameof(AfterHostTransport)));
        _targets.Add((UiPageKind.SteamHostMode,LobbyCore.Method(lobby,"ShowSteamHostModeMenu",true,typeof(void),ts),nameof(AfterSteamHostMode)));
        _targets.Add((UiPageKind.JoinTransport,LobbyCore.Method(lobby,"ShowJoinTransportMenu",true,typeof(void),ts),nameof(AfterJoinTransport)));
        _targets.Add((UiPageKind.LanSetup,LobbyCore.Method(lobby,"ShowConnectionMenu",true,typeof(void),ts,typeof(NetRole)),nameof(AfterLanSetup)));
    }
    // Prefix (Priority.First: before anything else can commit) + postfix (Priority.Last: sees the final lists) per page.
    public static IEnumerable<(MethodInfo Target,HarmonyMethod Prefix,HarmonyMethod Postfix)> Patches()
    {
        var pre=typeof(LobbyUiPages).GetMethod(nameof(BeforeShow),BindingFlags.NonPublic|BindingFlags.Static);
        foreach(var t in _targets)
        {
            var post=typeof(LobbyUiPages).GetMethod(t.Postfix,BindingFlags.NonPublic|BindingFlags.Static)??throw new MissingMethodException(nameof(LobbyUiPages),t.Postfix);
            yield return(t.Target,new HarmonyMethod(pre){priority=Priority.First},new HarmonyMethod(post){priority=Priority.Last});
        }
    }
    public static void Enable(){_on=true;}
    public static void Disable(){_on=false;}

    // ---- prefix / postfixes ----------------------------------------------------------------------------------------
    // the _panelRoot before the call (IntPtr.Zero when none); nested Show* calls get their own state
    static void BeforeShow(out IntPtr __state)
    {
        __state=IntPtr.Zero;
        try{if(On)__state=PanelPtr(LobbyUiNative.Live());}catch{}
    }
    static void AfterHub(IntPtr __state)=>AfterShow(UiPageKind.Hub,__state);
    static void AfterHostTransport(IntPtr __state)=>AfterShow(UiPageKind.HostTransport,__state);
    static void AfterSteamHostMode(IntPtr __state)=>AfterShow(UiPageKind.SteamHostMode,__state);
    static void AfterJoinTransport(IntPtr __state)=>AfterShow(UiPageKind.JoinTransport,__state);
    static void AfterLanSetup(IntPtr __state)=>AfterShow(UiPageKind.LanSetup,__state);

    static void AfterShow(UiPageKind kind,IntPtr before)
    {
        if(!On)return;
        try{Show(kind,before);Gate.Ok();}
        catch(Exception e){Fault(e);}
    }
    static void Show(UiPageKind kind,IntPtr before)
    {
        var ui=LobbyUiNative.Live();
        var snap=Capture(ui,kind,before,out var why);
        if(snap==null)
        {
            // not this page (or not committed): our layer goes, the page the mod built is shown as it is
            if(_view!=null||_hidden!=null)Leave(ui);
            if(why!=null&&_refused.Add(kind))LobbyCore.Info("LobbyUi: {Page} page not recognised ({Why}); the original page is shown",kind,why);
            return;
        }
        if(snap.Sig==_blocked){if(_view!=null||_hidden!=null)Leave(ui);return;}    // this exact page faulted: original
        _blocked=null;                                         // another page: try again (the fault gate still counts)
        LobbyUi.YieldView();                                 // the lobby layer (if any) hides now; its own P3 removes it
        if(_view!=null&&!_view.Valid(ui))Leave(_ui);           // a new ConnectionUI (title screen rebuilt)
        if(_page==null||_page.Kind!=kind)FriendPage=0;
        if(_view!=null&&_page!=null&&_page.Sig==snap.Sig){_page=snap;_view.Rebind(snap);}   // same page again: keep it (hover, list page)
        else
        {
            var old=_view;_view=null;_page=snap;
            try{_view=new LobbyUiPageView(ui,snap);}
            finally{Retire(old);}
        }
        HidePanel(ui);                                         // same call as the commit that made it visible
        LobbyUi.TitleHide();_titleAt=Now;
        LobbyUi.RenderOn();
        if(_announced.Add(kind))LobbyCore.Info("LobbyUi: {Page} page drawn",kind);
    }

    // ---- capture ---------------------------------------------------------------------------------------------------
    static IntPtr PanelPtr(ConnectionUI ui){var p=LobbyUiNative.Panel(ui);return p==null?IntPtr.Zero:p.HashlinkPointer;}
    static UiPageSnap Capture(ConnectionUI ui,UiPageKind kind,IntPtr before,out string why)
    {
        why=null;
        if(ui==null)return null;
        var panel=LobbyUiNative.Panel(ui);
        if(panel==null||panel.HashlinkPointer==IntPtr.Zero){why="no panel";return null;}
        if(panel.HashlinkPointer==before){why="no commit in this call";return null;}
        if(Convert.ToInt32(LobbyUiNative.Mode.GetValue(ui))!=LobbyUiNative.ModeMenu||!(bool)_menuVisible.GetValue(ui)||(bool)LobbyUiNative.KeepLobby.GetValue(ui)||
            (bool)_hubLayout.GetValue(ui)!=(kind==UiPageKind.Hub)){why="menu flags";return null;}
        // not the lobby flags: the Steam host lobby's Back reaches the host transport page with _inHostStatusMenu still
        // set (StopNetworkFromMenu does not clear it, IL 72147-72160, 178211-178228); !_keepLobbyVisible alone keeps
        // this gate apart from the lobby's (LobbyUiNative.GateNow needs it set)
        var menu=_menuRoot.GetValue(ui) as dc.h2d.Object;var parent=menu?.parent;
        if(parent==null||parent.HashlinkPointer!=panel.HashlinkPointer){why="menu not built on the panel";return null;}
        var list=LobbyUiNative.PendingButtons.GetValue(null) as IList;var infos=LobbyUiNative.PendingInfos.GetValue(null) as IList;
        if(list==null||infos==null){why="no pending lists";return null;}
        int n=list.Count;
        var s=new UiPageSnap{Kind=kind,Ui=ui,Hub=kind==UiPageKind.Hub,Refs=new object[n],Buttons=new LobbyUiButton[n],Fields=new bool[n],InfoRefs=new object[infos.Count],Infos=new string[infos.Count]};
        var labels=new string[n];
        var sig=new System.Text.StringBuilder().Append((int)kind);
        for(int i=0;i<n;i++)
        {
            var b=list[i];if(b==null){why="null button";return null;}
            s.Refs[i]=b;
            var m=new LobbyUiButton{Label=(string)LobbyUiNative.BLabel.GetValue(b)??"",Help=(string)LobbyUiNative.BHelp.GetValue(b)??"",
                Enabled=(bool)LobbyUiNative.BEnabled.GetValue(b),Color=(int)LobbyUiNative.BColor.GetValue(b),OnClick=LobbyUiNative.BOnClick.GetValue(b) as Action};
            s.Buttons[i]=m;s.Fields[i]=(bool)_fieldStyle.GetValue(b);labels[i]=m.Label;
            sig.Append('\u0001').Append(m.Label).Append('\u0002').Append(m.Help).Append('\u0002').Append(m.Enabled).Append(m.Color).Append(s.Fields[i]);
        }
        for(int i=0;i<infos.Count;i++)
        {
            var o=infos[i];if(o==null){why="null info";return null;}
            s.InfoRefs[i]=o;s.Infos[i]=LobbyUiNative.IText.GetValue(o) as string??"";
            sig.Append('\u0003').Append(s.Infos[i]);
        }
        var plan=LobbyUiPageRules.Match(Expect(kind),labels,s.Fields,s.Infos);
        if(plan==null){why="page shape";return null;}
        if(kind==UiPageKind.JoinTransport&&(!(_friendLobbies.GetValue(null) is IList fl)||fl.Count!=plan.Friends)){why="friend count";return null;}
        s.Plan=plan;s.Sig=sig.Append('\u0004').Append(plan.Host).ToString();
        return s;
    }
    // What the mod builds for each page in the current language: GetText.Instance.GetString of the release literals
    // (Localize is the same call, IL 72907-72911). A string the mod cannot give us is null and fails the match.
    static UiPageExpect Expect(UiPageKind kind)
    {
        static string S(string msgid)=>LobbyUiNative.ModString(msgid);
        switch(kind)
        {
            case UiPageKind.Hub:              // IL 76057-76118
                return new UiPageExpect{Kind=kind,Infos=new[]{S("Co-op multiplayer"),S("Host a session or join a friend over LAN or Steam.")},Choices=new[]{S("Host game"),S("Join game")},Back=S("Back")};
            case UiPageKind.HostTransport:    // IL 76186-76247
                return new UiPageExpect{Kind=kind,Infos=new[]{S("Host options"),S("LAN uses IP/port. Steam uses friends or a lobby code.")},Choices=new[]{S("Lan host"),S("Steam host")},Back=S("Back")};
            case UiPageKind.SteamHostMode:    // IL 76311-76374
                return new UiPageExpect{Kind=kind,Infos=new[]{S("Steam lobby visibility"),S("Friends-only stays private. Public creates a shareable lobby code.")},Choices=new[]{S("Steam friends-only host"),S("Steam public host")},Back=S("Back")};
            case UiPageKind.JoinTransport:    // IL 76439-76582
            {
                var join=S("Join");
                return new UiPageExpect{Kind=kind,Infos=new[]{S("Join a session"),S("Friends hosting right now appear below."),S("No friends currently hosting — list refreshes automatically.")},
                    Choices=new[]{S("Lan join")},Tail=new[]{S("Open Steam friends"),S("Join by Steam lobby code (fallback)")},FriendSuffix=join==null?null:" - "+join,Back=S("Back")};
            }
            case UiPageKind.LanSetup:         // IL 76806-76971; role as stored at IL 76786-76788
            {
                bool host=(NetRole)_menuSelection.GetValue(null)==NetRole.Host;
                return new UiPageExpect{Kind=kind,Host=host,
                    Infos=host?new[]{S("LAN host setup"),S("Share your IP and port with players on the same network.")}:new[]{S("LAN join setup"),S("Enter the host IP and port, then Join.")},
                    FieldPrefixes=new[]{S("Username: "),S("IP: "),S("Port: ")},Primary=S(host?"Host":"Join"),Back=S("Back")};
            }
        }
        return null;
    }
    static bool Same(IList list,object[] refs)
    {
        if(list==null||refs==null||list.Count!=refs.Length)return false;
        for(int i=0;i<refs.Length;i++)if(!ReferenceEquals(list[i],refs[i]))return false;
        return true;
    }
    // Still the captured page (see the class comment).
    static bool Current(ConnectionUI ui)
    {
        var p=_page;
        if(p==null||ui==null||!ReferenceEquals(ui,p.Ui))return false;
        if(Convert.ToInt32(LobbyUiNative.Mode.GetValue(ui))!=LobbyUiNative.ModeMenu)return false;
        if(!(bool)_menuVisible.GetValue(ui)||(bool)LobbyUiNative.KeepLobby.GetValue(ui)||(bool)_hubLayout.GetValue(ui)!=p.Hub)return false;
        if(LobbyUiNative.Panel(ui)==null)return false;
        return Same(LobbyUiNative.PendingButtons.GetValue(null) as IList,p.Refs)&&Same(LobbyUiNative.PendingInfos.GetValue(null) as IList,p.InfoRefs);
    }

    // ---- P3 / P4 (called by LobbyUi's ModEntry.OnFrameUpdate postfix and Boot.render handler) -----------------------
    internal static void AfterFrame()
    {
        DrainGrave();
        if(!On)return;
        try{Frame();Gate.Ok();}
        catch(Exception e){Fault(e);}
    }
    static void Frame()
    {
        double now=Now,dt=_lastTick>0?now-_lastTick:0;_lastTick=now;
        if(_view==null&&_hidden==null)return;
        var ui=LobbyUiNative.Live();
        if(_view==null||!_view.Valid(ui)||!Current(ui)){Leave(ui);return;}
        HidePanel(ui);                                         // a resize rebuilt it visible (ConnectionUI.update IL 96458-96497)
        if(now-_titleAt>=0.25){_titleAt=now;LobbyUi.TitleHide();}
        _view.Show();
        _view.Tick(dt);
    }
    // Before the frame is drawn: a commit that ran after P3 (main-thread queue drained after orig update, IL 34166-34172)
    // takes our layer away and leaves its own page; a rebuild of the same page is hidden again.
    internal static void Backstop()
    {
        if(!On)return;
        try
        {
            var v=_view;if(v==null)return;
            var ui=LobbyUiNative.Live();
            if(!ReferenceEquals(ui,v.Ui)){v.HideNow();return;}
            if(!Current(ui)){Leave(ui);return;}
            HidePanel(ui);
            LobbyUi.TitleRehide();
        }
        catch(Exception e){Fault(e);}
    }
    // The lobby takes over (its Show postfix): our layer hides at once; P3 removes it.
    internal static void Yield(){try{_view?.HideNow();}catch{}}

    // ---- shared with the view --------------------------------------------------------------------------------------
    internal static void Click(LobbyUiButton m)
    {
        var a=m?.OnClick;if(a==null)return;
        _clickDepth++;
        try{a();}
        catch(Exception e){LobbyCore.Warn("Multiplayer page action failed: {Error}",e.GetBaseException().Message);}   // the mod's own action
        finally{_clickDepth--;}
    }
    // every Interactive callback of the page view runs through here: nothing escapes into the game's event dispatch
    internal static void Guard(Action a)
    {
        _callback++;
        try{a();}
        catch(Exception e){Fault(e);}
        finally{_callback--;}
    }
    internal static void Fault(Exception e)
    {
        try
        {
            if(Gate.Fail(e)){Shutdown(false);return;}
            _blocked=_page?.Sig;                               // this capture stays original; the next page tries again
            Leave(LobbyUiNative.Live());
        }
        catch{}
    }

    // ---- panel / layer bookkeeping ---------------------------------------------------------------------------------
    static void HidePanel(ConnectionUI ui)
    {
        var p=LobbyUiNative.Panel(ui);if(p==null)return;
        if(p.visible)p.set_visible(false);
        _hidden=p;_ui=ui;
    }
    // only the panel we hid, and only while it is still the current one: a newer page keeps its own visible panel
    static void RestorePanel(ConnectionUI ui)
    {
        var p=_hidden;_hidden=null;if(p==null)return;
        try{var cur=LobbyUiNative.Panel(ui);if(cur!=null&&cur.HashlinkPointer==p.HashlinkPointer&&!cur.visible)cur.set_visible(true);}catch{}
    }
    static void Leave(ConnectionUI ui)
    {
        var v=_view;_view=null;_page=null;
        Retire(v);
        RestorePanel(ui??_ui);
        _ui=null;
        try{if(!LobbyUi.LayerUp)LobbyUi.TitleRestore();}catch{}
        try{LobbyUi.RenderOff();}catch{}
    }
    // A view whose Interactive may be running right now (our click or hover callback) is only hidden; it is removed at
    // the next P3, so no Interactive is taken out of the scene inside its own callback.
    static void Retire(LobbyUiPageView v)
    {
        if(v==null)return;
        try{v.HideNow();}catch{}
        if(_callback>0||_clickDepth>0){_grave.Add(v);return;}
        try{v.Dispose();}catch{}
    }
    static void DrainGrave()
    {
        if(_grave.Count==0||_callback>0||_clickDepth>0)return;
        var list=_grave.ToArray();_grave.Clear();
        foreach(var v in list)try{v.Dispose();}catch{}
    }
    // Part switch-off: everything back to the original pages. whole = the whole UI part tripped (LobbyUi restores the
    // title texts and the render hook itself).
    internal static void Shutdown(bool whole)
    {
        bool was=_on;_on=false;
        var v=_view;_view=null;_page=null;
        Retire(v);
        try{RestorePanel(LobbyUiNative.Live()??_ui);}catch{}
        _hidden=null;_ui=null;
        if(!whole){try{if(!LobbyUi.LayerUp)LobbyUi.TitleRestore();LobbyUi.RenderOff();}catch{}}
        if(was)LobbyCore.Warn("LobbyUi pages switched off; the original Multiplayer pages are shown again");
    }
}
