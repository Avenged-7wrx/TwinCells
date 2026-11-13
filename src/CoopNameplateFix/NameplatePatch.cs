using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using File=System.IO.File;
using Path=System.IO.Path;

// Game side of CoopNameplateFix (PLAN-ALPHA2.md issue 8, unit U4): binding, pins, the install transaction and the two
// postfixes. The numbers and the geometry are NameplateRules (pure, managed checks).
//
// Patched base-mod members (release names; the clone has none of them), each pinned by its IL body SHA-256 below:
//   GhostHero.GetNicknameScale()  private static double (IL 57555, 121 bytes): postfix, __result *= Scale.
//     Its only callers are SetLabel (IL 57390) and UpdateLabels (IL 57448). The base caches the UNscaled value
//     (_cachedNicknameScale), so the factor never compounds. ResolveNicknameScale is not patched (inlining candidate).
//   GhostHero.UpdateLabels()  public instance void (IL 57441, 311 bytes): postfix, Priority.Last. Per label (the
//     partner's avatar and the local hero, see NameplateRules) whose entity, sprite and parent are set and whose parent
//     is the entity's sprite (the label SetLabel made): x, y from
//     NameplateRules.Place, alpha = Alpha (only if different), posChanged = true. hook_boot_update (IL 34166) has no
//     exception handler, so the whole postfix body is inside try/catch: a throw here would skip
//     ProcessCameraSpectateInput and TickRemoteKingSubLevelTransitionGuard.
// Read only: GhostHero._labels (Dictionary<dc.Entity, GhostHero.LabelState>, InitOnly), LabelState.Label (IL pinned).
// GameProxy: the members the compiled part references, plus the two Haxe getters bound here by function index
// (h2d.Text has TWO "int get_textWidth()" methods: the raw accessor of the never-written physical field and the Haxe
// getter f@3389; same for get_textHeight / f@3388), are pinned symbolically by NameplateProxyCompatibility (generator
// pins + MVID/name + a symbolic ABI/IL fingerprint), never by the raw GameProxy hash (PLAN-ALPHA2.md section 4 rule 2).
// textColor is never read or written here: the CoopLobby colour (or the base COLORS["ST"]) stays.
// Switches: COOP_NAMEPLATE_DISABLE=1 (nothing patched; read by the entry type), COOP_NAMEPLATE_SCALE (0.2-1, default
// 0.5), COOP_NAMEPLATE_ALPHA (0.2-1, default 0.65). Fault gate: NameplateFaultGate (8 consecutive / 64 total).
public static class NameplatePatch
{
    public const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string NativePin="A37869A3C91443B50322B2CDF29595C9644AB8FC1280342F1F17C6F616A15534";
    public const string Owner="CoopNameplateFix.Nameplate.20261112";
    const BindingFlags S=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;
    const BindingFlags I=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;
    // IL body SHA-256 of the release members (System.Reflection.Metadata dump of 378BE6B0; lengths equal the release IL
    // dump headers). The DLL hash pins them already; these make a mis-resolved member fail closed.
    const int ScaleIl=121;const string ScaleSha="9C4C0FF82A233C3844BEA1B9A773D32DEF9F0EF620B894D988CEC944685D7719";
    const int UpdateIl=311;const string UpdateSha="9F9439B315454A35586DF3F14B8AD9DA7B319699385D53124125954E6B399676";
    const int LabelIl=7;const string LabelSha="645D76670EE21DDEFA1A846722DD40A383286BA57C5BE805CF358878F7BD6C9D";
    public const int TextWidthFIndex=3389,TextHeightFIndex=3388;
    const long LogIntervalMs=5000;

    static readonly object InstallSync=new();
    static bool _attempted;
    static volatile bool _active;
    static double _scale=NameplateRules.DefaultScale,_alpha=NameplateRules.DefaultAlpha;
    static MethodInfo _scaleTarget,_updateTarget;
    static Func<object,object> _labelsOf;
    static Action<object> _visit;
    static Func<dc.h2d.Text,int> _textWidth,_textHeight;
    static readonly NameplateFaultGate Gate=new();
    static readonly NameplateLogLimiter AppliedLog=new(LogIntervalMs);
    static readonly HashSet<IntPtr> _seenLabels=new();
    static readonly Stopwatch Clock=Stopwatch.StartNew();
    static long _scaleCalls,_labelUpdates,_labelsPlaced,_labelsKept;

