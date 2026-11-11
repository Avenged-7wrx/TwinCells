using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;
using HarmonyLib;

// Part UI of CoopLobby (SPEC.md "Part UI", brief.md 4.2-4.5, architecture B): the original ConnectionUI stays alive and
// does everything (network, hidden rebuilds, prompt, escape); while a lobby screen (host status / client waiting) is
// up, our own persistent layer is drawn and the original _panelRoot is hidden. Hidden parents make Interactives inert
// (h2d/Scene.hx:298-307), so only our buttons take clicks; they call the captured PendingButton.OnClick closures.
//   P1/P2  postfix LobbySession.ShowHostStatusMenu / ShowClientWaitingMenu: hide _panelRoot in the same call, capture
//          PendingButtons / PendingInfos (every lobby rebuild passes here, brief 1.2)
//   P3     postfix ModEntry.OnFrameUpdate(double), Priority.Last: gate, re-hide, 10 Hz model poll, per-frame animation
//   P4     dc.Hook_Boot.render, registered only while the layer exists: backstop for rebuilds and gate changes that run
//          after P3 (main-thread queue drained after orig in hook_boot_update, IL 34166-34172): hide a fresh _panelRoot,
//          or put the original back, before the frame is drawn
// Harmony only on plain managed statics (LobbySession) and ModEntry.OnFrameUpdate, never on ConnectionUI (a
// dc.ui.Process subclass: HashlinkFIndex routing, brief 4.1). Any fault: the layer goes, the original panel comes back.
public static class LobbyUi
{
    public const string Owner="CoopLobby.Ui.20261110";
    static readonly object Lock=new();
    static readonly FaultGate Gate=new("LobbyUi");
    static volatile bool _on,_active;
    static Harmony _harmony;
    static readonly List<MethodBase> _patched=new();
    static bool _renderHooked,_announced;
    static EventInfo _renderEvent;static Delegate _renderHook;
    static LobbyUiView _view;
    static ConnectionUI _ui;                 // the instance whose panel we hid
    static dc.h2d.Object _hidden;            // the _panelRoot we hid (restored on gate exit / trip)
    static List<LobbyUiButton> _buttons=new();static bool _hostBar,_captured,_barDirty,_pollSoon;static string _modeLabel,_exitLabel;
    static double _lastPoll,_lastTick;static int _clickDepth;

    static double Now=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;

    public static void Install()
    {
        lock(Lock)
        {
            if(_on)return;
            if(Gate.Tripped)throw new InvalidOperationException("LobbyUi switched off earlier in this session");
            try
            {
                LobbyModel.Prepare();
                LobbyUiNative.Bind();
                // The other Multiplayer pages (LobbyUiPages) are an optional feature of this part: bound before any patch,
                // patched after the lobby's patches as their own all-or-nothing step (PatchPages). A page-only problem
                // leaves every page original and the lobby screens installed exactly as validated (lobbyqa-full-06).
                bool pages=BindPages();
                _harmony=new Harmony(Owner);
                var show=new HarmonyMethod(typeof(LobbyUi).GetMethod(nameof(AfterShow),BindingFlags.NonPublic|BindingFlags.Static));
                Patch(LobbyUiNative.ShowHost,show);
                Patch(LobbyUiNative.ShowClient,show);
                Patch(LobbyUiNative.FrameUpdate,new HarmonyMethod(typeof(LobbyUi).GetMethod(nameof(AfterFrame),BindingFlags.NonPublic|BindingFlags.Static)){priority=Priority.Last});
                foreach(var t in _patched)
                {
                    var info=Harmony.GetPatchInfo(t);
                    if(info==null||info.Postfixes.Count(p=>p.owner==Owner)!=1||info.Prefixes.Any(p=>p.owner==Owner)||info.Transpilers.Any(p=>p.owner==Owner)||info.Finalizers.Any(p=>p.owner==Owner))
                        throw new InvalidOperationException("LobbyUi postfix was not installed exactly once on "+t.Name);
                }
                if(pages)pages=PatchPages();
                HookRender();
                _on=true;
                if(pages)LobbyUiPages.Enable();
            }
            catch{Rollback();throw;}
        }
    }
    static void Patch(MethodInfo target,HarmonyMethod postfix){_patched.Add(target);_harmony.Patch(target,postfix:postfix);}
    static void Rollback()
    {
        LobbyUiPages.Disable();
        UnhookRender();
        foreach(var t in _patched)try{_harmony?.Unpatch(t,HarmonyPatchType.All,Owner);}catch(Exception e){LobbyCore.Warn("LobbyUi rollback of {Method} failed: {Error}",t.Name,e.Message);}
        _patched.Clear();
    }
    // Reflection only (no patch yet). false: the pages stay original, nothing else changes. Logged once (Install runs once).
    static bool BindPages()
    {
        if(LobbyCore.Off("PAGES")){LobbyCore.Info("LobbyUi: Multiplayer pages switched off by COOP_LOBBY_PAGES_DISABLE");return false;}
        try{LobbyUiPages.Bind();return true;}
        catch(Exception e){LobbyCore.Warn("LobbyUi: Multiplayer pages not bound ({Error}); they stay original, the lobby screens are not affected",e.Message);return false;}
    }
    // The five page prefix + postfix pairs, all or none, with the same exactly-once check. On any failure only the page
    // targets are unpatched (the lobby's patches stay as installed and checked) and the pages stay original. Once all
    // five are in, they join _patched, so a later failure of the part (HookRender) rolls them back with the lobby's.
    // Leftovers of a failed unpatch are inert: the page hooks do nothing until LobbyUiPages.Enable.
    static bool PatchPages()
    {
        var done=new List<MethodBase>();
        try
        {
            foreach(var (target,prefix,postfix) in LobbyUiPages.Patches()){done.Add(target);_harmony.Patch(target,prefix:prefix,postfix:postfix);}
            foreach(var t in done)
            {
                var info=Harmony.GetPatchInfo(t);
                if(info==null||info.Postfixes.Count(p=>p.owner==Owner)!=1||info.Prefixes.Count(p=>p.owner==Owner)!=1||info.Transpilers.Any(p=>p.owner==Owner)||info.Finalizers.Any(p=>p.owner==Owner))
                    throw new InvalidOperationException("LobbyUi page patch was not installed exactly once on "+t.Name);
            }
            _patched.AddRange(done);
            return true;
        }
        catch(Exception e)
        {
            foreach(var t in done)try{_harmony.Unpatch(t,HarmonyPatchType.All,Owner);}catch(Exception u){LobbyCore.Warn("LobbyUi rollback of {Method} failed: {Error}",t.Name,u.Message);}
            LobbyCore.Warn("LobbyUi: Multiplayer page patches not installed ({Error}); the pages stay original, the lobby screens are not affected",e.GetBaseException().Message);
            return false;
        }
    }

