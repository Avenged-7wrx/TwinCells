using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using HaxeProxy.Runtime;
using ModCore.Utilities;
using R=CoopPartyHudRules;

// CoopPartyHud (Alpha2 U16, OWNER-DECISIONS "Partner HUD rework", layout B). Rules and wire: CoopPartyHudRules.cs,
// CoopPartyHudWire.cs; pins: CoopPartyHudPins.cs.
//
// Design: the base mod keeps building, updating and removing its own party plate (MultiplayerUI.initkingLife IL:86638,
// UpdateSlotLabel IL:86984, UpdateLifeBar IL:87020, DrawPartyChip IL:87159, ClearSlots / RemoveInactiveSlots). This part
// only ADDS one layer (an h2d.Object with its own Graphics and five texts) as the last child of the plate's inner panel
// (LifeSlot.LabelBox, inside the owner FlowBox the base adds to HUD.topRightFlowT), and hides the base plate's three
// visuals (ChipGraphics, LabelText, StatusText) with the Haxe set_visible (f@1377). The base goes on updating its hidden
// plate every frame, so reverting is instant and exact: remove the layer, show the three visuals again. The flow, the
// slot lifecycle and the removal stay the base mod's (the layer is a child of the base panel and goes with it).
// Patches (release names, IL-pinned, HarmonyX owner below):
//   initkingLife postfix      builds the layer for the slot it returns (__result)
//   UpdateSlotLabel prefix    reads the FULL display name before the original trims it (FitDisplayName, 9 chars); never skips
//   UpdateLifeBar postfix     "current / max" text and the bar fill from the remote snapshot's Life/MaxLife (args 1, 2)
//   NetNode.HandleLine prefix the CPHUD1| stats lines (CoopPartyHudWire)
//   ModEntry.OnFrameUpdate postfix  stats in (inbox) and out (own hero's tiers: on change and as a 2 s heartbeat)
// DrawPartyChip and KingLifeUpdate are not patched; they are IL-pinned because the layer relies on their geometry
// (the base plate is at most 303 x 45 incl. shadow, inside the layer's 300+ x 56+3) and on their call order.
// Texts: the plain name goes to ui.Text.set_text, which escapes [<>&] itself (f@3411); every set_text is followed by
// onResize (f@3413: font and scale for the current pixel scale) before measuring, as the base does; a 1 s tick lays the
// panel out again when a text's scale, font or size changed from outside (CoopPartyHudRules.MetricsChanged).
// Fail-safe: every body in try/catch; the FIRST fault switches the part off for the session and reverts every panel to
// the base plate (CoopPartyHudRules.Gate). Kill switch COOP_PARTY_HUD_DISABLE=1: nothing is patched (base plate, no wire).
public static class CoopPartyHudPatch
{
    public const string Owner="CoopPartyHud.Panel.20261112";
    public const string Switch="COOP_PARTY_HUD_DISABLE";
    const BindingFlags S=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
    const BindingFlags I=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    delegate bool ClientIndexFn(int localId,int remoteId,out int index);
    static readonly object InstallGate=new();
    static volatile bool Published;
    public static bool Disabled{get;private set;}
    public static string InitializationError{get;private set;}
    public static bool Ready=>Published;
    internal static readonly R.Gate Gate=new();
    public static bool Running=>Published&&!Disabled&&!Gate.Tripped;
    public static string TrippedBy{get;private set;}

    // ---- bound base members ----
    static Func<object,int> SlotIndexOf;
    static Func<object,object> ContainerOf,PanelOf,ChipOf,LabelOf,StatusOf;
    static ClientIndexFn ClientIndex;static Func<bool> InActualRun;
    static Func<dc.h2d.Text,int> TextWidth,TextHeight;

    // ---- CoopLobby colour (read only, by reflection: LobbyColorState.ColorOf / LobbyPalette.Rgb of CoopPack or CoopLobby) ----
    static Func<int,int> LobbyColorOf,LobbyRgb;static int LobbyCount;static long LobbyLookMs;static string LobbySource;static bool LobbyWarned;
    // ---- game stat colours (Options.getTierColor) ----
    static readonly int[] StatColors={-1,-1,-1};static long StatColorMs;static bool StatColorFromGame,StatColorWarned;