    // Read by the unit probe (reflection) and the log; never by game code.
    public static bool Active=>_active;
    public static double ScaleFactor=>_scale;
    public static double AlphaValue=>_alpha;
    public static long ScaleCalls=>Interlocked.Read(ref _scaleCalls);
    public static long LabelUpdates=>Interlocked.Read(ref _labelUpdates);
    public static long LabelsPlaced=>Interlocked.Read(ref _labelsPlaced);
    public static long LabelsKept=>Interlocked.Read(ref _labelsKept);
    public static long Faults=>Gate.Faults;
    public static string InitializationError{get;private set;}

    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);
    static void Pin(MethodInfo m,int length,string sha)
    {
        var il=m.GetMethodBody()?.GetILAsByteArray();
        if(il==null||il.Length!=length||Convert.ToHexString(SHA256.HashData(il))!=sha)throw new InvalidOperationException("Pinned body changed: "+m.DeclaringType?.Name+"."+m.Name);
    }
    static MethodInfo Own(string name)=>typeof(NameplatePatch).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic)??throw new MissingMethodException("NameplatePatch",name);

    // Returns the ready line. Throws (and leaves nothing patched) when anything does not match.
    public static string Install()
    {
        lock(InstallSync)
        {
            if(_attempted)throw new InvalidOperationException("CoopNameplateFix install already attempted"+(InitializationError!=null?": "+InitializationError:""));
            _attempted=true;
            Harmony harmony=null;var patched=new List<MethodInfo>();
            try
            {
                var mod=typeof(ModEntry).Assembly;
                if(mod.GetName().Version!=new Version(0,5,1,0)||Hash(mod.Location)!=BasePin||
                    Hash(Path.Combine(Path.GetDirectoryName(typeof(dc.Entity).Assembly.Location)??"","hlboot.dat"))!=NativePin)
                    throw new InvalidOperationException("CoopNameplateFix requires exact community0.5.1/native35.13.5");
                NameplateProxyCompatibility.ValidateLoaded(typeof(dc.Entity).Assembly);
                string symbolic=NameplateProxyCompatibility.FingerprintPin;
                Bind();
                var notes=new List<string>();
                _scale=NameplateRules.Factor(Environment.GetEnvironmentVariable(NameplateRules.ScaleSwitch),NameplateRules.DefaultScale,out var scaleNote);
                _alpha=NameplateRules.Factor(Environment.GetEnvironmentVariable(NameplateRules.AlphaSwitch),NameplateRules.DefaultAlpha,out var alphaNote);
                if(Environment.GetEnvironmentVariable(NameplateRules.ScaleSwitch)!=null)notes.Add(NameplateRules.ScaleSwitch+"="+(scaleNote??_scale.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                if(Environment.GetEnvironmentVariable(NameplateRules.AlphaSwitch)!=null)notes.Add(NameplateRules.AlphaSwitch+"="+(alphaNote??_alpha.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                harmony=new Harmony(Owner);
                patched.Add(_scaleTarget);
                harmony.Patch(_scaleTarget,postfix:new HarmonyMethod(Own(nameof(AfterNicknameScale))));
                patched.Add(_updateTarget);
                harmony.Patch(_updateTarget,postfix:new HarmonyMethod(Own(nameof(AfterUpdateLabels))){priority=Priority.Last});
                foreach(var target in patched)
                {
                    var info=Harmony.GetPatchInfo(target);
                    if(info==null||info.Postfixes.Count(p=>p.owner==Owner)!=1||info.Prefixes.Any(p=>p.owner==Owner)||info.Transpilers.Any(p=>p.owner==Owner)||info.Finalizers.Any(p=>p.owner==Owner))
                        throw new InvalidOperationException("Nameplate patch not installed exactly once on "+target.Name);
                }
                _active=true;
                return "scale x"+F(_scale)+", alpha "+F(_alpha)+", nickname labels centred over their player with the bottom edge kept; postfixes on GhostHero.GetNicknameScale ("+
                    Owners(_scaleTarget)+") and GhostHero.UpdateLabels ("+Owners(_updateTarget)+"); GameProxy symbolic "+symbolic+(notes.Count>0?"; "+string.Join("; ",notes):"");
            }
            catch(Exception error)
            {
                _active=false;InitializationError=error.GetBaseException().Message;
                if(harmony!=null)foreach(var target in patched)
                    try{harmony.Unpatch(target,HarmonyPatchType.All,Owner);}catch(Exception rollback){InitializationError+="; rollback: "+rollback.Message;}
                throw;
            }
        }
    }
    static string F(double v)=>v.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture);
    static string Owners(MethodBase m)
    {
        var info=Harmony.GetPatchInfo(m);
        if(info==null)return "none";
        return string.Join(",",info.Prefixes.Select(p=>"prefix:"+p.owner).Concat(info.Postfixes.Select(p=>"postfix:"+p.owner)).Concat(info.Transpilers.Select(p=>"transpiler:"+p.owner)).Concat(info.Finalizers.Select(p=>"finalizer:"+p.owner)));
    }

    static void Bind()
    {
        var ghost=typeof(GhostHero);
        _scaleTarget=ghost.GetMethod("GetNicknameScale",S,null,Type.EmptyTypes,null);
        if(_scaleTarget==null||!_scaleTarget.IsStatic||_scaleTarget.ReturnType!=typeof(double))throw new InvalidOperationException("Pinned method mismatch: GhostHero.GetNicknameScale");
        _updateTarget=ghost.GetMethod("UpdateLabels",I,null,Type.EmptyTypes,null);
        if(_updateTarget==null||_updateTarget.IsStatic||_updateTarget.ReturnType!=typeof(void))throw new InvalidOperationException("Pinned method mismatch: GhostHero.UpdateLabels");
        Pin(_scaleTarget,ScaleIl,ScaleSha);
        Pin(_updateTarget,UpdateIl,UpdateSha);
        var labels=ghost.GetField("_labels",I);
        var t=labels?.FieldType;
        if(labels==null||!labels.IsInitOnly||t==null||!t.IsGenericType||t.GetGenericTypeDefinition()!=typeof(Dictionary<,>)||t.GetGenericArguments()[0]!=typeof(dc.Entity))
            throw new InvalidOperationException("Pinned field mismatch: GhostHero._labels");
        var state=t.GetGenericArguments()[1];
        if(state.DeclaringType!=ghost||state.Name!="LabelState"||state.IsValueType)throw new InvalidOperationException("Pinned type mismatch: GhostHero.LabelState");
        var label=state.GetProperty("Label",I);
        var getter=label?.GetMethod;
        if(label==null||label.PropertyType!=typeof(dc.ui.Text)||getter==null||getter.IsStatic)throw new InvalidOperationException("Pinned property mismatch: GhostHero.LabelState.Label");
        Pin(getter,LabelIl,LabelSha);
        _labelsOf=FieldGetter(labels);
        var visitor=typeof(Visitor<>).MakeGenericType(state);
        visitor.GetField("LabelOf",BindingFlags.Static|BindingFlags.Public).SetValue(null,Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(state,typeof(dc.ui.Text)),getter));
        _visit=(Action<object>)Delegate.CreateDelegate(typeof(Action<object>),visitor.GetMethod("Run",BindingFlags.Static|BindingFlags.Public));
        _textWidth=HaxeGetter("get_textWidth",TextWidthFIndex);
        _textHeight=HaxeGetter("get_textHeight",TextHeightFIndex);
    }
    // Instance field read as object -> object, without per-frame reflection.
    static Func<object,object> FieldGetter(FieldInfo f)
    {
        var dm=new DynamicMethod("CoopNameplateFix_get_"+f.Name,typeof(object),new[]{typeof(object)},typeof(NameplatePatch).Module,true);
        var il=dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Castclass,f.DeclaringType);il.Emit(OpCodes.Ldfld,f);il.Emit(OpCodes.Ret);
        return (Func<object,object>)dm.CreateDelegate(typeof(Func<object,object>));
    }
    // The Haxe getter (not the property accessor of the same name and signature), by its pinned function index.
    public static Func<dc.h2d.Text,int> HaxeGetter(string name,int findex)
    {
        MethodInfo found=null;
        foreach(var m in typeof(dc.h2d.Text).GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly))
        {
            if(m.Name!=name||m.IsSpecialName||m.ReturnType!=typeof(int)||m.GetParameters().Length!=0||FIndex(m)!=findex)continue;
            if(found!=null)throw new InvalidOperationException("Pinned method mismatch: h2d.Text."+name+" (ambiguous)");
            found=m;
        }
        if(found==null)throw new InvalidOperationException("Pinned method mismatch: h2d.Text."+name+" findex "+findex);
        return (Func<dc.h2d.Text,int>)Delegate.CreateDelegate(typeof(Func<dc.h2d.Text,int>),found);
    }
    public static int FIndex(MethodInfo m)
    {
        foreach(var a in m.CustomAttributes)
            if(a.AttributeType.Name=="HashlinkFIndexAttribute"&&a.ConstructorArguments.Count==1)return Convert.ToInt32(a.ConstructorArguments[0].Value);
        return -1;
    }

    // Typed walk over Dictionary<dc.Entity, LabelState> (no per-frame allocation; LabelState is private to the base).
    static class Visitor<TState> where TState:class
    {
        public static Func<TState,dc.ui.Text> LabelOf=null;     // set once by Bind (reflection)
        public static void Run(object labels)
        {
            if(!(labels is Dictionary<dc.Entity,TState> map)||map.Count==0)return;
            foreach(var kv in map)
            {
                var state=kv.Value;
                Apply(kv.Key,state==null?null:LabelOf(state));
            }
        }
    }

    // ---- the postfixes ----
    static void AfterNicknameScale(ref double __result)
    {
        if(!_active)return;
        try
        {
            __result=NameplateRules.Nickname(__result,_scale);
            Interlocked.Increment(ref _scaleCalls);
        }
        catch(Exception error){Fault("scale",error);}
    }
    static void AfterUpdateLabels(object __instance)
    {
        if(!_active||__instance==null)return;
        try
        {
            Interlocked.Increment(ref _labelUpdates);
            var labels=_labelsOf(__instance);
            if(labels!=null)_visit(labels);
            Gate.Ok();
        }
        catch(Exception error){Fault("label update",error);}
    }
    // Mirrors the base mod's own skip conditions (UpdateLabels IL_0052-IL_0068) and adds: the label still hangs under
    // the king's sprite. Anything else keeps the base mod's values for this frame.
    static void Apply(dc.Entity king,dc.ui.Text label)
    {
        if(king==null||label==null)return;
        var sprite=king.spr;if(sprite==null)return;
        var parent=label.parent;if(parent==null)return;
        if(parent.HashlinkPointer!=sprite.HashlinkPointer){Interlocked.Increment(ref _labelsKept);return;}
        int width=_textWidth(label),bottom=_textHeight(label);      // initGlyphs when needed, then calcWidth / calcHeight
        NameplateRules.Box(label.calcXMin,width,label.realMaxWidth,out var xMin,out var boxWidth);
        double scaleX=label.scaleX,scaleY=label.scaleY;int dir=king.dir;
        var layout=NameplateRules.Place(xMin,boxWidth,bottom,scaleX,scaleY,dir,_scale);
        if(!layout.Ok){Interlocked.Increment(ref _labelsKept);return;}
        label.x=layout.X;label.y=layout.Y;
        if(label.alpha!=_alpha)label.alpha=_alpha;
        label.posChanged=true;
        Interlocked.Increment(ref _labelsPlaced);
        var key=label.HashlinkPointer;
        if(_seenLabels.Contains(key))return;
        if(_seenLabels.Count>=64)_seenLabels.Clear();
        _seenLabels.Add(key);
        if(AppliedLog.Allow(Clock.ElapsedMilliseconds,out long held))
            try{Serilog.Log.Information("[CoopNameplateFix] Label applied: box {XMin}+{Width}x{Bottom} scale {ScaleX}/{ScaleY} dir {Dir} -> x {X} y {Y} alpha {Alpha}{Held}",
                xMin,boxWidth,bottom,F(scaleX),F(scaleY),dir,F(layout.X),F(layout.Y),F(_alpha),held>0?" ("+held+" more labels since the last line)":"");}catch{}
    }
    static void Fault(string what,Exception error)
    {
        Gate.Fail(out bool first,out bool trip);
        if(Gate.Tripped)_active=false;
        if(first)try{Serilog.Log.Warning("[CoopNameplateFix] Nameplate {What} fault: {Error}",what,error.GetBaseException().Message);}catch{}
        if(trip)try{Serilog.Log.Warning("[CoopNameplateFix] Nameplate fix switched off for this session after {Faults} faults; labels keep the base mod's look",Gate.Faults);}catch{}
    }
}