    // ---- P1 / P2 -----------------------------------------------------------------------------------------------
    static void AfterShow()
    {
        if(!_on||Gate.Tripped)return;
        try
        {
            // a nested call returns before it rebuilds anything (IL 71625-71628, 71879-71882): nothing new to capture
            if((int)LobbyUiNative.RebuildDepth.GetValue(null)>0)return;
            var ui=LobbyUiNative.Live();
            if(!LobbyUiNative.GateNow(ui)||_overflow)return;
            LobbyUiPages.Yield();    // a Multiplayer page layer (if any) hides now; its own P3 removes it (never throws)
            Enter(ui);               // same call stack as the rebuild that just made a fresh, visible _panelRoot (IL 96361-96406)
            Capture();
            Sync(ui,false,0);
            Gate.Ok();
        }
        catch(Exception e){Fault(e);}
    }

    // ---- P3 ----------------------------------------------------------------------------------------------------
    static void AfterFrame()
    {
        if(!_on)return;
        if(!Gate.Tripped)
        {
            try{Frame();Gate.Ok();}
            catch(Exception e){Fault(e);}
        }
        LobbyUiPages.AfterFrame();   // own fault gate, never throws; also disposes page layers retired inside callbacks
    }
    static void Frame()
    {
        double now=Now,dt=_lastTick>0?now-_lastTick:0;_lastTick=now;
        var ui=LobbyUiNative.Live();
        if(!LobbyUiNative.GateNow(ui)){_overflow=false;if(_view!=null||_hidden!=null)Leave(ui);return;}
        if(_overflow)
        {
            // More than two players (CAP off, failed or tripped here, or a host without it): our two cards would hide a
            // player whose ready state gates the launch, so the original panel stays until the count is back to two.
            if(now-_lastPoll>=0.1){_lastPoll=now;LobbyModel.Poll();}
            if(LobbyModel.Players.Count>2)return;
            _overflow=false;
        }
        Enter(ui);
        if(!_captured)Capture();                             // PendingButtons stay valid until the next BeginMenu (IL 93658-93662)
        Sync(ui,true,dt);
    }
    // Shared by P1/P2 and P3 once the gate holds. A view built for another ConnectionUI goes first: EnsureCreated makes
    // a new instance when the title screen's parent changes (IL 96787-96803), and its first lobby menu can come before
    // the next P3; then the current panel is hidden and Sync builds a view for this instance in the same call.
    static void Enter(ConnectionUI ui)
    {
        if(_view!=null&&!_view.Valid(ui))Leave(_ui);         // a new ConnectionUI (title screen rebuilt) or a new root
        HidePanel(ui);
    }
    static bool _overflow;static double _titleHiddenAt;
    // The title screen's build line, controller tip and pad warning (pr.TitleScreen fields 24 build, 27 help,
    // 41 padWarning, game bytecode t@1306) are drawn above ConnectionUI.root, so they showed through the lobby next to
    // our hint line (native capture lobbyqa-full-01). Hidden while the layer is up, restored exactly on Leave.
    static readonly List<dc.h2d.Object> _hiddenTitle=new();
    static System.Reflection.MethodInfo _getTitle;
    static void HideTitleTexts()
    {
        try
        {
            _getTitle??=LobbyCore.Method(LobbyCore.ModType("DeadCellsMultiplayerMod.LobbySession"),"GetTitleScreen",true,typeof(dc.pr.TitleScreen));
            if(!(_getTitle.Invoke(null,null) is dc.pr.TitleScreen ts)||ts.HashlinkPointer==IntPtr.Zero)return;
            foreach(dc.h2d.Object o in new dc.h2d.Object[]{ts.build,ts.help,ts.padWarning})
            {
                if(o==null||o.HashlinkPointer==IntPtr.Zero||!o.visible)continue;
                o.set_visible(false);
                if(!_hiddenTitle.Exists(x=>x.HashlinkPointer==o.HashlinkPointer))_hiddenTitle.Add(o);   // listed once
            }
        }
        catch{}
    }
    static void RestoreTitleTexts()
    {
        foreach(var o in _hiddenTitle){try{if(o!=null&&o.HashlinkPointer!=IntPtr.Zero&&!o.visible)o.set_visible(true);}catch{}}
        _hiddenTitle.Clear();
    }
    static void Sync(ConnectionUI ui,bool frame,double dt)
    {
        bool created=false;
        if(_view==null)
        {
            _view=new LobbyUiView(ui);_active=true;created=true;_barDirty=true;
            if(!_announced){_announced=true;LobbyCore.Info("LobbyUi layer up (host screen {Host})",_hostBar);}
        }
        // The title screen re-shows its help text on menu rebuilds (native capture lobbyqa-full-03 after a hide done
        // once per view), so hide again on every Show*Menu rebuild (frame=false) and at most 4 times a second otherwise.
        double tnow=Now;
        if(created||!frame||tnow-_titleHiddenAt>=0.25){_titleHiddenAt=tnow;HideTitleTexts();}
        if(!_renderHooked)AddRender();                       // P4 only while a view exists; retried here if it threw
        _view.Show();
        // never rebuild the bar from inside one of its own click callbacks; the next frame does it
        if(_barDirty&&_clickDepth==0){_view.SetButtons(_buttons,_hostBar,_modeLabel,_exitLabel);_barDirty=false;}   // stays dirty (retried) if it threw
        double now=Now;
        if(created||_pollSoon||(frame&&now-_lastPoll>=0.1))
        {
            _pollSoon=false;_lastPoll=now;LobbyModel.Poll();
            if(LobbyModel.Players.Count>2){_overflow=true;LobbyCore.Info("LobbyUi: {Count} players in the lobby, original panel shown",LobbyModel.Players.Count);Leave(ui);return;}
            _view.Apply();
        }
        if(frame)_view.Tick(dt);
    }
    static void Capture()
    {
        var list=LobbyUiNative.PendingButtons.GetValue(null) as IList;
        var infos=LobbyUiNative.PendingInfos.GetValue(null) as IList;
        var bs=new List<LobbyUiButton>(list?.Count??0);
        if(list!=null)foreach(var b in list)
        {
            if(b==null)continue;
            bs.Add(new LobbyUiButton{Label=(string)LobbyUiNative.BLabel.GetValue(b)??"",Help=(string)LobbyUiNative.BHelp.GetValue(b)??"",
                Enabled=(bool)LobbyUiNative.BEnabled.GetValue(b),Color=(int)LobbyUiNative.BColor.GetValue(b),OnClick=LobbyUiNative.BOnClick.GetValue(b) as Action});
        }
        string mode=null;
        if(infos!=null)foreach(var i in infos){var m=i==null?null:LobbyUiRules.SelectedMode(LobbyUiNative.IText.GetValue(i) as string);if(m!=null){mode=m;break;}}
        bool host=LobbyUiNative.HostScreen;
        // the exit button's label is the mod's GetString("Back") / ("Disconnect") in the language of this capture
        _buttons=bs;_hostBar=host;_modeLabel=mode;_exitLabel=LobbyUiNative.ModString(host?"Back":"Disconnect");_captured=true;_barDirty=true;
    }
    static void HidePanel(ConnectionUI ui)
    {
        var p=LobbyUiNative.Panel(ui);if(p==null)return;
        if(p.visible)p.set_visible(false);     // the Haxe setter, as the mod calls it (Object::set_visible + pop, IL 96404-96407)
        _hidden=p;_ui=ui;
    }
    static void RestorePanel(ConnectionUI ui)
    {
        var p=_hidden;_hidden=null;if(p==null)return;
        try{var cur=LobbyUiNative.Panel(ui);if(cur!=null&&cur.HashlinkPointer==p.HashlinkPointer&&!cur.visible)cur.set_visible(true);}catch{}
    }
    static void Leave(ConnectionUI ui)
    {
        var v=_view;_view=null;_active=false;
        if(!LobbyUiPages.HasView)UnhookRender();             // a Multiplayer page layer still needs the render backstop
        try{v?.Dispose();}catch{}
        // CAP's "lobby full" popup is on its way: re-showing the stale waiting panel would flash it (~0.25 s); the
        // popup commits a fresh panel itself.
        if(LobbyShared.PopupPending)_hidden=null;else RestorePanel(ui??_ui);
        if(!LobbyUiPages.HasView)RestoreTitleTexts();        // the page layer that took over keeps them hidden
        _ui=null;_captured=false;_barDirty=false;_buttons=new List<LobbyUiButton>();
    }

