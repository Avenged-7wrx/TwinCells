using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using AttackData=dc.tool.atk.AttackData;
using ComboCount=dc.ui.hud.ComboCount;
using Entity=dc.Entity;
using Hero=dc.en.Hero;
using HeroAttackSource=dc.tool.atk.atkSources.HeroAttackSource;
using Mob=dc.en.Mob;
using R=ComboFixRules;

// CoopComboFix (Alpha2 unit U8; PLAN-ALPHA2.md issue 11, owner decision Q2; rules and game facts in ComboFixRules.cs).
//
// Part A (both roles; in practice the host, the only role that replays):
//   - Harmony prefix + finalizer on the base mod's private static TryReplayIncomingSpecialHitReaction(Mob,double)
//     (release IL 129657; the replay runs synchronously inside it: RunWithSuppressedMobHitSend -> closure b__0,
//     IL 181279) and TryApplyHostBossFinishingHit(Mob,int) (IL 125564) keep a [ThreadStatic] depth;
//   - a DCCM hook on dc.en.Hook_Mob.onDirectHitFromHero (native Mob.onDirectHitFromHero f@1076; the GardenerBoss
//     override f@9425 keeps its own code and only loses the super call during a replay) returns without calling the
//     original while a replay depth is above 0 or the base mod's KingSkin weapon context is active
//     (KingWeaponSupport.IsInKingContext: a remote avatar's weapon must not feed the local hero's combo either).
// Part B (switch COOP_COMBO_PARTB_DISABLE=1 turns it off on its own):
//   - a DCCM hook on dc.ui.hud.Hook_ComboCount.setValue reads k from the game's own call setValue(n, 1 + k*n);
//   - client: a Harmony postfix on the private static ReadAttackIntentDamage(AttackData) (IL 133616; its only caller is
//     Hook_Mob_onDamage IL 134124, before orig, so the stack count is the one the client's own damage used) multiplies
//     the hint of an attack whose source is the local hero and that has no tag 27 by 1 + k*countAffect(132), while
//     the host answered "on" (ComboFixWire);
//   - host: a DCCM hook on dc.tool.atk.atkSources.Hook_HeroAttackSource.computePerksBonusMul (native f@18793) removes
//     the local hero's combo term k*countAffect(132) from the first perks computation of a replay frame for the
//     replay's own target by the local hero.
// Every body: try/catch behind one fault gate (8 consecutive / 64 total -> pass-through for the session); the original
// game calls are never wrapped, so a game exception is not swallowed or changed. Logs: rate-limited ("[CoopComboFix] ").
// Pins: the base mod release DLL (378BE6B0, 0.5.1.0), hlboot.dat A37869A3, the IL of every base method this part patches
// or relies on (IlPins, release bytes), and GameProxy symbolically (ComboProxyFingerprint.cs).
public static class ComboFixPatch
{
    public const string Owner="CoopComboFix.20261112";
    public const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string NativePin="A37869A3C91443B50322B2CDF29595C9644AB8FC1280342F1F17C6F616A15534";
    const BindingFlags S=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.DeclaredOnly;
    const BindingFlags I=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly;
    const string SyncName="DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization";
    const string KingName="DeadCellsMultiplayerMod.Ghost.KingWeaponSupport";
    const string ReplayClosure="<>c__DisplayClass200_0";