    // ---- panels (game thread) ----
    sealed class Panel
    {
        public int SlotIndex;public WeakReference<object> Slot;
        public dc.h2d.Object Base,Flow;public dc.h2d.Object BaseChip;public dc.ui.Text BaseLabel,BaseStatus;
        public dc.h2d.Object Layer;public dc.h2d.Graphics G;public dc.ui.Text Name,Hp;public readonly dc.ui.Text[] Stat=new dc.ui.Text[3];
        public string NameText,RawName;public int NameColor=-1;public double NameScale=R.NameScale,Width=R.BaseWidth,NameWidth,NameRoom,HpWidth;public bool Widened,Overflow,StatsDropped;public int FitTries;
        public int Life=int.MinValue,Max=int.MinValue;public string HpText;public int HpColor=-1;
        public bool StatsShown,StatsVisible;public R.Stats ShownStats=new R.Stats(-1,-1,-1);public readonly int[] StatColor={-1,-1,-1};public readonly double[] StatWidth=new double[3];
        public int RemoteId,LocalId=-1;public long ColorMs;public bool NeedLayout=true,Reverted;public string RevertReason;public long Layouts,Draws,MetricRelayouts;
        // Text metrics the last layout used: per text (name, HP, stat 0..2) scaleX, scaleY, raw width, raw height.
        public readonly double[] Laid=new double[MetricCount];
    }
    const int MetricCount=5*4,NameMetric=0,HpMetric=1,StatMetric=2;   // text index: name 0, HP 1, stats 2..4
    static void Measure(dc.ui.Text t,double[] into,int index)
    {
        int at=index*4;
        into[at]=t.scaleX;into[at+1]=t.scaleY;into[at+2]=TextWidth(t);into[at+3]=TextHeight(t);
    }
    static double[] Metrics(Panel p)
    {
        var m=new double[MetricCount];
        Measure(p.Name,m,NameMetric);Measure(p.Hp,m,HpMetric);for(int i=0;i<3;i++)Measure(p.Stat[i],m,StatMetric+i);
        return m;
    }
    static readonly ConditionalWeakTable<object,Panel> Panels=new();
    static readonly List<WeakReference<Panel>> Live=new();
    static long Converted,Reverts;

    // ---- stats session (game thread) ----
    sealed class Peer{public R.Stats Stats;public bool Known;public long SeenMs;public long Lines;public bool Announced;}
    static NetNode Session;static readonly Dictionary<int,Peer> Peers=new();
    static R.Stats Own,LastSent;static bool HaveOwn,HaveSent;static long SampleMs,SentMs,LinesOut;static int SendFaults;static bool SendOff;

    static readonly Dictionary<string,long> LogAt=new(StringComparer.Ordinal);static int LogLines;