    // ---- P4 ----------------------------------------------------------------------------------------------------
    // The generated hook delegate types (Hook_Boot.hook_render / orig_render) derive from the runtime CoreLib's
    // MulticastDelegate, which this add-on (compiled against reference assemblies) cannot name (CS0012). Same adapter as
    // CoopFrameFix PlayerTint.cs:85-100: a DynamicMethod with the exact handler signature that calls BeforeRender (never
    // throws) and then orig(self, engine).
    // The handler is registered only while a view exists (Sync adds it, Leave / Shutdown remove it), so runs pay
    // nothing for it: DCCM rebuilds the handler chain on every Boot.render call, one link per handler
    // (HashlinkHookManager.HookEntry). Install adds and removes it once: the framework creates the Boot.render hook
    // manager and its native hook on the first add (HashlinkHooks.GetManager), so that happens at install, as
    // CoopFrameFix PlayerTint does, and both calls are proven before the part goes on. Later adds / removes only edit
    // the handler list (HashlinkHookManager.AddHook / RemoveHook), which is safe during a dispatch: the chain is built
    // before it is invoked. The empty list keeps the framework's ordinary hooked state (PlayerTint.cs:34-35).
    static void HookRender()
    {
        _renderHook=BuildRenderAdapter(out _renderEvent);
        _renderHooked=true;_renderEvent.AddEventHandler(null,_renderHook);
        _renderEvent.RemoveEventHandler(null,_renderHook);_renderHooked=false;   // a throw here: Rollback removes it again
    }
    static void AddRender()
    {
        if(_renderHooked||_renderEvent==null||_renderHook==null)return;
        try{_renderEvent.AddEventHandler(null,_renderHook);_renderHooked=true;}
        catch{try{_renderEvent.RemoveEventHandler(null,_renderHook);}catch{}throw;}   // removing an absent handler is a no-op
    }
    // Reflection + DynamicMethod only (no hook registered): notes\ui\bindcheck.ps1 validates it offline.
    internal static Delegate BuildRenderAdapter(out EventInfo ev)
    {
        ev=typeof(dc.Hook_Boot).GetEvent("render")??throw new MissingMemberException("Hook_Boot","render");
        var invoke=ev.EventHandlerType.GetMethod("Invoke");var ps=invoke?.GetParameters();
        if(ps==null||invoke.ReturnType!=typeof(void)||ps.Length!=3||ps[1].ParameterType!=typeof(dc.Boot)||ps[2].ParameterType!=typeof(dc.h3d.Engine))
            throw new InvalidOperationException("Unexpected Boot.render hook signature");
        var types=ps.Select(p=>p.ParameterType).ToArray();var orig=types[0].GetMethod("Invoke");
        if(orig==null||orig.ReturnType!=typeof(void)||orig.GetParameters().Length!=2)throw new InvalidOperationException("Unexpected Boot.render original signature");
        var adapter=new DynamicMethod("CoopLobby_UiRender",typeof(void),types,typeof(LobbyUi).Module,true);
        var il=adapter.GetILGenerator();
        il.Emit(OpCodes.Call,typeof(LobbyUi).GetMethod(nameof(BeforeRender),BindingFlags.Static|BindingFlags.NonPublic));
        for(int i=0;i<types.Length;i++)il.Emit(OpCodes.Ldarg,(short)i);
        il.Emit(OpCodes.Callvirt,orig);il.Emit(OpCodes.Ret);
        var hook=adapter.CreateDelegate(ev.EventHandlerType);RuntimeHelpers.PrepareDelegate(hook);
        return hook;
    }
    static void UnhookRender(){if(!_renderHooked)return;_renderHooked=false;try{_renderEvent.RemoveEventHandler(null,_renderHook);}catch{}}
    static void BeforeRender()
    {
        try{if(_active&&!Gate.Tripped)Backstop();}
        catch(Exception e){try{Fault(e);}catch{}}
        LobbyUiPages.Backstop();     // own fault gate, never throws
    }
    static void Backstop()
    {
        var v=_view;if(v==null)return;
        var ui=LobbyUiNative.Live();
        // the title screen is dying (launch) or was replaced: never draw the stale layer over it; P3 does the rest (the
        // old panel is not shown here, so a launch never flashes it)
        if(!ReferenceEquals(ui,v.Ui)){v.HideNow();return;}
        // the lobby was left on this same ConnectionUI after P3: put the original back before this frame is drawn, or the
        // frame has neither panel. Client when the host quits: NotifyRemoteDisconnected clears both flags (IL 72421-72423)
        // and commits no menu (its queued refresh does nothing without a flag, IL 177459-177468), and it can run after
        // P3 (main-thread queue drained after orig update, IL 34170-34172). After Back etc. the panel is a new one and
        // Leave shows nothing old.
        if(!LobbyUiNative.GateNow(ui)){Leave(ui);return;}
        HidePanel(ui);
        // the title screen turns its help text back on during its own update (lobbyqa-full-03/-04 captures): re-hide
        // the cached objects right before this frame is drawn (three proxy reads, no allocation)
        foreach(var o in _hiddenTitle){if(o!=null&&o.HashlinkPointer!=IntPtr.Zero&&o.visible)o.set_visible(false);}
    }

