using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using dc.libs.heaps.slib;
using DeadCellsMultiplayerMod;

// In-run display of the lobby colours, all on the game thread.
//
// Body tint: CoopFrameFix PlayerTint's approach (Load1 PlayerTint.cs:83-100, 169-227): one adapter on the framework's
// Boot.render event (not Harmony) multiplies sprite.color x/y/z of the player bodies once around the whole render and
// restores the recorded values in a finally block. Bodies: the local hero (Game.ME.hero.spr, own net id), every ghost
// (ModEntry.clients[i].spr, ModEntry.clientIds[i]) - the same sprites CoopSyncFixes tints (CSF Colors.cs:38-45) - and
// the partner's downed corpse (ModEntry._remoteDownedCines[userId]._corpse.spr; release IL 22673-22677 keys it by
// RemoteDownedState.UserId), which neither CSF nor CFF colours (player-color verify.md section 2 item 3): while the
// partner is downed the ghost is hidden (RemoteDownedCorpse.HideGhost) and that corpse is the body on screen.
// Colour = LobbyPalette multipliers of LobbyColorState.ColorOf(id): the chosen colour, or the defaults (host Ember,
// client Ice = CSF's pair-profile multipliers) when the partner has no colour support.
//
// No double tint: the body tint is mandatory for the part (Install throws when it cannot bind) and LobbyColor goes live
// only while Check says we own the bodies: COOP_SYNC_DISABLE_COLORS=1 (CSF then never installs its draw handler,
// Colors.cs:20, and CFF never hooks render, PlayerTint.cs:78) AND CSF's BodyDrawHook is null AND CFF's
// PlayerTint.Current is neither TakenOver nor Waiting. Re-checked every 2 s; any other answer switches the whole part
// off (LobbyColor.SwitchOff -> Off), logged once.
//
// Partner label and minimap dot (SPEC: the PARTNER's surfaces; the local hero keeps the game's red dot) are recoloured
// only while the partner supports colours (LobbyColorState.CanPick); otherwise they stay exactly as the mod draws them
// (COLORS["ST"] label, 0xE32D2D dot, IL 57317-57322 / 56836-56862).
// Label: the ghost nickname is a dc.ui.Text child of the ghost sprite kept in GhostHero._labels[king] (SetLabel, IL
// 57315-57439, textColor COLORS["ST"] at IL 57432); the holder is the static ModEntry._ghost, replaced per level
// (verify.md section 1). Only SetLabel writes textColor (verify.md item 9), so a write sticks until the label is
// recreated; it is re-applied at 10 Hz. Minimap: the dot of a tracker is a BatchElement whose r/g/b only
// MiniMap.track writes (bytecode f@3307 op157/187/213 from the colour argument; verify.md item 8); track replaces the
// tracker object (stopTracking first, op42) and postUpdate drops trackers of destroyed entities, so trackers are
// matched by entity pointer at 4 Hz, not cached. Every original value is recorded before the first write and put
// back when the partner loses support, the session ends or the part switches off.
public static class LobbyColorTint
{
    const BindingFlags Any=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;
    enum Mode{Off,Waiting,Active,Blocked}
    struct Saved{public dc.h3d.Vector Color;public double R,G,B;}
    static Mode _mode=Mode.Off;
    static string _reason="not installed",_loggedReason;
    static EventInfo _renderEvent;static Delegate _renderHook;
    static readonly Saved[] Slots=new Saved[8];
    static int Used,Depth;
    static long _nextCheck,_nextLabels,_nextDots;
    // optional surfaces, each validated at install
    static FieldInfo _ghostField,_labelsField,_downedField,_corpseField,_miniMapField;
    static PropertyInfo _labelProp;
    static readonly Dictionary<IntPtr,(dc.ui.Text Label,int Original)> _labels=new();
    static readonly Dictionary<IntPtr,(dc.ui.hud.MMTracker Tracker,double R,double G,double B)> _dots=new();
    static readonly Dictionary<IntPtr,int> _ids=new();

    public static string Describe()=>"body tint bound, owner checked at the first frame; label "+(_labelsField!=null)+", minimap "+(_miniMapField!=null)+", downed corpse "+(_downedField!=null);