    public static void Install()
    {
        lock(InstallGate)
        {
            if(Published)return;
            if(InitializationError!=null)throw new InvalidOperationException("Earlier party HUD initialization failed: "+InitializationError);
            Disabled=Environment.GetEnvironmentVariable(Switch)=="1";
            if(Disabled)return;                                     // kill switch: nothing patched, no wire; the base plate stays
            Harmony harmony=null;var targets=new List<MethodBase>();
            try
            {
                var mod=typeof(ModEntry);
                CoopPartyHudPins.RequireExactBinaries(mod.Assembly,typeof(dc.Entity).Assembly);
                CoopPartyHudPins.VerifySymbolic();
                var ui=mod.Assembly.GetType("DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI.MultiplayerUI",true);
                var slot=ui.GetNestedType("LifeSlot",BindingFlags.NonPublic)??throw new InvalidOperationException("Pinned type missing: MultiplayerUI.LifeSlot");
                var init=CoopPartyHudPins.Base(ui,"initkingLife",false,new[]{typeof(dc.ui.HUD),typeof(int),typeof(dc.ui.hud.LifeBar)},slot,CoopPartyHudPins.InitPlateIl,CoopPartyHudPins.InitPlateSha);
                var label=CoopPartyHudPins.Base(ui,"UpdateSlotLabel",true,new[]{slot,typeof(string)},typeof(void),CoopPartyHudPins.SlotLabelIl,CoopPartyHudPins.SlotLabelSha);
                var life=CoopPartyHudPins.Base(ui,"UpdateLifeBar",true,new[]{slot,typeof(int),typeof(int),typeof(int),typeof(int),typeof(int)},typeof(void),CoopPartyHudPins.LifeBarIl,CoopPartyHudPins.LifeBarSha);
                CoopPartyHudPins.Base(ui,"DrawPartyChip",true,new[]{slot,typeof(int),typeof(int),typeof(int)},typeof(void),CoopPartyHudPins.DrawChipIl,CoopPartyHudPins.DrawChipSha);
                CoopPartyHudPins.Base(ui,"KingLifeUpdate",false,new[]{typeof(dc.en.Hero)},typeof(void),CoopPartyHudPins.KingLifeIl,CoopPartyHudPins.KingLifeSha);
                SlotIndexOf=Getter<int>(slot,"SlotIndex",typeof(int),CoopPartyHudPins.SlotIndexIl,CoopPartyHudPins.SlotIndexSha);
                ContainerOf=Getter<object>(slot,"Container",typeof(dc.ui.FlowBox),CoopPartyHudPins.ContainerIl,CoopPartyHudPins.ContainerSha);
                PanelOf=Getter<object>(slot,"LabelBox",typeof(dc.h2d.Object),CoopPartyHudPins.LabelBoxIl,CoopPartyHudPins.LabelBoxSha);
                ChipOf=Getter<object>(slot,"ChipGraphics",typeof(dc.h2d.Graphics),CoopPartyHudPins.ChipIl,CoopPartyHudPins.ChipSha);
                LabelOf=Getter<object>(slot,"LabelText",typeof(dc.ui.Text),CoopPartyHudPins.LabelTextIl,CoopPartyHudPins.LabelTextSha);
                StatusOf=Getter<object>(slot,"StatusText",typeof(dc.ui.Text),CoopPartyHudPins.StatusTextIl,CoopPartyHudPins.StatusTextSha);
                ClientIndex=(ClientIndexFn)Delegate.CreateDelegate(typeof(ClientIndexFn),CoopPartyHudPins.Base(mod,"TryGetClientIndex",true,new[]{typeof(int),typeof(int),typeof(int).MakeByRefType()},typeof(bool),CoopPartyHudPins.ClientIndexIl,CoopPartyHudPins.ClientIndexSha));
                var lobby=mod.Assembly.GetType("DeadCellsMultiplayerMod.LobbySession",true);
                InActualRun=(Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>),CoopPartyHudPins.Base(lobby,"IsInActualRun",true,Type.EmptyTypes,typeof(bool),CoopPartyHudPins.InActualRunIl,CoopPartyHudPins.InActualRunSha));
                var frame=CoopPartyHudPins.Base(mod,"OnFrameUpdate",false,new[]{typeof(double)},typeof(void),CoopPartyHudPins.OnFrameUpdateIl,CoopPartyHudPins.OnFrameUpdateSha);
                TextWidth=HaxeGetter("get_textWidth",CoopPartyHudPins.TextWidthFIndex);
                TextHeight=HaxeGetter("get_textHeight",CoopPartyHudPins.TextHeightFIndex);
                BindMakeText();
                var handle=CoopPartyHudWire.Bind();
                harmony=new Harmony(Owner);
                void Patch(MethodBase target,string prefix,string postfix,Type owner,int priority)
                {
                    targets.Add(target);
                    harmony.Patch(target,prefix:prefix==null?null:new HarmonyMethod(owner.GetMethod(prefix,S)){priority=priority},
                        postfix:postfix==null?null:new HarmonyMethod(owner.GetMethod(postfix,S)){priority=priority});
                    var info=Harmony.GetPatchInfo(target);
                    if(info==null||info.Prefixes.Count(p=>p.owner==Owner)!=(prefix==null?0:1)||info.Postfixes.Count(p=>p.owner==Owner)!=(postfix==null?0:1)||
                       info.Transpilers.Any(p=>p.owner==Owner)||info.Finalizers.Any(p=>p.owner==Owner))
                        throw new InvalidOperationException("Party HUD patch on "+target.Name+" was not installed exactly once");
                }
                Patch(init,null,nameof(AfterInitPlate),typeof(CoopPartyHudPatch),Priority.Normal);
                Patch(label,nameof(BeforeSlotLabel),null,typeof(CoopPartyHudPatch),Priority.Normal);
                Patch(life,null,nameof(AfterLifeBar),typeof(CoopPartyHudPatch),Priority.Normal);
                Patch(handle,"BeforeHandleLine",null,typeof(CoopPartyHudWire),Priority.First);
                Patch(frame,null,nameof(AfterFrame),typeof(CoopPartyHudPatch),Priority.Normal);
                Published=true;
            }
            catch(Exception error)
            {
                Published=false;InitializationError=error.GetBaseException().Message;
                if(harmony!=null)foreach(var t in targets)try{harmony.Unpatch(t,HarmonyPatchType.All,Owner);}catch(Exception rollback){InitializationError+="; rollback: "+rollback.Message;}
                throw;
            }
        }
    }
    // Typed getter of a LifeSlot auto-property (private nested type of the base): object -> T without per-call reflection.
    static Func<object,T> Getter<T>(Type slot,string name,Type type,int length,string sha)
    {
        var p=slot.GetProperty(name,I|BindingFlags.DeclaredOnly);
        if(p==null||p.PropertyType!=type||p.GetMethod==null)throw new InvalidOperationException("Pinned property mismatch: LifeSlot."+name);
        CoopPartyHudPins.Pin(p.GetMethod,length,sha);
        var dm=new DynamicMethod("CoopPartyHud_get_"+name,typeof(T),new[]{typeof(object)},typeof(CoopPartyHudPatch).Module,true);
        var il=dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Castclass,slot);il.Emit(OpCodes.Callvirt,p.GetMethod);
        if(typeof(T)==typeof(object)&&type.IsValueType)il.Emit(OpCodes.Box,type);
        il.Emit(OpCodes.Ret);
        return (Func<object,T>)dm.CreateDelegate(typeof(Func<object,T>));
    }
    // The Haxe getter (not the raw property accessor of the same name and signature), by its pinned function index
    // (pattern of CoopNameplateFix NameplatePatch.HaxeGetter).
    public static Func<dc.h2d.Text,int> HaxeGetter(string name,int findex)
    {
        MethodInfo found=null;
        foreach(var m in typeof(dc.h2d.Text).GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly))
        {
            if(m.Name!=name||m.IsSpecialName||m.ReturnType!=typeof(int)||m.GetParameters().Length!=0||CoopPartyHudPins.FIndex(m)!=findex)continue;
            if(found!=null)throw new InvalidOperationException("Pinned method mismatch: h2d.Text."+name+" (ambiguous)");
            found=m;
        }
        if(found==null)throw new InvalidOperationException("Pinned method mismatch: h2d.Text."+name+" findex "+findex);
        return (Func<dc.h2d.Text,int>)Delegate.CreateDelegate(typeof(Func<dc.h2d.Text,int>),found);
    }

    // ================= faults, logs =================
    internal static void Fault(string what,Exception error)
    {
        bool before=Gate.Tripped;Gate.Fail();
        if(Gate.Faults==1)try{Serilog.Log.Warning("[CoopPartyHud] Party HUD {What} fault: {Error}; the base party plate is shown again",what,error.GetBaseException().Message);}catch{}
        if(Gate.Tripped&&!before)
        {
            TrippedBy=what;
            int n=RevertAll("fault: "+what);
            try{Serilog.Log.Warning("[CoopPartyHud] Party HUD switched off for this session after {Faults} fault(s); {Panels} panel(s) back to the base plate",Gate.Faults,n);}catch{}
        }
    }
    internal static void SendFault(Exception error)
    {
        SendFaults++;
        if(SendFaults==1)Info("sendfault",60000,"Stats line not sent: {Error}",error.GetBaseException().Message);
        if(SendFaults>=8&&!SendOff){SendOff=true;Info("sendoff",60000,"Stats sending stopped for this session after {Faults} send errors",SendFaults);}
    }
    static void Info(string key,long cooldownMs,string template,params object[] values)
    {
        long now=Environment.TickCount64;bool seen=LogAt.TryGetValue(key,out var at);
        if(!R.LogAllowed(seen,at,now,cooldownMs,LogLines))return;
        LogAt[key]=now;LogLines++;
        try{Serilog.Log.Information("[CoopPartyHud] "+template,values);}catch{}
    }

    // ================= panel: build, revert =================
    static void AfterInitPlate(object __result)
    {
        if(!Running||__result==null)return;
        try{Convert(__result);Gate.Ok();}
        catch(Exception error){Fault("panel build",error);}
    }
    static void Convert(object slot)
    {
        if(Panels.TryGetValue(slot,out _))return;
        var p=new Panel{SlotIndex=SlotIndexOf(slot),Slot=new WeakReference<object>(slot)};
        p.Base=PanelOf(slot) as dc.h2d.Object;p.Flow=ContainerOf(slot) as dc.h2d.Object;
        p.BaseChip=ChipOf(slot) as dc.h2d.Object;p.BaseLabel=LabelOf(slot) as dc.ui.Text;p.BaseStatus=StatusOf(slot) as dc.ui.Text;
        if(p.Base==null||p.Flow==null||p.BaseChip==null||p.BaseLabel==null||p.BaseStatus==null){Info("noparts:"+p.SlotIndex,60000,"Plate of slot {Slot} incomplete; base plate kept",p.SlotIndex);return;}
        // Register first: a fault from here on is reverted by RevertAll (layer removed, base visuals shown).
        Panels.Add(slot,p);Live.RemoveAll(w=>!w.TryGetTarget(out var x)||x.Reverted);Live.Add(new WeakReference<Panel>(p));
        p.Layer=new dc.h2d.Object(p.Base);
        p.G=new dc.h2d.Graphics(p.Layer);
        p.NameText=null;
        p.Name=MakeText("Guest",R.NameGold,R.NameScale,p.Layer);
        p.Hp=MakeText("0 / 0",R.HpTextColor(100),R.HpScale,p.Layer);
        for(int i=0;i<3;i++){p.Stat[i]=MakeText("0",R.StatFallback(i),R.StatScale,p.Layer);p.Stat[i].set_visible(false);}
        p.BaseChip.set_visible(false);p.BaseLabel.set_visible(false);p.BaseStatus.set_visible(false);
        Layout(p);Draw(p);
        Converted++;
        Info("convert:"+p.SlotIndex,60000,"Panel built for slot {Slot}: width {Width}, base plate hidden",p.SlotIndex,F(p.Width));
    }
    // Assets.makeText(text, ?color, ?bool, ?parent) is an HlFunc whose signature uses the runtime corelib's Nullable<T>; this
    // add-on compiles against reference assemblies and never names it (CoopLobby SPEC rule). Bound once by shape and called
    // through a compiled expression: Assets.Class.makeText.Invoke(text, (int?)color, (bool?)false, parent), exactly the
    // base mod's call (initkingLife IL_0143-IL_0173) with an explicit colour instead of COLORS["WO"].
    static Func<dc.String,int,dc.h2d.Object,dc.ui.Text> MakeTextFn;
    static void BindMakeText()
    {
        var classProp=typeof(dc.Assets).GetProperty("Class",BindingFlags.Public|BindingFlags.Static)??throw new InvalidOperationException("Proxy member missing: Assets.Class");
        var makeProp=classProp.PropertyType.GetProperty("makeText",I)??throw new InvalidOperationException("Proxy member missing: _Assets.makeText");
        var invoke=makeProp.PropertyType.GetMethod("Invoke");var ps=invoke?.GetParameters();
        if(invoke==null||invoke.ReturnType!=typeof(dc.ui.Text)||ps.Length!=4||ps[0].ParameterType!=typeof(dc.String)||ps[1].ParameterType!=typeof(int?)||ps[2].ParameterType!=typeof(bool?)||ps[3].ParameterType!=typeof(dc.h2d.Object))
            throw new InvalidOperationException("Proxy member changed: _Assets.makeText signature");
        var text=System.Linq.Expressions.Expression.Parameter(typeof(dc.String),"text");
        var color=System.Linq.Expressions.Expression.Parameter(typeof(int),"color");
        var parent=System.Linq.Expressions.Expression.Parameter(typeof(dc.h2d.Object),"parent");
        var fn=System.Linq.Expressions.Expression.Property(System.Linq.Expressions.Expression.Property(null,classProp),makeProp);
        var call=System.Linq.Expressions.Expression.Invoke(fn,text,System.Linq.Expressions.Expression.Convert(color,typeof(int?)),
            System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Constant(false),typeof(bool?)),parent);
        MakeTextFn=System.Linq.Expressions.Expression.Lambda<Func<dc.String,int,dc.h2d.Object,dc.ui.Text>>(call,text,color,parent).Compile();
    }
    static dc.ui.Text MakeText(string text,int color,double scale,dc.h2d.Object parent)
    {
        var t=MakeTextFn(text.AsHaxeString(),color,parent);
        if(t==null)throw new InvalidOperationException("Assets.makeText returned null");
        t.customScale=scale;t.onResize();
        t.set_textColor(color);
        return t;
    }
    static int RevertAll(string reason)
    {
        int n=0;
        foreach(var w in Live.ToArray())if(w.TryGetTarget(out var p)&&!p.Reverted){Revert(p,reason);n++;}
        Live.Clear();
        return n;
    }
    static void Revert(Panel p,string reason)
    {
        p.Reverted=true;p.RevertReason=reason;Reverts++;
        try{p.Layer?.remove();}catch{}
        try{p.BaseChip?.set_visible(true);}catch{}
        try{p.BaseLabel?.set_visible(true);}catch{}
        try{p.BaseStatus?.set_visible(true);}catch{}
        try{(p.Flow as dc.h2d.Flow)?.set_needReflow(true);}catch{}
    }
    static bool TryPanel(object slot,out Panel p){p=null;return slot!=null&&Panels.TryGetValue(slot,out p)&&!p.Reverted&&p.Layer!=null;}

    // ================= panel: name, stats, colours =================
    static void BeforeSlotLabel(object __0,string __1)
    {
        if(!Running)return;
        try{if(TryPanel(__0,out var p)){Label(p,__1);}Gate.Ok();}
        catch(Exception error){Fault("name update",error);}
    }
    static void Label(Panel p,string displayName)
    {
        long now=Environment.TickCount64;bool relayout=p.NeedLayout;
        if(p.NameText==null||!string.Equals(displayName,p.RawName,StringComparison.Ordinal))      // the base passes a name every frame: no per-frame allocation
        {
            p.RawName=displayName;var name=R.Name(displayName);
            // As the base does (UpdateSlotLabel IL_0039-IL_0045): set_text, then onResize, then measure (in Layout).
            if(!string.Equals(name,p.NameText,StringComparison.Ordinal)){p.NameText=name;p.Name.set_text(R.UiText(name).AsHaxeString());p.Name.onResize();relayout=true;}
        }
        var net=ModEntry._net;int localId=net!=null&&net.IsAlive?net.id:0;
        if(localId!=p.LocalId){p.LocalId=localId;p.RemoteId=RemoteFor(localId,p.SlotIndex);p.ColorMs=0;}
        if(R.Due(p.ColorMs,now,1000))
        {
            p.ColorMs=now==0?1:now;
            int c=PartnerColor(p.RemoteId);
            if(c!=p.NameColor){p.NameColor=c;p.Name.set_textColor(c);}
            RefreshStatColors(now);
            for(int i=0;i<3;i++)if(StatColors[i]!=p.StatColor[i]){p.StatColor[i]=StatColors[i];p.Stat[i].set_textColor(StatColors[i]);}
            // Text scale or font changed from outside (ui.Text.onResize after a window / UI-scale change): lay out again.
            if(!relayout&&R.MetricsChanged(p.Laid,Metrics(p))){relayout=true;p.MetricRelayouts++;}
        }
        Peer peer=null;
        bool known=p.RemoteId>0&&Peers.TryGetValue(p.RemoteId,out peer)&&peer.Known;
        bool show=R.SlotShowsStats(p.RemoteId,known,known?peer.SeenMs:0,now);
        if(show!=p.StatsShown){p.StatsShown=show;relayout=true;}   // visibility is applied by Layout (StatsVisible)
        if(show)
        {
            var s=peer.Stats;
            if(!s.Equals(p.ShownStats))
            {
                for(int i=0;i<3;i++)if(s[i]!=p.ShownStats[i]){p.Stat[i].set_text(s[i].ToString(System.Globalization.CultureInfo.InvariantCulture).AsHaxeString());p.Stat[i].onResize();}
                p.ShownStats=s;relayout=true;
            }
        }
        if(relayout){Layout(p);p.NeedLayout=false;}
    }
    // Remote net id of a base slot: the inverse of ModEntry.TryGetClientIndex, confirmed with the pinned base method itself.
    static int RemoteFor(int localId,int slot)
    {
        int id=R.RemoteIdForSlot(localId,slot);
        return id>0&&ClientIndex(localId,id,out int back)&&back==slot?id:0;
    }
    static int PartnerColor(int remoteId)
    {
        if(remoteId<=0)return R.NameGold;
        long now=Environment.TickCount64;
        if(LobbyColorOf==null&&R.Due(LobbyLookMs,now,5000)){LobbyLookMs=now==0?1:now;LookUpLobby();}
        if(LobbyColorOf==null)return R.NameGold;
        try{int index=LobbyColorOf(remoteId);bool valid=index>=0&&index<LobbyCount;return R.NameColor(valid,index,LobbyCount,valid?LobbyRgb(index):0);}
        catch(Exception error)
        {
            if(!LobbyWarned){LobbyWarned=true;Info("lobbyerr",60000,"Lobby colour unavailable ({Error}); gold name",error.GetBaseException().Message);}
            return R.NameGold;
        }
    }
    // CoopLobby (in CoopPack.dll, or the standalone CoopLobby.dll) publishes LobbyColorState.ColorOf(netId) (palette index of
    // what the run really tints, LobbyPalette.cs:42-47) and LobbyPalette.Rgb(index) / Count; global types, read only.
    static void LookUpLobby()
    {
        Type state=null,palette=null;int n=0;string source=null;
        foreach(var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            string an;try{an=a.GetName().Name;}catch{continue;}
            if(an!="CoopPack"&&an!="CoopLobby")continue;
            Type s=null,pl=null;try{s=a.GetType("LobbyColorState",false);pl=a.GetType("LobbyPalette",false);}catch{}
            if(s!=null&&pl!=null){state=s;palette=pl;source=an;n++;}
        }
        if(n!=1)return;
        var colorOf=state.GetMethod("ColorOf",BindingFlags.Public|BindingFlags.Static,null,new[]{typeof(int)},null);
        var rgb=palette.GetMethod("Rgb",BindingFlags.Public|BindingFlags.Static,null,new[]{typeof(int)},null);
        var count=palette.GetProperty("Count",BindingFlags.Public|BindingFlags.Static);
        if(colorOf?.ReturnType!=typeof(int)||rgb?.ReturnType!=typeof(int)||count?.PropertyType!=typeof(int))return;
        LobbyCount=(int)count.GetValue(null);
        LobbyColorOf=(Func<int,int>)Delegate.CreateDelegate(typeof(Func<int,int>),colorOf);
        LobbyRgb=(Func<int,int>)Delegate.CreateDelegate(typeof(Func<int,int>),rgb);
        LobbySource=source;
        Info("lobby",600000,"Partner name colour from {Source} LobbyColorState ({Count} colours)",source,LobbyCount);
    }
    // The game's own stat colours: Options.getTierColor("Brutality"|"Tactic"|"Survival") (f@5791), refreshed every 2 s
    // (the player may change the colour-blind override in the options).
    static void RefreshStatColors(long now)
    {
        if(!R.Due(StatColorMs,now,2000)&&StatColors[0]>=0)return;
        StatColorMs=now==0?1:now;
        dc.Options options=null;
        try{options=dc.Main.Class.ME?.options;}catch{}
        bool fromGame=false;
        for(int i=0;i<3;i++)
        {
            int c;bool known=false;int v=0;
            if(options!=null)
            {
                try{v=options.getTierColor(R.TierIds[i].AsHaxeString());known=true;}
                catch(Exception error){if(!StatColorWarned){StatColorWarned=true;Info("statcolor",600000,"Stat colour from the game unavailable ({Error}); default stat colours",error.GetBaseException().Message);}}
            }
            c=R.StatColor(i,known,v);fromGame|=known;
            StatColors[i]=c;
        }
        StatColorFromGame=fromGame;
    }

    // ================= panel: layout and drawing =================
    static double Width(dc.ui.Text t){int w=TextWidth(t);return w<=0?0:w*t.scaleX;}
    static double Height(dc.ui.Text t){int h=TextHeight(t);return h<=0?0:h*t.scaleY;}
    static void Place(dc.h2d.Object o,double x,double y){if(o.x!=x)o.x=x;if(o.y!=y)o.y=y;o.posChanged=true;}
    static void Layout(Panel p)
    {
        p.Layouts++;
        bool show=p.StatsShown;
        for(int i=0;i<3;i++)p.StatWidth[i]=show?Width(p.Stat[i]):0;
        double stats=R.StatsWidth(p.StatWidth[0],p.StatWidth[1],p.StatWidth[2],show);
        var fit=R.FitName(scale=>{if(p.Name.customScale!=scale){p.Name.customScale=scale;p.Name.onResize();}return Width(p.Name);},stats);
        if(p.Name.customScale!=fit.Scale){p.Name.customScale=fit.Scale;p.Name.onResize();}
        bool widthChanged=p.Width!=fit.Width;
        p.NameScale=fit.Scale;p.Width=fit.Width;p.NameWidth=fit.NameWidth;p.NameRoom=fit.Room;p.Widened=fit.Widened;p.Overflow=fit.Overflow;p.FitTries=fit.Tries;p.StatsDropped=fit.StatsDropped;
        if(fit.Overflow)Info("overflow:"+p.SlotIndex,60000,"Name of slot {Slot} wider than the widest panel ({Width} > {Room}) at scale {Scale}",p.SlotIndex,F(fit.NameWidth),F(fit.Room),F(fit.Scale));
        else if(fit.StatsDropped)Info("drop:"+p.SlotIndex+":"+p.Width,60000,"Stats of slot {Slot} hidden so the full name fits: panel {Width} at scale {Scale}",p.SlotIndex,F(p.Width),F(fit.Scale));
        else if(fit.Widened)Info("widen:"+p.SlotIndex+":"+p.Width,60000,"Panel of slot {Slot} widened to {Width} for a long name (scale {Scale})",p.SlotIndex,F(p.Width),F(fit.Scale));
        Place(p.Name,R.Pad,R.CenterY(R.RowTop,R.RowBottom,Height(p.Name),9));
        bool visible=R.StatsVisible(show,fit.StatsDropped);
        for(int i=0;i<3;i++)if(visible)Place(p.Stat[i],R.StatX(i,p.Width,p.StatWidth[0],p.StatWidth[1],p.StatWidth[2]),R.CenterY(R.RowTop,R.RowBottom,Height(p.Stat[i]),10));
        if(visible!=p.StatsVisible){p.StatsVisible=visible;for(int i=0;i<3;i++)p.Stat[i].set_visible(visible);}
        PlaceHp(p);
        if(widthChanged){Draw(p);try{(p.Flow as dc.h2d.Flow)?.set_needReflow(true);}catch{}}
        var laid=Metrics(p);Array.Copy(laid,p.Laid,MetricCount);
    }
    static void PlaceHp(Panel p)
    {
        p.HpWidth=Width(p.Hp);
        Place(p.Hp,R.HpX(p.Width,p.HpWidth),R.CenterY(R.BarY,R.BarY+R.BarH,Height(p.Hp),R.BarY-2));
        Measure(p.Hp,p.Laid,HpMetric);
    }
    static void AfterLifeBar(object __0,int __1,int __2)
    {
        if(!Running)return;
        try{if(TryPanel(__0,out var p))Life(p,__1,__2);Gate.Ok();}
        catch(Exception error){Fault("HP update",error);}
    }
    static void Life(Panel p,int life,int maxLife)
    {
        if(life==p.Life&&maxLife==p.Max)return;
        p.Life=life;p.Max=maxLife;
        var text=R.HpText(life,maxLife);int color=R.HpTextColor(R.Percent(life,maxLife));
        if(!string.Equals(text,p.HpText,StringComparison.Ordinal)){p.HpText=text;p.Hp.set_text(text.AsHaxeString());p.Hp.onResize();PlaceHp(p);}   // as the base (UpdateLifeBar IL_00DC-IL_0110)
        if(color!=p.HpColor){p.HpColor=color;p.Hp.set_textColor(color);}
        Draw(p);
    }
    static void Rect(dc.h2d.Graphics g,int color,double alpha,double x,double y,double w,double h)
    {
        if(w<=0||h<=0)return;
        g.beginFill(Ref<int>.From(ref color),Ref<double>.From(ref alpha));g.drawRect(x,y,w,h);g.endFill();
    }
    // Pixel style of the base plate (DrawPartyChip IL:87159): soft shadow, chamfered bronze frame with a lit top edge, navy
    // window, dark inset HP bar with highlight/shade bands and 6 segment notches; one panel of width W, two rows.
    static void Draw(Panel p)
    {
        var g=p.G;if(g==null)return;
        p.Draws++;
        double W=p.Width,H=R.PanelHeight;
        g.clear();
        Rect(g,0x000000,0.38,R.Shadow,R.Shadow,W,H);
        Rect(g,0x3A1B12,1,4,0,W-8,H);Rect(g,0x3A1B12,1,0,4,W,H-8);Rect(g,0x3A1B12,1,2,2,W-4,H-4);
        Rect(g,0xA44E32,1,6,2,W-12,H-4);Rect(g,0xA44E32,1,2,6,W-4,H-12);Rect(g,0xA44E32,1,4,4,W-8,H-8);
        Rect(g,0xC66B42,1,6,2,W-12,2);
        Rect(g,0x2A3A5E,1,7,7,W-14,H-14);
        Rect(g,0x1A2340,1,8,8,W-16,H-16);
        Rect(g,0x2A3A5E,1,10,R.RowBottom+1,W-20,1);
        double bx=R.BarX,by=R.BarY,bw=R.BarWidth(W),bh=R.BarH;
        Rect(g,0x07090F,1,bx-R.BarShell,by-R.BarShell,bw+2*R.BarShell,bh+2*R.BarShell);
        Rect(g,0x11202A,1,bx,by,bw,bh);
        int pct=R.Percent(p.Life,p.Max);double fw=R.FillWidth(bw,p.Life,p.Max);
        if(fw>0)
        {
            Rect(g,R.FillColor(pct),1,bx,by,fw,bh);
            Rect(g,R.FillHighlight(pct),1,bx,by,fw,2);
            Rect(g,R.FillShade(pct),1,bx,by+bh-2,fw,2);
        }
        for(int i=1;i<R.BarSegments;i++)Rect(g,0x07090F,1,bx+bw*i/R.BarSegments-1,by,2,bh);
    }

    // ================= stats session (frame tick) =================
    static void AfterFrame()
    {
        if(!Published)return;
        if(Disabled||Gate.Tripped){CoopPartyHudWire.Clear();return;}
        try
        {
            var net=ModEntry._net;
            if(net==null||!net.IsAlive){if(Session!=null)EndSession();CoopPartyHudWire.Clear();Gate.Ok();return;}
            if(!ReferenceEquals(net,Session)){EndSession();Session=net;}
            long now=Environment.TickCount64;
            while(CoopPartyHudWire.Dequeue(out var m))
            {
                if(m.Sender==net.id)continue;
                if(!Peers.TryGetValue(m.Sender,out var peer))Peers[m.Sender]=peer=new Peer();
                bool was=peer.Known&&R.Capable(peer.SeenMs,now);
                peer.Stats=m.Stats;peer.Known=true;peer.SeenMs=now==0?1:now;peer.Lines++;
                if(!was)Info("cap:"+m.Sender,60000,"Partner {Peer} sends its stats (CPHUD1): {Stats}",m.Sender,m.Stats.ToString());
            }
            foreach(var kv in Peers)
                if(kv.Value.Known&&!R.Capable(kv.Value.SeenMs,now)&&!kv.Value.Announced){kv.Value.Announced=true;Info("nocap:"+kv.Key,60000,"Partner {Peer} stats hidden: no CPHUD1 line for {Ttl} s",kv.Key,R.CapabilityTtlMs/1000);}
                else if(R.Capable(kv.Value.SeenMs,now))kv.Value.Announced=false;
            if(R.Due(SampleMs,now,R.SampleMs))
            {
                SampleMs=now==0?1:now;
                bool run=false;try{run=InActualRun();}catch{}
                var hero=run?dc.pr.Game.Class.ME?.hero:null;
                if(hero!=null)
                {
                    Own=R.Clamp(hero.brutalityTier,hero.tacticTier,hero.survivalTier);HaveOwn=true;
                    bool changed=!HaveSent||!Own.Equals(LastSent);
                    if(R.SendNow(SendOff,net.HasRemote,net.id,changed,SentMs,now))
                    {
                        int to=CoopPartyHudWire.Send(net,R.Encode(net.id,Own));
                        if(to>0){SentMs=now==0?1:now;LastSent=Own;HaveSent=true;LinesOut++;if(changed)Info("own:"+Own,10000,"Own stats sent: {Stats}",Own.ToString());}
                    }
                }
            }
            Gate.Ok();
        }
        catch(Exception error){Fault("stats tick",error);}
    }
    static void EndSession()
    {
        Session=null;Peers.Clear();HaveSent=false;SentMs=0;SampleMs=0;
        CoopPartyHudWire.Clear();
    }

    static string F(double v)=>v.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture);

    // ================= probe read-out (informational; the probe reads the scene itself) =================
    public static Dictionary<string,object> Snapshot()
    {
        long now=Environment.TickCount64;
        var panels=new List<object>();
        foreach(var w in Live)if(w.TryGetTarget(out var p))
            panels.Add(new Dictionary<string,object>{["slot"]=p.SlotIndex,["remoteId"]=p.RemoteId,["name"]=p.NameText,["nameColor"]=p.NameColor,["nameScale"]=p.NameScale,["nameWidth"]=p.NameWidth,
                ["nameRoom"]=p.NameRoom,["width"]=p.Width,["widened"]=p.Widened,["overflow"]=p.Overflow,["fitTries"]=p.FitTries,["hp"]=p.HpText,["hpWidth"]=p.HpWidth,["life"]=p.Life,["maxLife"]=p.Max,
                ["statsShown"]=p.StatsShown,["statsVisible"]=p.StatsVisible,["statsDropped"]=p.StatsDropped,["stats"]=p.StatsShown?p.ShownStats.ToString():null,["statColors"]=p.StatColor.ToArray(),["statWidths"]=p.StatWidth.ToArray(),
                ["reverted"]=p.Reverted,["revertReason"]=p.RevertReason,["layouts"]=p.Layouts,["metricRelayouts"]=p.MetricRelayouts,["draws"]=p.Draws});
        var peers=Peers.Select(kv=>(object)new Dictionary<string,object>{["id"]=kv.Key,["stats"]=kv.Value.Stats.ToString(),["known"]=kv.Value.Known,["capable"]=R.Capable(kv.Value.SeenMs,now),
            ["seenAgoMs"]=kv.Value.SeenMs==0?-1:now-kv.Value.SeenMs,["lines"]=kv.Value.Lines}).ToList();
        return new Dictionary<string,object>{["ready"]=Published,["running"]=Running,["disabled"]=Disabled,["tripped"]=Gate.Tripped,["trippedBy"]=TrippedBy,["faults"]=Gate.Faults,
            ["initializationError"]=InitializationError,["converted"]=Converted,["reverts"]=Reverts,["panels"]=panels,["peers"]=peers,["own"]=HaveOwn?Own.ToString():null,
            ["lastSent"]=HaveSent?LastSent.ToString():null,["linesOut"]=LinesOut,["sendOff"]=SendOff,["sendFaults"]=SendFaults,["statColors"]=StatColors.ToArray(),["statColorsFromGame"]=StatColorFromGame,
            ["lobbyColorSource"]=LobbySource,["linesReceived"]=CoopPartyHudWire.ReceivedCount,["linesDropped"]=CoopPartyHudWire.DroppedCount,["linesSent"]=CoopPartyHudWire.SentCount,
            ["wireSendFaults"]=CoopPartyHudWire.SendFaultCount,["symbolicPin"]=CoopPartyHudPins.SymbolicPin};
    }
}