    // IL pins of the release DLL 378BE6B0 (length, SHA-256 of the IL bytes; GetILAsByteArray returns the original IL even
    // after another module patched the method). Patched: replay, boss finish, intent, HandleLine, RejectIncompatiblePeer,
    // OnFrameUpdate. Relied on: the replay closure (synchronous createFromHeroAndHit with the local hero), Hook_Mob_onDamage
    // (reads the intent before orig and sends it as the hint), EstimateClientAttackDamageHint (passes the intent through),
    // RunWithSuppressedMobHitSend (runs the replay synchronously), the King context getter and SendLineSafe.
    public static readonly (string Key,int Length,string Sha)[] IlPins=
    {
        ("MobsSynchronization.TryReplayIncomingSpecialHitReaction",90,"5D66A2C48B1C58A6005575F6D874B5687A68D69BD8DE8DF6C622F3CD63B2C58E"),
        ("MobsSynchronization.<>c__DisplayClass200_0.<TryReplayIncomingSpecialHitReaction>b__0",296,"EDA572013C12ECE8CFBA8B75893A998CB24227230CEFA6C63F36A874C7CCAA8D"),
        ("MobsSynchronization.TryApplyHostBossFinishingHit",309,"03045A089E09323E9BCF4062EE7488479579C399E98D0C38233F14053D1D9341"),
        ("MobsSynchronization.ReadAttackIntentDamage",82,"BEBF14FEA72BBEFDBD576DCCAA2F2BD00423D85F4A58B5951160721F49CCE491"),
        ("MobsSynchronization.Hook_Mob_onDamage",814,"BDFCF5394DDF668616F5172E93322264C6F1C8C92FF51C56C7A60D7378ADFFD8"),
        ("MobsSynchronization.EstimateClientAttackDamageHint",70,"B2DDDB4AD7F5BBA040C9DC5EF20F02B33D5490D33CBC355C04786058618369AB"),
        ("MobsSynchronization.RunWithSuppressedMobHitSend",38,"A912FDDB4A3E9C2D08D739CF4F46566FC7B50E609C4AED6F1C96B78A2BFB9458"),
        ("KingWeaponSupport.get_IsInKingContext",9,"49368DBCFA6E292F9145EB3AF1B5AC6F09CB5BEC984CC6F04EFC325F0195CBCA"),
        ("NetNode.HandleLine",11632,"DDBA7D561C9AFE056538D8473CE182DC1F9D3D6B0081A181F3FCAF087E9EEA3F"),
        ("NetNode.RejectIncompatiblePeer",96,"D53169BEC368195B4B05BE406A87287C9AF885324BDA6732DD5BAF4E49196611"),
        ("NetNode.SendLineSafe",178,"8AE4B5C5660FAF4ED37DBA938CE4ABB68302852FBC3B0638BFF98ED04C954C2A"),
        ("ModEntry.OnFrameUpdate",270,"2246501C5B61327925F02715B4034722C6E9648434F7111AB114EBFED22717B3"),
    };

    static readonly object Gate=new();
    static bool _bound,_installed;
    static string _error;
    public static string InitializationError=>_error;
    public static bool Installed=>_installed;
    public static bool PartBOn{get;private set;}
    public static string ProxyPath{get;private set;}="";
    static volatile bool _tripped;
    static int _consecutive;
    static long _faults;
    public static bool Tripped=>_tripped;

    // ---- bound members (Bind: reflection, pins and IL checks only; no game state, no static constructor run) ----
    internal static MethodInfo ReplayMethod,BossMethod,IntentMethod,KingGetter,HandleLineMethod,RejectMethod,SendLineMethod,FrameMethod;
    internal static Func<bool> InKingContext;
    static readonly Dictionary<string,MethodBase> PinnedMethods=new(StringComparer.Ordinal);

    // ---- replay frames (game thread) ----
    sealed class ReplayFrame{public IntPtr Mob;public bool Done;}
    [ThreadStatic] static int _replayDepth;
    [ThreadStatic] static int _bossDepth;
    [ThreadStatic] static List<ReplayFrame> _frames;

    // ---- k (game thread writes, game thread reads) ----
    static double _perStack;static bool _perStackKnown;
    public static double PerStack=>_perStack;
    public static bool PerStackKnown=>_perStackKnown;

    // ---- counters ----
    static long _replaySkips,_bossSkips,_kingSkips,_directPassed,_kSeen,_hintsScaled,_hintsNoStacks,_hintsUnsupported,_hintsKUnknown,
        _hostRemoved,_hostNoStacks,_hostKUnknown;
    static string _lastStats="";static long _lastStatsAt=long.MinValue;
    static bool _warnedHostK,_warnedClientK;