    public static void Install()
    {
        BindRender();       // mandatory: throws, and LobbyColor.Install rolls back
        Surface("nickname label",()=>
        {
            var ghostType=LobbyCore.ModType("DeadCellsMultiplayerMod.GhostHero");
            _ghostField=LobbyCore.Field(typeof(ModEntry),"_ghost",ghostType,true);
            var f=ghostType.GetField("_labels",LobbyCore.I);
            var t=f?.FieldType;
            if(t==null||!t.IsGenericType||t.GetGenericTypeDefinition()!=typeof(Dictionary<,>)||t.GetGenericArguments()[0]!=typeof(dc.Entity))throw new InvalidOperationException("Pinned field mismatch: GhostHero._labels");
            var label=t.GetGenericArguments()[1].GetProperty("Label",LobbyCore.I);
            if(label?.PropertyType!=typeof(dc.ui.Text)||label.GetMethod==null)throw new InvalidOperationException("Pinned property mismatch: GhostHero.LabelState.Label");
            _labelsField=f;_labelProp=label;
        });
        Surface("downed corpse",()=>
        {
            var corpse=LobbyCore.ModType("DeadCellsMultiplayerMod.RemoteDownedCorpse");
            var f=LobbyCore.Field(typeof(ModEntry),"_remoteDownedCines",typeof(Dictionary<,>).MakeGenericType(typeof(int),corpse),false);
            _corpseField=LobbyCore.Field(corpse,"_corpse",typeof(dc.en.HeroDeadCorpse),false);
            _downedField=f;
        });
        Surface("minimap dot",()=>{_miniMapField=LobbyCore.Field(typeof(ModEntry),"miniMap",typeof(dc.ui.hud.MiniMap),true);});
        _mode=Mode.Waiting;_reason="not checked yet";_nextCheck=0;
    }
    static void Surface(string name,Action bind)
    {
        try{bind();}
        catch(Exception e){LobbyCore.Warn("Colour surface {Surface} off: {Error}",name,e.Message);}
    }
    // Render adapter with the exact delegate signature of the generated hook type (its delegate types cannot be named
    // from here), copied from CoopFrameFix PlayerTint.cs:83-100.
    static void BindRender()
    {
        var proxy=typeof(HSprite).Assembly;
        var ev=proxy.GetType("dc.Hook_Boot",true).GetEvent("render")??throw new InvalidOperationException("Boot.render hook event missing");
        var invoke=ev.EventHandlerType.GetMethod("Invoke");var parameters=invoke.GetParameters();
        if(parameters.Length!=3||invoke.ReturnType!=typeof(void)||parameters[1].ParameterType.FullName!="dc.Boot"||parameters[2].ParameterType.FullName!="dc.h3d.Engine")
            throw new InvalidOperationException("Unexpected Boot.render hook signature");
        var types=parameters.Select(p=>p.ParameterType).ToArray();var origInvoke=types[0].GetMethod("Invoke");
        if(origInvoke==null||origInvoke.ReturnType!=typeof(void)||origInvoke.GetParameters().Length!=types.Length-1)throw new InvalidOperationException("Unexpected Boot.render original signature");
        var adapter=new DynamicMethod("CoopLobby_ColorRenderTint",typeof(void),types,typeof(LobbyColorTint).Module,true);
        var il=adapter.GetILGenerator();
        il.Emit(OpCodes.Call,typeof(LobbyColorTint).GetMethod(nameof(BeforeRender),BindingFlags.Static|BindingFlags.NonPublic));     // never throws
        il.BeginExceptionBlock();
        for(int i=0;i<types.Length;i++)il.Emit(OpCodes.Ldarg,(short)i);
        il.Emit(OpCodes.Callvirt,origInvoke);
        il.BeginFinallyBlock();
        il.Emit(OpCodes.Call,typeof(LobbyColorTint).GetMethod(nameof(AfterRender),BindingFlags.Static|BindingFlags.NonPublic));      // never throws
        il.EndExceptionBlock();il.Emit(OpCodes.Ret);
        var hook=adapter.CreateDelegate(ev.EventHandlerType);RuntimeHelpers.PrepareDelegate(hook);
        ev.AddEventHandler(null,hook);
        _renderEvent=ev;_renderHook=hook;
    }
    public static void Uninstall()
    {
        _mode=Mode.Off;
        var hook=_renderHook;_renderHook=null;
        if(hook!=null)try{_renderEvent.RemoveEventHandler(null,hook);}catch{}
    }
    // Part tripped: put everything back and stop being called every frame.
    public static void Off()
    {
        _mode=Mode.Off;
        try{Restore();}catch{}
        RestoreSurfaces();
        Uninstall();
    }