    // ---- shared with the view ----------------------------------------------------------------------------------
    internal static void Click(LobbyUiButton m)
    {
        var a=m?.OnClick;if(a==null)return;
        _clickDepth++;
        try{a();}
        catch(Exception e){LobbyCore.Warn("Lobby button action failed: {Error}",e.GetBaseException().Message);}   // the mod's own action, not a UI fault
        finally{_clickDepth--;_pollSoon=true;}
    }
    public static void PollSoon(){_pollSoon=true;}

    // ---- shared with the Multiplayer pages (LobbyUiPages) ------------------------------------------------------------
    // Title texts and the Boot.render handler are shared: hidden / registered while either layer is up, given back
    // only when neither is.
    internal static bool Running=>_on&&!Gate.Tripped;
    internal static bool LayerUp=>_view!=null;
    internal static void YieldView(){try{_view?.HideNow();}catch{}}   // a page took over: hide now, P3 Leave disposes
    internal static void TitleHide()=>HideTitleTexts();
    internal static void TitleRehide(){foreach(var o in _hiddenTitle){try{if(o!=null&&o.HashlinkPointer!=IntPtr.Zero&&o.visible)o.set_visible(false);}catch{}}}
    internal static void TitleRestore()=>RestoreTitleTexts();
    internal static void RenderOn(){if(!_renderHooked)AddRender();}
    internal static void RenderOff(){if(_view==null&&!LobbyUiPages.HasView)UnhookRender();}
    public static void Fault(Exception e){if(Gate.Fail(e))Shutdown();}
    // Trip: back to exactly the original UI for the rest of the session. The Harmony postfixes stay as no-ops (unpatching
    // a method from inside its own postfix is avoided); the render hook is removed as CoopFrameFix PlayerTint does.
    static void Shutdown()
    {
        try{LobbyUiPages.Shutdown(true);}catch{}              // the whole UI part goes: the pages too
        _active=false;
        var v=_view;_view=null;
        try{v?.Dispose();}catch{}
        try{var ui=LobbyUiNative.Live()??_ui;var p=LobbyUiNative.Panel(ui);if(p!=null&&!p.visible&&LobbyUiNative.GateNow(ui))p.set_visible(true);}catch{}
        _hidden=null;_ui=null;
        RestoreTitleTexts();
        UnhookRender();
        LobbyCore.Warn("LobbyUi switched off; the original lobby panel is shown again");
    }
}