    // DCCM hooks. The generated hook/orig delegate types derive from System.Private.CoreLib's MulticastDelegate, which the
    // pack's reference set (PSHOME\ref) cannot name at compile time, so the handlers are DynamicMethods built from the
    // events' own reflected signatures (pattern of tests\SyncAudit.Gameplay.cs:30-36, natively proven). Each handler calls
    // the original itself, in its own IL, outside any managed try block of ours.
    sealed class NativeHook{public EventInfo Event;public Delegate Handler;public bool Added;}
    static NativeHook _hDirect,_hPerks,_hCombo;
    static Harmony _harmony;

    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);
    static string IlSha(byte[] il)=>il==null?"":Convert.ToHexString(SHA256.HashData(il));
    static MethodInfo Method(Type type,string name,bool isStatic,Type ret,params Type[] args)
    {
        var m=type.GetMethod(name,isStatic?S:I,null,args,null);
        if(m==null||m.IsStatic!=isStatic||(ret!=null&&m.ReturnType!=ret))throw new InvalidOperationException("Pinned method mismatch: "+type.Name+"."+name);
        return m;
    }
    static void PinIl(string key,MethodBase m)
    {
        foreach(var p in IlPins)
            if(p.Key==key)
            {
                var il=m.GetMethodBody()?.GetILAsByteArray();
                if(il==null||il.Length!=p.Length||IlSha(il)!=p.Sha)throw new InvalidOperationException("Pinned IL changed: "+key);
                PinnedMethods[key]=m;
                return;
            }
        throw new InvalidOperationException("No IL pin for "+key);
    }

    // Reflection binding, file pins and IL pins. Touches no game state and installs nothing; Test-Glue.ps1 calls it offline
    // against the real release DLL, GameProxy and hlboot. Throws on any mismatch.
    public static void Bind()
    {
        lock(Gate)
        {
            if(_bound)return;
            var mod=typeof(ModEntry).Assembly;
            if(mod.GetName().Version!=new Version(0,5,1,0)||Hash(mod.Location)!=BasePin)
                throw new InvalidOperationException("CoopComboFix requires DeadCellsMultiplayerMod 0.5.1 (pinned release 378BE6B0)");
            var boot=Path.Combine(Path.GetDirectoryName(typeof(Hero).Assembly.Location)??"","hlboot.dat");
            if(Hash(boot)!=NativePin)throw new InvalidOperationException("CoopComboFix requires the pinned native game build (hlboot A37869A3)");
            ProxyPath=ComboProxyCompatibility.ValidateLoaded(typeof(Hero).Assembly);
            var sync=mod.GetType(SyncName,true);
            if(sync.BaseType!=typeof(object))throw new InvalidOperationException("MobsSynchronization is not a plain managed class");
            ReplayMethod=Method(sync,"TryReplayIncomingSpecialHitReaction",true,typeof(void),typeof(Mob),typeof(double));
            BossMethod=Method(sync,"TryApplyHostBossFinishingHit",true,typeof(void),typeof(Mob),typeof(int));
            IntentMethod=Method(sync,"ReadAttackIntentDamage",true,typeof(double),typeof(AttackData));
            var origOnDamage=typeof(dc.en.Hook_Mob).GetNestedType("orig_onDamage",BindingFlags.Public)??throw new InvalidOperationException("Hook_Mob.orig_onDamage missing");
            var onDamage=Method(sync,"Hook_Mob_onDamage",false,typeof(void),origOnDamage,typeof(Mob),typeof(AttackData));
            var estimate=Method(sync,"EstimateClientAttackDamageHint",true,typeof(double),typeof(double),typeof(int),typeof(int),typeof(bool));
            var suppressed=Method(sync,"RunWithSuppressedMobHitSend",true,typeof(void),typeof(Action));
            var closureType=sync.GetNestedType(ReplayClosure,BindingFlags.NonPublic)??throw new InvalidOperationException("Pinned replay closure missing");
            var closure=Method(closureType,"<TryReplayIncomingSpecialHitReaction>b__0",false,typeof(void));
            PinIl("MobsSynchronization.TryReplayIncomingSpecialHitReaction",ReplayMethod);
            PinIl("MobsSynchronization.<>c__DisplayClass200_0.<TryReplayIncomingSpecialHitReaction>b__0",closure);
            PinIl("MobsSynchronization.TryApplyHostBossFinishingHit",BossMethod);
            PinIl("MobsSynchronization.ReadAttackIntentDamage",IntentMethod);
            PinIl("MobsSynchronization.Hook_Mob_onDamage",onDamage);
            PinIl("MobsSynchronization.EstimateClientAttackDamageHint",estimate);
            PinIl("MobsSynchronization.RunWithSuppressedMobHitSend",suppressed);
            var king=mod.GetType(KingName,true);
            KingGetter=Method(king,"get_IsInKingContext",true,typeof(bool));
            PinIl("KingWeaponSupport.get_IsInKingContext",KingGetter);
            var depth=king.GetField("_contextDepth",S);
            if(depth==null||depth.FieldType!=typeof(int)||depth.GetCustomAttribute<ThreadStaticAttribute>()==null)
                throw new InvalidOperationException("Pinned King context depth changed (int, [ThreadStatic])");
            InKingContext=(Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>),KingGetter);
            var net=typeof(NetNode);
            HandleLineMethod=Method(net,"HandleLine",false,typeof(bool),typeof(string),typeof(int?),typeof(string).MakeByRefType());
            RejectMethod=Method(net,"RejectIncompatiblePeer",false,typeof(bool),typeof(string),typeof(string));
            SendLineMethod=Method(net,"SendLineSafe",false,typeof(System.Threading.Tasks.Task),typeof(string));
            FrameMethod=Method(typeof(ModEntry),"OnFrameUpdate",false,typeof(void),typeof(double));
            PinIl("NetNode.HandleLine",HandleLineMethod);
            PinIl("NetNode.RejectIncompatiblePeer",RejectMethod);
            PinIl("NetNode.SendLineSafe",SendLineMethod);
            PinIl("ModEntry.OnFrameUpdate",FrameMethod);
            // Members read at run time (exact types).
            var me=typeof(ModEntry).GetField("me",S);var netField=typeof(ModEntry).GetField("_net",S);
            if(me==null||me.FieldType!=typeof(Hero)||!me.IsPublic||netField==null||netField.FieldType!=typeof(NetNode)||!netField.IsPublic)
                throw new InvalidOperationException("Pinned ModEntry fields changed");
            foreach(var (name,type) in new[]{("IsHost",typeof(bool)),("IsAlive",typeof(bool)),("id",typeof(int))})
            {var p=net.GetProperty(name,I);if(p==null||p.PropertyType!=type||p.GetMethod==null||!p.GetMethod.IsPublic)throw new InvalidOperationException("Pinned NetNode."+name+" changed");}
            if(PinnedMethods.Count!=IlPins.Length)throw new InvalidOperationException("Not every IL pin was checked");
            // The three native hook events and the exact handler shapes the DynamicMethods are built for.
            _hDirect=BindHook(typeof(dc.en.Hook_Mob),"onDirectHitFromHero",typeof(void),typeof(Mob),typeof(AttackData));
            _hPerks=BindHook(typeof(dc.tool.atk.atkSources.Hook_HeroAttackSource),"computePerksBonusMul",typeof(double),typeof(HeroAttackSource),typeof(AttackData),typeof(Entity));
            _hCombo=BindHook(typeof(dc.ui.hud.Hook_ComboCount),"setValue",typeof(int),typeof(ComboCount),typeof(int),typeof(double));
            _bound=true;
        }
    }

    public static void Install()
    {
        lock(Gate)
        {
            if(_installed)return;
            if(_error!=null)throw new InvalidOperationException("Earlier CoopComboFix initialization failed: "+_error);
            var patched=new List<MethodBase>();
            try
            {
                Bind();
                PartBOn=!R.SwitchSet(Environment.GetEnvironmentVariable(R.PartBSwitch));
                _harmony=new Harmony(Owner);
                void Patch(MethodBase target,string prefix,string postfix,string finalizer,int priority)
                {
                    patched.Add(target);
                    HarmonyMethod H(string n)=>n==null?null:new HarmonyMethod(typeof(ComboFixPatch).GetMethod(n,BindingFlags.Static|BindingFlags.NonPublic)??throw new MissingMethodException("ComboFixPatch",n)){priority=priority};
                    _harmony.Patch(target,prefix:H(prefix),postfix:H(postfix),finalizer:H(finalizer));
                    ExactlyOnce(target,prefix!=null,postfix!=null,finalizer!=null);
                }
                Patch(ReplayMethod,nameof(BeforeReplay),null,nameof(AfterReplay),Priority.First);
                Patch(BossMethod,nameof(BeforeBoss),null,nameof(AfterBoss),Priority.First);
                Patch(FrameMethod,null,nameof(AfterFrame),null,Priority.Normal);
                if(PartBOn)
                {
                    Patch(IntentMethod,null,nameof(AfterIntent),null,Priority.Last);   // after any other postfix: scale the final intent
                    ComboFixWire.Install(_harmony,patched);
                }
                BuildDirectHook(_hDirect);AddHook(_hDirect);
                if(PartBOn)
                {
                    BuildComboHook(_hCombo);AddHook(_hCombo);
                    BuildPerksHook(_hPerks);AddHook(_hPerks);
                }
                _installed=true;
            }
            catch(Exception e)
            {
                _error=e.GetBaseException().Message;
                foreach(var h in new[]{_hDirect,_hCombo,_hPerks})
                    try{if(h!=null&&h.Added){h.Event.RemoveEventHandler(null,h.Handler);h.Added=false;}}catch(Exception r){_error+="; rollback: "+r.Message;}
                if(_harmony!=null)foreach(var t in patched.Distinct())try{_harmony.Unpatch(t,HarmonyPatchType.All,Owner);}catch(Exception r){_error+="; rollback: "+r.Message;}
                throw;
            }
        }
    }

    // ---- DCCM hook construction ----
    // An event of a generated Hook_* class: static, handler = (orig, self, args...) -> ret, orig = (self, args...) -> ret.
    static NativeHook BindHook(Type hookClass,string name,Type ret,params Type[] args)
    {
        var ev=hookClass.GetEvent(name,BindingFlags.Public|BindingFlags.Static)??throw new InvalidOperationException("Native hook event missing: "+hookClass.Name+"."+name);
        if(ev.AddMethod==null||!ev.AddMethod.IsStatic||ev.RemoveMethod==null)throw new InvalidOperationException("Native hook event is not static: "+name);
        var invoke=ev.EventHandlerType.GetMethod("Invoke")??throw new InvalidOperationException("Native hook handler without Invoke: "+name);
        var p=invoke.GetParameters().Select(x=>x.ParameterType).ToArray();
        var origInvoke=p.Length>0?p[0].GetMethod("Invoke"):null;
        if(invoke.ReturnType!=ret||p.Length!=args.Length+1||!typeof(Delegate).IsAssignableFrom(p[0])||origInvoke==null||origInvoke.ReturnType!=ret||
           !p.Skip(1).SequenceEqual(args)||!origInvoke.GetParameters().Select(x=>x.ParameterType).SequenceEqual(args))
            throw new InvalidOperationException("Native hook signature changed: "+hookClass.Name+"."+name);
        return new NativeHook{Event=ev};
    }
    static void AddHook(NativeHook h){h.Event.AddEventHandler(null,h.Handler);h.Added=true;}
    static System.Reflection.Emit.ILGenerator NewHandler(NativeHook h,string name,out System.Reflection.Emit.DynamicMethod dm,out Type[] p,out MethodInfo origInvoke)
    {
        var invoke=h.Event.EventHandlerType.GetMethod("Invoke");
        p=invoke.GetParameters().Select(x=>x.ParameterType).ToArray();origInvoke=p[0].GetMethod("Invoke");
        dm=new System.Reflection.Emit.DynamicMethod("CoopComboFix_"+name,invoke.ReturnType,p,typeof(ComboFixPatch).Module,true);
        return dm.GetILGenerator();
    }
    static void Finish(NativeHook h,System.Reflection.Emit.DynamicMethod dm)
    {
        h.Handler=dm.CreateDelegate(h.Event.EventHandlerType);
        System.Runtime.CompilerServices.RuntimeHelpers.PrepareDelegate(h.Handler);
    }
    static MethodInfo Own(string name)=>typeof(ComboFixPatch).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic)??throw new MissingMethodException("ComboFixPatch",name);
    // onDirectHitFromHero: if(SkipDirectHit(self)) return; orig(self, a);
    static void BuildDirectHook(NativeHook h)
    {
        var il=NewHandler(h,"onDirectHitFromHero",out var dm,out _,out var origInvoke);
        var call=il.DefineLabel();
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
        il.Emit(System.Reflection.Emit.OpCodes.Call,Own(nameof(SkipDirectHit)));
        il.Emit(System.Reflection.Emit.OpCodes.Brfalse_S,call);
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        il.MarkLabel(call);
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);il.Emit(System.Reflection.Emit.OpCodes.Ldarg_2);
        il.Emit(System.Reflection.Emit.OpCodes.Callvirt,origInvoke);
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        Finish(h,dm);
    }
    // computePerksBonusMul: return AdjustPerks(orig(self, atk, target), self, target);
    static void BuildPerksHook(NativeHook h)
    {
        var il=NewHandler(h,"computePerksBonusMul",out var dm,out _,out var origInvoke);
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);il.Emit(System.Reflection.Emit.OpCodes.Ldarg_2);il.Emit(System.Reflection.Emit.OpCodes.Ldarg_3);
        il.Emit(System.Reflection.Emit.OpCodes.Callvirt,origInvoke);
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);il.Emit(System.Reflection.Emit.OpCodes.Ldarg_3);
        il.Emit(System.Reflection.Emit.OpCodes.Call,Own(nameof(AdjustPerks)));
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        Finish(h,dm);
    }
    // ComboCount.setValue: ObserveComboSetValue(v, mult); return orig(self, v, mult);
    static void BuildComboHook(NativeHook h)
    {
        var il=NewHandler(h,"setValue",out var dm,out _,out var origInvoke);
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_2);il.Emit(System.Reflection.Emit.OpCodes.Ldarg_3);
        il.Emit(System.Reflection.Emit.OpCodes.Call,Own(nameof(ObserveComboSetValue)));
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);il.Emit(System.Reflection.Emit.OpCodes.Ldarg_2);il.Emit(System.Reflection.Emit.OpCodes.Ldarg_3);
        il.Emit(System.Reflection.Emit.OpCodes.Callvirt,origInvoke);
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        Finish(h,dm);
    }
    static void ExactlyOnce(MethodBase target,bool prefix,bool postfix,bool finalizer)
    {
        var info=Harmony.GetPatchInfo(target)??throw new InvalidOperationException("Patch missing on "+target.Name);
        int Ours(IEnumerable<Patch> ps)=>ps.Count(p=>p.owner==Owner);
        if(Ours(info.Prefixes)!=(prefix?1:0)||Ours(info.Postfixes)!=(postfix?1:0)||Ours(info.Finalizers)!=(finalizer?1:0)||Ours(info.Transpilers)!=0)
            throw new InvalidOperationException("Patch on "+target.Name+" was not installed exactly once");
    }
    public static string Describe()=>"partA=on (replays, boss finishing hits, King context) partB="+(PartBOn?"on":"disabled by "+R.PartBSwitch+"=1")+" proxy="+ProxyPath;

    // ---- fault gate ----
    internal static void Fault(Exception e)
    {
        long n=Interlocked.Increment(ref _faults);
        int c=Interlocked.Increment(ref _consecutive);
        bool trip=!_tripped&&R.ShouldTrip(c,n);
        if(trip)_tripped=true;
        if(n==1||trip)Warn("fault"+(trip?"; switched off for this session, the game's own combo rules apply again":"")+": "+e.GetBaseException().GetType().Name+": "+e.GetBaseException().Message);
    }
    internal static void Ok(){if(_consecutive!=0)_consecutive=0;}
    internal static void Info(string text){try{Serilog.Log.Information("[CoopComboFix] {Text:l}",text);}catch{}}
    internal static void Warn(string text){try{Serilog.Log.Warning("[CoopComboFix] {Text:l}",text);}catch{}}

    internal static Hero LocalHero()
    {
        var game=dc.pr.Game.Class.ME;
        if(game==null||game.HashlinkPointer==IntPtr.Zero)return null;
        var hero=game.hero;
        if(hero==null||hero.HashlinkPointer==IntPtr.Zero||hero.destroyed)return null;
        return hero;
    }

    // ================= Harmony bodies (never throw) =================
    static void BeforeReplay(Mob mob,out bool __state)
    {
        __state=false;
        try
        {
            (_frames??=new List<ReplayFrame>(4)).Add(new ReplayFrame{Mob=mob==null?IntPtr.Zero:mob.HashlinkPointer});
            _replayDepth++;__state=true;
        }
        catch(Exception e){Fault(e);}
    }
    static Exception AfterReplay(Exception __exception,bool __state)
    {
        if(__state)
        {
            _replayDepth--;
            var frames=_frames;if(frames!=null&&frames.Count>0)frames.RemoveAt(frames.Count-1);
        }
        return __exception;
    }
    static void BeforeBoss(out bool __state){_bossDepth++;__state=true;}
    static Exception AfterBoss(Exception __exception,bool __state){if(__state)_bossDepth--;return __exception;}

    // Client, Part B: the intent of the local hero's own attack, scaled by his own combo. Runs inside Hook_Mob_onDamage
    // before orig (release IL 134124), so countAffect(132) is the stack count the client's own damage used.
    static void AfterIntent(AttackData attack,ref double __result)
    {
        if(_tripped||!PartBOn)return;
        try
        {
            var net=ModEntry._net;
            if(net==null||net.IsHost||!net.IsAlive)return;
            long now=Environment.TickCount64;
            bool supports=ComboFixWire.ClientSupported(net,now);
            bool own=false,noBonus=false;int stacks=0;
            if(supports&&attack!=null)
            {
                var hero=LocalHero();var source=attack.source;
                own=hero!=null&&source!=null&&source.HashlinkPointer==hero.HashlinkPointer;
                if(own){noBonus=attack.hasTag(R.NoPerkBonusTag);stacks=hero.countAffect(R.ComboAffect);}
            }
            var v=R.ClientHint(true,true,supports,__result,own,noBonus,stacks,_perStackKnown,_perStack,out var factor);
            switch(v)
            {
                case R.HintVerdict.Scaled:
                {
                    long n=++_hintsScaled;double before=__result;__result*=factor;
                    if(R.LogOccurrence(n))Info("Own combo sent with a hit (#"+n+"): stacks="+stacks+" factor="+R.Num(factor)+" intent "+R.Num(before)+" -> "+R.Num(__result));
                    break;
                }
                case R.HintVerdict.NoStacks:_hintsNoStacks++;break;
                case R.HintVerdict.Unsupported:_hintsUnsupported++;break;
                case R.HintVerdict.PerStackUnknown:
                    _hintsKUnknown++;
                    if(!_warnedClientK){_warnedClientK=true;Warn("own combo stacks present but the per-stack value was never shown by the game; hint left unscaled");}
                    break;
            }
            Ok();
        }
        catch(Exception e){Fault(e);}
    }

    // Frame tick (game thread, after ModEntry.OnFrameUpdate): the wire and the stats line.
    static void AfterFrame()
    {
        if(!_installed)return;
        try
        {
            long now=Environment.TickCount64;
            if(PartBOn&&!_tripped)ComboFixWire.Tick(now);
            Stats(now);
        }
        catch(Exception e){Fault(e);}
    }
    static void Stats(long now)
    {
        string line="replaySkipped="+_replaySkips+" bossSkipped="+_bossSkips+" kingSkipped="+_kingSkips+" ownDirectHits="+_directPassed+
            " perStackSeen="+_kSeen+(_perStackKnown?"(k="+R.Num(_perStack)+")":"")+" hintsScaled="+_hintsScaled+" hintsNoStacks="+_hintsNoStacks+
            " hintsUnsupported="+_hintsUnsupported+" hostTermRemoved="+_hostRemoved+" hostNoStacks="+_hostNoStacks+
            " unknownK="+(_hostKUnknown+_hintsKUnknown)+" faults="+Interlocked.Read(ref _faults)+(_tripped?" OFF":"");
        bool changed=!string.Equals(line,_lastStats,StringComparison.Ordinal);
        if(!R.StatsDue(now,_lastStatsAt,changed))return;
        if(_lastStatsAt==long.MinValue&&_replaySkips+_bossSkips+_kingSkips+_directPassed+_kSeen+_hintsScaled+_hostRemoved==0){_lastStats=line;return;}
        _lastStats=line;_lastStatsAt=now;
        Info("stats "+line);
    }

    // ================= DCCM hook bodies (called from the DynamicMethod handlers; never throw) =================
    // Part A: true = the handler returns without calling the original Mob.onDirectHitFromHero.
    static bool SkipDirectHit(Mob self)
    {
        if(_tripped)return false;
        try
        {
            var verdict=R.DirectHitVerdict(_replayDepth,_bossDepth,InKingContext());
            if(verdict!=R.Skip.None)Count(verdict,self);
            else _directPassed++;
            Ok();
            return verdict!=R.Skip.None;
        }
        catch(Exception e){Fault(e);return false;}
    }
    static void Count(R.Skip verdict,Mob self)
    {
        long n=verdict==R.Skip.Replay?++_replaySkips:verdict==R.Skip.BossFinish?++_bossSkips:++_kingSkips;
        if(!R.LogOccurrence(n))return;
        string what=verdict==R.Skip.Replay?"replayed partner hit":verdict==R.Skip.BossFinish?"partner's boss finishing hit":"partner avatar weapon hit (King context)";
        Info("Kept a "+what+" out of the local combo and on-hit effects (#"+n+", "+(self?.GetType().Name??"?")+")");
    }

    // Part B, host: the replayed partner hit is computed without the local hero's own combo term.
    static double AdjustPerks(double result,HeroAttackSource self,Entity target)
    {
        if(_replayDepth<=0||_tripped||!PartBOn)return result;
        try
        {
            var frames=_frames;
            if(frames==null||frames.Count==0)return result;
            var frame=frames[frames.Count-1];
            bool same=target!=null&&frame.Mob!=IntPtr.Zero&&target.HashlinkPointer==frame.Mob;
            Hero hero=null;bool local=false;int stacks=0;
            if(!frame.Done&&same)
            {
                hero=LocalHero();var parent=self?.parent;
                local=hero!=null&&parent!=null&&parent.HashlinkPointer==hero.HashlinkPointer;
                if(local)stacks=hero.countAffect(R.ComboAffect);
            }
            var v=R.HostPerks(true,true,frame.Done,same,local,result,stacks,_perStackKnown,_perStack,out var adjusted);
            if(R.ConsumesFrame(v))frame.Done=true;
            switch(v)
            {
                case R.HostVerdict.Removed:
                {
                    long n=++_hostRemoved;
                    if(R.LogOccurrence(n))Info("Own combo kept out of a replayed partner hit (#"+n+"): stacks="+stacks+" perks "+R.Num(result)+" -> "+R.Num(adjusted));
                    break;
                }
                case R.HostVerdict.NoStacks:_hostNoStacks++;break;
                case R.HostVerdict.PerStackUnknown:
                    _hostKUnknown++;
                    if(!_warnedHostK){_warnedHostK=true;Warn("own combo stacks present during a replayed partner hit but the per-stack value was never shown by the game; left as is");}
                    break;
            }
            Ok();
            return adjusted;
        }
        catch(Exception e){Fault(e);return result;}
    }

    // Part B: k from the game's own HUD call ComboCount.setValue(n, 1 + k*n) (Mob.hx:2687-2688).
    static void ObserveComboSetValue(int v,double mult)
    {
        if(_tripped)return;
        try
        {
            if(R.TryPerStack(v,mult,out var k))
            {
                bool first=!_perStackKnown||Math.Abs(k-_perStack)>1e-12;
                _perStack=k;_perStackKnown=true;long n=++_kSeen;
                if(first&&n<=64)Info("Combo per-stack value from the game: k="+R.Num(k)+" (stacks="+v+", multiplier="+R.Num(mult)+")");
            }
            Ok();
        }
        catch(Exception e){Fault(e);}
    }

    // Read by the unit probe (part-state) and the logs; no game object.
    public static string Status()=>"installed="+(_installed?1:0)+";partB="+(PartBOn?1:0)+";tripped="+(_tripped?1:0)+";proxy="+ProxyPath+
        ";k="+(_perStackKnown?R.Num(_perStack):"")+";replaySkipped="+_replaySkips+";bossSkipped="+_bossSkips+";kingSkipped="+_kingSkips+
        ";ownDirectHits="+_directPassed+";hintsScaled="+_hintsScaled+";hintsNoStacks="+_hintsNoStacks+";hintsUnsupported="+_hintsUnsupported+
        ";hostTermRemoved="+_hostRemoved+";hostNoStacks="+_hostNoStacks+";faults="+Interlocked.Read(ref _faults)+";"+ComboFixWire.Status(Environment.TickCount64);
}