    // ---- frame tick (LobbyColor.AfterFrame) ----
    // null while our render tint owns the player bodies, otherwise why not. The first call decides at once (DCCM has
    // initialized every mod, CSF and CFF included, before the first frame); then every 2 s. Logged when it changes.
    public static string Check(long now)
    {
        if(_renderHook==null||_mode==Mode.Off)return _reason??"render tint not bound";
        if(now>=_nextCheck||now<_nextCheck-2000)
        {
            _nextCheck=now+2000;
            var reason=BlockReason();
            var next=reason==null?Mode.Active:Mode.Blocked;
            if(next!=_mode||reason!=_reason)
            {
                _mode=next;_reason=reason;
                if(next==Mode.Active)LobbyCore.Info("Colour body tint active (labels {Labels}, minimap {Dots}, downed corpse {Corpse})",_labelsField!=null,_miniMapField!=null,_downedField!=null);
                else if(_loggedReason!=reason){_loggedReason=reason;LobbyCore.Info("Colour body tint not applied: {Reason}; in-run colours stay as drawn today",reason);}
            }
        }
        return _mode==Mode.Active?null:_reason;
    }
    public static void Tick(long now,bool session)
    {
        // Partner surfaces only with a partner that supports colours; otherwise exactly the mod's own look.
        if(_mode!=Mode.Active||!session||!LobbyColorState.CanPick){RestoreSurfaces();return;}
        if(now>=_nextLabels||now<_nextLabels-100){_nextLabels=now+100;try{ApplyLabels();}catch{}}
        if(now>=_nextDots||now<_nextDots-250){_nextDots=now+250;try{ApplyDots();}catch{}}
    }
    static string BlockReason()
    {
        // CSF reads the switch once at its own start (Colors.cs:20, 37); LobbyColor.Install already refused without it.
        if(Environment.GetEnvironmentVariable("COOP_SYNC_DISABLE_COLORS")!="1")return "COOP_SYNC_DISABLE_COLORS is not 1";
        {
            // CoopPack: both colour owners are parts of this assembly; each counts only while its part is present.
            Type t=global::CoopPackRuntime.PackParts.FindType("CoopSyncFixes","CoopSyncFixes");
            if(t!=null)
            {
                var hook=t.GetField("BodyDrawHook",Any);
                if(hook==null||!typeof(Delegate).IsAssignableFrom(hook.FieldType))return "unknown CoopSyncFixes build (no BodyDrawHook)";
                if(hook.GetValue(null)!=null)return "the CoopSyncFixes draw tint is active";
            }
            t=global::CoopPackRuntime.PackParts.FindType("CoopFrameFix","PlayerTint");
            if(t!=null)
            {
                var current=t.GetProperty("Current",BindingFlags.Static|BindingFlags.Public);
                if(current==null)return "unknown CoopFrameFix build (no PlayerTint.Current)";
                var state=current.GetValue(null)?.ToString();
                if(state=="TakenOver"||state=="Waiting")return "the CoopFrameFix render tint is "+state;
            }
        }
        return null;
    }

    // ---- body tint (inside Boot.render) ----
    static void BeforeRender()
    {
        try
        {
            if(!LobbyColorRules.EnterRender(ref Depth))return;     // only the outermost render of a frame tints
            if(Used>0)Restore();
            if(_mode!=Mode.Active||!LobbyColorState.PartRunning)return;
            try{Tint();}
            catch{Restore();}
        }
        catch{}
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Tint()
    {
        var net=ModEntry._net;
        if(net==null||!net.IsAlive)return;
        int local=net.IsHost?LobbyColorRules.HostId:net.id;
        try{Consider(dc.pr.Game.Class.ME?.hero?.spr,local);}catch{}
        var clients=ModEntry.clients;var ids=ModEntry.clientIds;
        if(clients!=null&&ids!=null)for(int i=0;i<clients.Length&&i<ids.Length&&Used<Slots.Length;i++){try{Consider(clients[i]?.spr,ids[i]);}catch{}}
        if(_downedField!=null&&ModEntry.Instance!=null&&_downedField.GetValue(ModEntry.Instance) is IDictionary downed&&downed.Count>0)
            foreach(DictionaryEntry e in downed)
                try{if(e.Key is int id&&e.Value!=null&&_corpseField.GetValue(e.Value) is dc.en.HeroDeadCorpse corpse&&!corpse.destroyed)Consider(corpse.spr,id);}catch{}
    }
    static void Consider(HSprite sprite,int id)
    {
        if(sprite==null||Used>=Slots.Length||!LobbyColorRules.ValidId(id))return;
        var color=sprite.color;if(color==null)return;
        for(int i=0;i<Used;i++)if(Slots[i].Color.HashlinkPointer==color.HashlinkPointer)return;      // one colour vector is multiplied once
        var (r,g,b)=LobbyPalette.Multipliers(LobbyColorState.ColorOf(id));
        var saved=new Saved{Color=color,R=color.x,G=color.y,B=color.z};
        Slots[Used++]=saved;                 // recorded before the first write: Restore always puts the three values back
        color.x=saved.R*r;color.y=saved.G*g;color.z=saved.B*b;
    }
    static void Restore()
    {
        for(int i=Used-1;i>=0;i--)
        {
            try{var s=Slots[i];s.Color.x=s.R;s.Color.y=s.G;s.Color.z=s.B;}catch{}
            Slots[i]=default;
        }
        Used=0;
    }
    static void AfterRender()
    {
        try{if(LobbyColorRules.LeaveRender(ref Depth)&&Used>0)Restore();}
        catch{}
    }

    // ---- partner label and minimap dot (frame tick) ----
    // Partner entities only (the ghosts, ModEntry.clients[i] by clientIds[i]); the local hero is never in this map, so
    // its own minimap dot keeps the game's 0xE32D2D (en.Hero.minimapTracking f@1970 op6-15).
    static void PartnerIds()
    {
        _ids.Clear();
        var net=ModEntry._net;if(net==null||!net.IsAlive)return;
        int local=net.IsHost?LobbyColorRules.HostId:net.id;
        var clients=ModEntry.clients;var ids=ModEntry.clientIds;
        if(clients!=null&&ids!=null)for(int i=0;i<clients.Length&&i<ids.Length;i++)
            if(clients[i]!=null&&LobbyColorRules.ValidId(ids[i])&&ids[i]!=local)_ids[clients[i].HashlinkPointer]=ids[i];
    }
    static readonly HashSet<IntPtr> _seen=new();
    static void ApplyLabels()
    {
        if(_labelsField==null)return;
        _seen.Clear();
        if(_ghostField.GetValue(null) is object ghost&&_labelsField.GetValue(ghost) is IDictionary labels&&labels.Count>0)
        {
            PartnerIds();
            foreach(DictionaryEntry e in labels)
            {
                if(!(e.Key is dc.Entity entity)||e.Value==null||!_ids.TryGetValue(entity.HashlinkPointer,out int id))continue;
                if(!(_labelProp.GetValue(e.Value) is dc.ui.Text label))continue;
                var key=label.HashlinkPointer;
                if(!_labels.ContainsKey(key))_labels[key]=(label,label.textColor);
                int rgb=LobbyPalette.Rgb(LobbyColorState.ColorOf(id));
                if(label.textColor!=rgb)label.textColor=rgb;
                _seen.Add(key);
            }
        }
        foreach(var key in _labels.Keys.Where(k=>!_seen.Contains(k)).ToList())_labels.Remove(key);     // recreated or gone: nothing to put back
    }
    static void ApplyDots()
    {
        if(_miniMapField==null)return;
        _seen.Clear();
        if(_miniMapField.GetValue(null) is dc.ui.hud.MiniMap map&&!map.destroyed&&map.trackers is dc.hl.types.ArrayObj trackers)
        {
            PartnerIds();
            int n=Math.Min(trackers.length,1024);
            for(int i=0;i<n&&_ids.Count>0;i++)
            {
                if(!(trackers.getDyn(i) is dc.ui.hud.MMTracker t))continue;
                var e=t.e;if(e==null||!_ids.TryGetValue(e.HashlinkPointer,out int id))continue;
                var dot=t.dot;if(dot==null)continue;             // only trackers made with a colour have a dot (f@3307 op90-94)
                var key=t.HashlinkPointer;
                if(!_dots.ContainsKey(key))_dots[key]=(t,dot.r,dot.g,dot.b);
                int rgb=LobbyPalette.Rgb(LobbyColorState.ColorOf(id));
                double r=LobbyColorRules.Channel(rgb,16),g=LobbyColorRules.Channel(rgb,8),b=LobbyColorRules.Channel(rgb,0);
                if(dot.r!=r||dot.g!=g||dot.b!=b){dot.r=r;dot.g=g;dot.b=b;}
                _seen.Add(key);
            }
        }
        foreach(var key in _dots.Keys.Where(k=>!_seen.Contains(k)).ToList())_dots.Remove(key);
    }
    static void RestoreSurfaces()
    {
        if(_labels.Count>0)
        {
            foreach(var s in _labels.Values)try{s.Label.textColor=s.Original;}catch{}
            _labels.Clear();
        }
        if(_dots.Count>0)
        {
            foreach(var s in _dots.Values)try{var dot=s.Tracker.dot;if(dot!=null){dot.r=s.R;dot.g=s.G;dot.b=s.B;}}catch{}
            _dots.Clear();
        }
    }
}
