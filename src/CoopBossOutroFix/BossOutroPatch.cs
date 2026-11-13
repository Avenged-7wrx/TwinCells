using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using dc.en;
using dc.en.mob.boss;
using dc.tool.atk;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using Mono.Cecil;
using File=System.IO.File;
using Path=System.IO.Path;

// Part CoopBossOutroFix (Alpha2 unit U1; PLAN-ALPHA2.md issue 10 "client freeze after the Concierge died"). Client only;
// on the host every hook passes straight through.
//
// What it does (the plan's chosen fix, steps 1-5):
//  1. A DCCM hook on dc.en.mob.boss.Hook_Behemoth.tryToPreventDeath (native f@11626). When the client's own call returns
//     true and Game.curCine is a NEW live object of exact type dc.GameCinematic, the pair (boss, scene) is recorded.
//     (The generic Hook_Boss.tryToPreventDeath, f@4451, exists but cannot see the scene: Behemoth calls it first, op 1,
//     before it builds the scene, op 139, and Boss.tryToPreventDeath itself builds none; it is therefore not hooked.)
//  2. A Harmony prefix on the base mod's private static MobsSynchronization.ShouldProcessClientVisualState(Mob)
//     (release IL 124083; its only callers are ApplyInterpolatedState IL 122950 and ApplyClientAnimationStateBeforeUpdate
//     IL 124552, both pinned) answers false for the recorded boss while its scene is the live current scene - the base
//     mod's own rule for a dying boss. The host's cached animation is then not replayed over "lethalHit", its onEnd sends
//     the scene's signal, and the native queue ends the scene as on the host (about 4 s): kill() -> onDie (swallowed by
//     the base mod: no authority) -> cine.destroyed=true -> curCine clears.
//  2b. A Harmony prefix on the base mod's private static MobsSynchronization.TryQueueClientMobAttack(Mob, ...) (release IL
//     127898; its only callers are ApplyIncomingHostMobAttacks IL 127575 and RetryPendingClientBossAttacks IL 127752, both
//     pinned; both mark the attack applied right after the call, so a skipped attack is dropped, never retried) skips the
//     replay of a host attack on the recorded boss while its scene is the live current scene. The replayed skill plays its
//     own animation over "lethalHit" and drops its onEnd exactly like the animation replay of step 2. The Alpha1 CSF gate
//     held every incoming mob packet during a client cinematic, so this path was never reached; with U2's narrowed gate it
//     is (native run a2-U1-boss-outro-6: the host's behemothAtkA replayed at 2.55 s, the client scene held 18.8 s).
//  3. A Harmony finalizer on the base mod's MobsSynchronization.RunWithAuthoritativeClientMobDie(Mob, Action) (IL 139129,
//     the one funnel of every client authoritative death, RunClientAuthoritativeBossDeath included) notes the host's
//     authoritative death of the recorded boss; the next frame ends that scene with the base mod's own
//     ModEntry.SuppressRemoteBossDeathCineState(cine) + RestoreViewportToLocalHero() (a destroyed boss can never fire
//     "lethalHit").
//  4. Last resort, no host confirmation needed: a plain dc.GameCinematic that is still current after 20 s is ended the same
//     way when it is the recorded outro, or (another boss) when the client is on a BossLevelIds level, a boss of that level
//     was alive when the scene began and one of them is now lethal (life<=1), destroyed, gone or authoritatively dead.
//     Fallback when the base routines leave it current: cine.destroyed=true, clear curCine, unlockControls,
//     cancelSkillControlLock. The boss is never killed locally.
//  5. One line per tryToPreventDeath call (both roles), per natural end of a recorded outro and per release (WRN).
//
// Rules (PLAN-ALPHA2.md section 4): add-on only; release member names; every base-mod method called, patched or relied on is
// pinned by its IL SHA-256 (plus the release DLL 378BE6B0 and the native boot image A37869A3); the GameProxy members are
// pinned symbolically (BossOutroProxyFingerprint.cs); every body in try/catch behind BossOutroFaultGate; kill switch
// COOP_BOSS_OUTRO_DISABLE=1 (nothing is patched or hooked); rate-limited lines. The decisions are BossOutroRules.cs.
// Game thread only (DCCM IOnFrameUpdate, the native hook dispatch and the base mod's mob hooks all run on it).
public sealed class BossOutroCall
{
    internal long CinePtr;
    internal int CineUid;
    internal int LifeBefore;
}

public static class BossOutroPatch
{
    public const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string NativePin="A37869A3C91443B50322B2CDF29595C9644AB8FC1280342F1F17C6F616A15534";
    public const string HarmonyPin="70E7647876B6C3D874EA99B56627A95D1DCE2E1DD791981A002578A2FEBC9BBD";   // HarmonyX 2.16.1: void finalizer rethrows
    public const string Owner="CoopBossOutroFix.Outro.20261112";
    public const string Switch="COOP_BOSS_OUTRO_DISABLE";
    public const int TryToPreventDeathFunction=11626;      // f@11626 en.mob.boss.Behemoth.tryToPreventDeath
    const string MobsType="DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization";
    const BindingFlags S=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    const BindingFlags I=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;

    // IL body SHA-256 of every base-mod method this part patches, calls or whose behaviour it relies on (release 0.5.1,
    // 378BE6B0; lengths equal the release IL dump headers; the same hashes as CoopOpt's pins where both pin a method).
    public static readonly IReadOnlyList<(string Type,string Method,int Length,string Sha)> IlPins=Array.AsReadOnly(new[]{
        (MobsType,"ShouldProcessClientVisualState",114,"0276597818E70CF03227C47397C6A4A14F7BF974DC67D3F07B9488FD01BF07DE"),      // patched (prefix)
        (MobsType,"RunWithAuthoritativeClientMobDie",108,"53D81C1E0736929F53FA15427855E1F2B59DDA3B9802C6C261DED1BE171D84C1"),    // patched (finalizer)
        (MobsType,"ApplyClientAnimationStateBeforeUpdate",158,"E1FAF98341392536518635B1C1090C3D112BFC0FEDA8A22AA3EA9A831F1A3444"),// caller 1 of the guard
        (MobsType,"ApplyInterpolatedState",1118,"BDF8B2617982D9F6680DC9CFCF050D81131FE0AB5D3A7E1C4D191F9F5B788C0C"),            // caller 2 of the guard
        (MobsType,"TryQueueClientMobAttack",118,"C944B7E15B9586C9DB5C8BFD7C6BD974ADB5E9A921E76F9B4BEC83348E8C18E6"),           // patched (prefix, step 2b)
        (MobsType,"ApplyIncomingHostMobAttacks",287,"5DEC41C7F3BC00C9453DD1BBE510F57D3A8A6543621AB45F105C5DFB225085E3"),       // caller 1 of the attack guard (marks applied after)
        (MobsType,"RetryPendingClientBossAttacks",390,"EA4557A4D89A5908B5F294C36CF2AD7E4A142C70D0310700BDC8D0CB3690E322"),     // caller 2 of the attack guard (removes, marks applied after)
        (MobsType,"RunClientAuthoritativeBossDeath",276,"68F3B60139AE41D59CFD38FED84D6C555612166BF903B7A7EDEB21C06F5ABB95"),    // the boss death apply
        (MobsType,"IsClient",23,"82DF264B773CB1F5B32D62D163CF28838FA6BB536495F168310E544575646054"),
        ("DeadCellsMultiplayerMod.LobbySession","get_NetRef",6,"6DA5F23A4D13988DE21B0F637DD992C0DF2805337998BF11708590761A8CC2A7"),
        ("DeadCellsMultiplayerMod.LobbySession","IsInActualRun",14,"9163568D5920EEAD88209D8F9B938EA666E82A5D266A37FE3A64F6AFDFB89A1C"),
        ("DeadCellsMultiplayerMod.Mobs.Bosses.BossSyncHelpers","IsBossMob",88,"872788C212B308E8B0962721F77D4AAC9824FD4D716BE930D4D474C77D943533"),
        ("DeadCellsMultiplayerMod.ModEntry","SuppressRemoteBossDeathCineState",123,"075188A37B0B6374FC5046E1737832DC8B819BA88422C7CCA50DEA5918EEDD42"),
        ("DeadCellsMultiplayerMod.ModEntry","RestoreViewportToLocalHero",69,"A53D2870BD7FF72D15C670159685EF93590C87742553461B3E17D8668FCC0F4D")});

    static readonly object InstallSync=new();
    static bool _attempted;
    static volatile bool _published;
    public static bool Ready=>_published;
    public static bool Disabled{get;private set;}
    public static string InitializationError{get;private set;}
    public static string ProxyCheck{get;private set;}

    // ---- bound at install (reflection only; no type initializer of the base mod runs here) ----
    static Func<NetNode> _netRef;
    static Func<NetNode,bool> _isClient;
    static Func<bool> _inRun;
    static Func<Mob,bool> _isBoss;
    static Action<ModEntry,dc.GameCinematic> _suppress;
    static Action<ModEntry> _restore;
    static FieldInfo _fBossLevels,_fSync,_fTracked;
    static MethodInfo _visualTarget,_dieTarget,_attackTarget;
    static EventInfo _hookEvent;
    static Delegate _hookHandler;
    // ---- read at first use, in a run (the base mod's statics are initialised by then) ----
    static HashSet<string> _bossLevels;
    static object _sync;
    static List<Mob> _tracked;

    // ---- game-thread state ----
    static readonly BossOutroFaultGate Gate=new();
    static readonly BossOutroLogBudget CallLog=new(48),EndLog=new(32),ReleaseLog=new(32);
    sealed class BossOutroActive
    {
        public long BossPtr;public Mob Boss;public string BossType="";public long CinePtr;public int CineUid;public long Started;public long GuardCalls;public long AttackHolds;
        public bool DeathApplied;public bool DeathApplyThrew;
    }
    sealed class BossOutroWatch
    {
        public long CinePtr;public int CineUid;public long Started;public long LastCheck;public bool BossLevel;public string Level="";public bool Released;
        public readonly List<(long Ptr,Mob Mob,string Type)> Bosses=new();public readonly HashSet<long> DeathApplied=new();
    }
    static BossOutroActive _active;
    static BossOutroWatch _watch;
    static volatile bool _guardArmed;
    static long _guardBossPtr;
    static long _hostCalls,_clientCalls,_recorded,_guardCalls,_attackHolds,_nativeEnds,_releases,_fallbacks,_deathNotes;

    // ---- read by the unit probe (CoopBossOutroProbe, reflection) ----
    public static long HostCalls=>Interlocked.Read(ref _hostCalls);
    public static long ClientCalls=>Interlocked.Read(ref _clientCalls);
    public static long Recorded=>Interlocked.Read(ref _recorded);
    public static long GuardCalls=>Interlocked.Read(ref _guardCalls);
    public static long AttackHolds=>Interlocked.Read(ref _attackHolds);
    public static long NativeEnds=>Interlocked.Read(ref _nativeEnds);
    public static long Releases=>Interlocked.Read(ref _releases);
    public static long Fallbacks=>Interlocked.Read(ref _fallbacks);
    public static long DeathNotes=>Interlocked.Read(ref _deathNotes);
    public static long Faults=>Gate.Faults;
    public static bool Tripped=>Gate.Tripped;
    public static bool GuardArmed=>_guardArmed;
    public static string LastRelease{get;private set;}="";
    public static long ActiveBossPointer=>_active?.BossPtr??0;
    public static long ActiveScenePointer=>_active?.CinePtr??0;
    public static int ActiveSceneUid=>_active?.CineUid??0;
    public static double ActiveOutroSeconds=>_active is {} a?Seconds(a.Started):-1;

    // ================================================================ install ================================================
    public static void Install()
    {
        lock(InstallSync)
        {
            if(_attempted){if(InitializationError!=null)throw new InvalidOperationException("Earlier boss outro initialization failed: "+InitializationError);return;}
            _attempted=true;
            Disabled=Environment.GetEnvironmentVariable(Switch)=="1";
            if(Disabled)return;                     // kill switch: nothing is patched or hooked
            Harmony harmony=null;bool hookAdded=false;
            try
            {
                VerifyFiles();
                ProxyCheck=ValidateProxy(typeof(Mob).Assembly);
                Bind();
                BindHook();
                harmony=new Harmony(Owner);
                harmony.Patch(_visualTarget,prefix:new HarmonyMethod(Own(nameof(BeforeVisualState))));
                harmony.Patch(_dieTarget,finalizer:new HarmonyMethod(Own(nameof(AfterAuthoritativeDie))));
                harmony.Patch(_attackTarget,prefix:new HarmonyMethod(Own(nameof(BeforeQueueAttack))));
                var visual=Harmony.GetPatchInfo(_visualTarget);var die=Harmony.GetPatchInfo(_dieTarget);var attack=Harmony.GetPatchInfo(_attackTarget);
                if(visual==null||visual.Prefixes.Count(p=>p.owner==Owner)!=1||visual.Postfixes.Any(p=>p.owner==Owner)||visual.Finalizers.Any(p=>p.owner==Owner)||visual.Transpilers.Any(p=>p.owner==Owner))
                    throw new InvalidOperationException("ShouldProcessClientVisualState prefix not installed exactly once");
                if(die==null||die.Finalizers.Count(p=>p.owner==Owner)!=1||die.Prefixes.Any(p=>p.owner==Owner)||die.Postfixes.Any(p=>p.owner==Owner)||die.Transpilers.Any(p=>p.owner==Owner))
                    throw new InvalidOperationException("RunWithAuthoritativeClientMobDie finalizer not installed exactly once");
                if(attack==null||attack.Prefixes.Count(p=>p.owner==Owner)!=1||attack.Postfixes.Any(p=>p.owner==Owner)||attack.Finalizers.Any(p=>p.owner==Owner)||attack.Transpilers.Any(p=>p.owner==Owner))
                    throw new InvalidOperationException("TryQueueClientMobAttack prefix not installed exactly once");
                hookAdded=true;_hookEvent.AddEventHandler(null,_hookHandler);
                _published=true;
            }
            catch(Exception error)
            {
                _published=false;InitializationError=error.GetBaseException().Message;
                if(hookAdded)try{_hookEvent.RemoveEventHandler(null,_hookHandler);}catch(Exception r){InitializationError+="; hook rollback: "+r.Message;}
                if(harmony!=null)
                    foreach(var target in new[]{_visualTarget,_dieTarget,_attackTarget})
                        if(target!=null)try{harmony.Unpatch(target,HarmonyPatchType.All,Owner);}catch(Exception r){InitializationError+="; rollback: "+r.Message;}
                throw;
            }
        }
    }
    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);
    static void VerifyFiles()
    {
        var original=typeof(ModEntry).Assembly;
        if(original.GetName().Version!=new Version(0,5,1,0)||Hash(original.Location)!=BasePin||
            Hash(Path.Combine(Path.GetDirectoryName(typeof(Hero).Assembly.Location),"hlboot.dat"))!=NativePin)
            throw new InvalidOperationException("CoopBossOutroFix requires exact community0.5.1/native35.13.5");
        if(string.IsNullOrEmpty(typeof(Harmony).Assembly.Location)||Hash(typeof(Harmony).Assembly.Location)!=HarmonyPin)
            throw new InvalidOperationException("CoopBossOutroFix requires the pinned HarmonyX build");
    }
    // Raw pin, or the symbolic path (generator pins, loaded identity, fingerprint), exactly as the Alpha1 verifiers.
    static string ValidateProxy(Assembly proxy)
    {
        // CoopPack S1: the raw pin from the shared GameProxy image (no second 27 MB read and hash); the symbolic fallback
        // walks this part's own image over the same bytes with the shared generator pins (same expressions, same order).
        var shared=global::CoopPackRuntime.PackProxyImage.Acquire(proxy);
        try
        {
            if(shared.RawSha256==BossOutroProxyCompatibility.RawPin)return "raw";
            using var image=BossOutroProxyImage.FromBytes(shared.Bytes);
            var mode=BossOutroProxyCompatibility.ValidateImage(image,proxy.ManifestModule.ModuleVersionId,proxy.FullName,shared.Generators);
            try{Serilog.Log.Information("[CoopBossOutroFix] Generated GameProxy ABI/IL verified: raw={Raw} symbolic={Symbolic}",shared.RawSha256,BossOutroProxyCompatibility.FingerprintPin);}catch{}
            return mode;
        }
        finally{shared.Release();}
    }
    // The four generator pins with the loaded-identity check (CoopPackRuntime.PackProxyImage.Generators, same expressions).
    static IReadOnlyDictionary<string,string> Generators()
    {
        var pins=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var assembly in new[]{typeof(ModCore.Mods.ModBase).Assembly,typeof(HashlinkNET.Compiler.HashlinkCompiler).Assembly,
            typeof(ModuleDefinition).Assembly,typeof(HaxeProxy.Runtime.HaxeProxyBase).Assembly})
        {
            byte[] image=File.ReadAllBytes(assembly.Location);
            using(var stream=new MemoryStream(image,false))using(var parsed=AssemblyDefinition.ReadAssembly(stream))
                if(parsed.MainModule.Mvid!=assembly.ManifestModule.ModuleVersionId || parsed.Name.FullName!=assembly.FullName)
                    throw new InvalidOperationException("Loaded generator identity differs from its file");
            pins.Add(assembly.GetName().Name,Convert.ToHexString(SHA256.HashData(image)));
        }
        return pins;
    }
    static MethodInfo Method(Type t,string name,bool isStatic,Type ret,params Type[] args)
    {
        var m=t.GetMethod(name,(isStatic?S:I)|BindingFlags.DeclaredOnly,null,args,null);
        if(m==null||m.IsStatic!=isStatic||(ret!=null&&m.ReturnType!=ret))throw new InvalidOperationException("Pinned method missing: "+t.Name+"."+name);
        return m;
    }
    static FieldInfo Field(Type t,string name,Type type,bool readOnly)
    {
        var f=t.GetField(name,S|BindingFlags.DeclaredOnly);
        if(f==null||f.FieldType!=type||(readOnly&&!f.IsInitOnly))throw new InvalidOperationException("Pinned field mismatch: "+t.Name+"."+name);
        return f;
    }
    // The one declared method of that name (a parameter type the add-on cannot name, e.g. a nested base-mod type): unique by
    // name, static or instance as pinned, the pinned return type and parameter count; its body is then pinned by IL hash.
    static MethodInfo UniqueMethod(Type t,string name,bool isStatic,Type ret,int parameters)
    {
        var all=t.GetMethods((isStatic?S:I)|BindingFlags.DeclaredOnly).Where(m=>m.Name==name).ToArray();
        if(all.Length!=1||all[0].IsStatic!=isStatic||all[0].ReturnType!=ret||all[0].GetParameters().Length!=parameters)throw new InvalidOperationException("Pinned method missing or overloaded: "+t.Name+"."+name);
        return all[0];
    }
    static void Pin(MethodInfo m,int length,string sha)
    {
        var il=m.GetMethodBody()?.GetILAsByteArray();
        if(il==null||il.Length!=length||Convert.ToHexString(SHA256.HashData(il))!=sha)throw new InvalidOperationException("Pinned body changed: "+m.DeclaringType?.Name+"."+m.Name);
    }
    static T Make<T>(MethodInfo m) where T:Delegate=>(T)Delegate.CreateDelegate(typeof(T),m);
    static void Bind()
    {
        var original=typeof(ModEntry).Assembly;
        var ms=original.GetType(MobsType,true);
        if(ms.BaseType!=typeof(object))throw new InvalidOperationException("MobsSynchronization is not a plain managed class");
        var lobby=original.GetType("DeadCellsMultiplayerMod.LobbySession",true);
        var boss=original.GetType("DeadCellsMultiplayerMod.Mobs.Bosses.BossSyncHelpers",true);
        var bound=new Dictionary<string,MethodInfo>(StringComparer.Ordinal)
        {
            ["ShouldProcessClientVisualState"]=Method(ms,"ShouldProcessClientVisualState",true,typeof(bool),typeof(Mob)),
            ["RunWithAuthoritativeClientMobDie"]=Method(ms,"RunWithAuthoritativeClientMobDie",true,typeof(void),typeof(Mob),typeof(Action)),
            ["ApplyClientAnimationStateBeforeUpdate"]=Method(ms,"ApplyClientAnimationStateBeforeUpdate",true,typeof(void),typeof(Mob)),
            ["ApplyInterpolatedState"]=Method(ms,"ApplyInterpolatedState",true,typeof(void),typeof(Mob)),
            // TryQueueClientMobAttack(Mob mob, string skillId, bool requiresTargetInArea, int? data, int targetUserId, int attackDir)
            ["TryQueueClientMobAttack"]=Method(ms,"TryQueueClientMobAttack",true,typeof(void),typeof(Mob),typeof(string),typeof(bool),typeof(int?),typeof(int),typeof(int)),
            // ApplyIncomingHostMobAttacks(IReadOnlyList<NetNode.MobAttack>) and RetryPendingClientBossAttacks(): relied on, not called
            ["ApplyIncomingHostMobAttacks"]=UniqueMethod(ms,"ApplyIncomingHostMobAttacks",true,typeof(void),1),
            ["RetryPendingClientBossAttacks"]=Method(ms,"RetryPendingClientBossAttacks",true,typeof(void)),
            ["RunClientAuthoritativeBossDeath"]=Method(ms,"RunClientAuthoritativeBossDeath",true,typeof(void),typeof(Mob),typeof(string)),
            ["IsClient"]=Method(ms,"IsClient",true,typeof(bool),typeof(NetNode)),
            ["get_NetRef"]=Method(lobby,"get_NetRef",true,typeof(NetNode)),
            ["IsInActualRun"]=Method(lobby,"IsInActualRun",true,typeof(bool)),
            ["IsBossMob"]=Method(boss,"IsBossMob",true,typeof(bool),typeof(Mob)),
            ["SuppressRemoteBossDeathCineState"]=Method(typeof(ModEntry),"SuppressRemoteBossDeathCineState",false,typeof(void),typeof(dc.GameCinematic)),
            ["RestoreViewportToLocalHero"]=Method(typeof(ModEntry),"RestoreViewportToLocalHero",false,typeof(void)),
        };
        foreach(var (_,name,length,sha) in IlPins)Pin(bound[name],length,sha);
        _visualTarget=bound["ShouldProcessClientVisualState"];_dieTarget=bound["RunWithAuthoritativeClientMobDie"];_attackTarget=bound["TryQueueClientMobAttack"];
        _netRef=Make<Func<NetNode>>(bound["get_NetRef"]);
        _isClient=Make<Func<NetNode,bool>>(bound["IsClient"]);
        _inRun=Make<Func<bool>>(bound["IsInActualRun"]);
        _isBoss=Make<Func<Mob,bool>>(bound["IsBossMob"]);
        _suppress=Make<Action<ModEntry,dc.GameCinematic>>(bound["SuppressRemoteBossDeathCineState"]);
        _restore=Make<Action<ModEntry>>(bound["RestoreViewportToLocalHero"]);
        _fBossLevels=Field(typeof(ModEntry),"BossLevelIds",typeof(HashSet<string>),true);
        _fSync=Field(ms,"Sync",typeof(object),true);
        _fTracked=Field(ms,"trackedMobs",typeof(List<Mob>),true);
    }
    // The native hook: the generated delegate types derive from the runtime CoreLib's MulticastDelegate, which this add-on
    // cannot name (CS0012), so the handler is a DynamicMethod with the exact handler signature (CoopLobby LobbyUi P4 and
    // CoopFrameFix PlayerTint pattern): call BeforePreventDeath, orig(self, atk, x), AfterPreventDeath; return orig's value.
    static void BindHook()
    {
        var proxy=typeof(Mob).Assembly;
        var hookType=proxy.GetType(BossOutroProxyFingerprint.HookType,true);
        _hookEvent=hookType.GetEvent(BossOutroProxyFingerprint.HookEvent,S)??throw new InvalidOperationException("Hook_Behemoth.tryToPreventDeath missing");
        if(_hookEvent.AddMethod==null||!_hookEvent.AddMethod.IsStatic||_hookEvent.RemoveMethod==null||!_hookEvent.RemoveMethod.IsStatic)
            throw new InvalidOperationException("Hook_Behemoth.tryToPreventDeath accessors changed");
        if(!BossOutroHookShape.StartsWithFunction(_hookEvent.AddMethod.GetMethodBody()?.GetILAsByteArray(),TryToPreventDeathFunction)||
           !BossOutroHookShape.StartsWithFunction(_hookEvent.RemoveMethod.GetMethodBody()?.GetILAsByteArray(),TryToPreventDeathFunction))
            throw new InvalidOperationException("Hook_Behemoth.tryToPreventDeath no longer hooks f@"+TryToPreventDeathFunction);
        var handlerType=_hookEvent.EventHandlerType;var invoke=handlerType?.GetMethod("Invoke");var ps=invoke?.GetParameters();
        if(invoke==null||invoke.ReturnType!=typeof(bool)||ps.Length!=4||ps[1].ParameterType!=typeof(Behemoth)||ps[2].ParameterType!=typeof(AttackData)||
           ps[3].ParameterType!=typeof(double)||!typeof(Delegate).IsAssignableFrom(ps[0].ParameterType))
            throw new InvalidOperationException("Pinned tryToPreventDeath handler ABI changed");
        var origType=ps[0].ParameterType;var origInvoke=origType.GetMethod("Invoke");var os=origInvoke?.GetParameters();
        if(origInvoke==null||origInvoke.ReturnType!=typeof(bool)||os.Length!=3||os[0].ParameterType!=typeof(Behemoth)||os[1].ParameterType!=typeof(AttackData)||os[2].ParameterType!=typeof(double))
            throw new InvalidOperationException("Pinned tryToPreventDeath original ABI changed");
        var dm=new DynamicMethod("CoopBossOutroFix_tryToPreventDeath",typeof(bool),new[]{origType,typeof(Behemoth),typeof(AttackData),typeof(double)},typeof(BossOutroPatch).Module,true);
        var il=dm.GetILGenerator();
        var call=il.DeclareLocal(typeof(BossOutroCall));var result=il.DeclareLocal(typeof(bool));
        il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Call,Own(nameof(BeforePreventDeath)));il.Emit(OpCodes.Stloc,call);
        il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldarg_2);il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Callvirt,origInvoke);il.Emit(OpCodes.Stloc,result);
        il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldloc,call);il.Emit(OpCodes.Ldloc,result);il.Emit(OpCodes.Call,Own(nameof(AfterPreventDeath)));
        il.Emit(OpCodes.Ldloc,result);il.Emit(OpCodes.Ret);
        _hookHandler=dm.CreateDelegate(handlerType);
        RuntimeHelpers.PrepareDelegate(_hookHandler);
    }
    static MethodInfo Own(string name)=>typeof(BossOutroPatch).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic)??throw new MissingMethodException(nameof(BossOutroPatch),name);

    // ================================================================ hooks ==================================================
    static bool IsClient(){var net=_netRef();return _isClient(net);}
    static double Seconds(long since)=>(Stopwatch.GetTimestamp()-since)/(double)Stopwatch.Frequency;
    // Native identity (HaxeObject : HaxeProxyBase : IHashlinkPointer); typed, so a non-proxy argument does not compile.
    static long Ptr(HaxeProxy.Runtime.HaxeProxyBase o)=>o==null?0:o.HashlinkPointer.ToInt64();

    // Step 1, before orig: which scene is current.
    static BossOutroCall BeforePreventDeath(Behemoth self)
    {
        if(!_published||Gate.Tripped)return null;
        try
        {
            var call=new BossOutroCall();
            var cine=dc.pr.Game.Class.ME?.curCine;
            if(cine!=null){call.CinePtr=Ptr(cine);call.CineUid=cine.uniqId;}
            if(self!=null)call.LifeBefore=self.life;
            return call;
        }
        catch(Exception error){Fault("tryToPreventDeath (before)",error);return null;}
    }
    // Step 1, after orig: record the client's outro; one line per call (both roles).
    static void AfterPreventDeath(Behemoth self,BossOutroCall call,bool result)
    {
        if(call==null||!_published||Gate.Tripped)return;
        try
        {
            bool client=IsClient();
            if(client)Interlocked.Increment(ref _clientCalls);else Interlocked.Increment(ref _hostCalls);
            var game=dc.pr.Game.Class.ME;var cine=game?.curCine;
            long ptr=Ptr(cine);int uid=cine!=null?cine.uniqId:0;bool destroyed=cine!=null&&cine.destroyed;string type=cine?.GetType().FullName??"";
            bool record=BossOutroRules.ShouldRecord(client,result,call.CinePtr,call.CineUid,ptr,uid,destroyed,type);
            string outcome=!client?"host (no guard)":record?"recorded":!result?"not recorded: returned false":ptr==0?"not recorded: no scene":
                BossOutroRules.SameScene(ptr,uid,call.CinePtr,call.CineUid)?"not recorded: no new scene":destroyed?"not recorded: scene destroyed":"not recorded: scene type";
            long bossPtr=Ptr(self);
            if(record)
            {
                var previous=_active;
                _active=new BossOutroActive{BossPtr=bossPtr,Boss=self,BossType=self?.GetType().Name??"",CinePtr=ptr,CineUid=uid,Started=Stopwatch.GetTimestamp()};
                _guardBossPtr=bossPtr;_guardArmed=true;
                Interlocked.Increment(ref _recorded);
                if(previous!=null)outcome+="; replaces the outro of "+Seconds(previous.Started).ToString("0.0")+" s";
            }
            var grant=CallLog.Take();
            if(grant!=BossOutroLogBudget.Grant.Silent)
                Serilog.Log.Information("[CoopBossOutroFix] {Role} tryToPreventDeath: {Boss} life {Before}->{After}/{Max} result={Result} scene={Scene} uid={Uid} outro={Outcome}{More}",
                    client?"client":"host",self?.GetType().Name??"?",call.LifeBefore,self?.life??0,self?.maxLife??0,result,type.Length==0?"none":type,uid,outcome,
                    grant==BossOutroLogBudget.Grant.LogLast?" (further tryToPreventDeath lines are counted, not logged)":"");
            Gate.Ok();
        }
        catch(Exception error){Fault("tryToPreventDeath (after)",error);}
    }
    // Step 2: Harmony prefix on MobsSynchronization.ShouldProcessClientVisualState(Mob).
    static bool BeforeVisualState(Mob __0,ref bool __result)
    {
        if(!_guardArmed)return true;
        try
        {
            var a=_active;
            if(__0==null||a==null)return true;
            long mob=Ptr(__0);
            if(mob!=_guardBossPtr)return true;
            if(!BossOutroRules.GuardApplies(_guardArmed,_guardBossPtr,mob,SceneCurrent(a)))return true;
            __result=false;a.GuardCalls++;Interlocked.Increment(ref _guardCalls);
            return false;
        }
        catch(Exception error){Fault("visual-state guard",error);return true;}
    }
    // Step 2b: Harmony prefix on MobsSynchronization.TryQueueClientMobAttack(Mob, ...). Same rule as step 2: for the recorded
    // boss while its scene is the live current scene, the replayed host attack is skipped (the original is not run); both
    // pinned callers then mark it applied, so it is dropped, never replayed after the scene. Every other mob, and the boss
    // after its scene, is left to the original method.
    static bool BeforeQueueAttack(Mob __0)
    {
        if(!_guardArmed)return true;
        try
        {
            var a=_active;
            if(__0==null||a==null)return true;
            long mob=Ptr(__0);
            if(mob!=_guardBossPtr)return true;
            if(!BossOutroRules.GuardApplies(_guardArmed,_guardBossPtr,mob,SceneCurrent(a)))return true;
            a.AttackHolds++;Interlocked.Increment(ref _attackHolds);
            return false;
        }
        catch(Exception error){Fault("attack-replay guard",error);return true;}
    }
    // The recorded scene is still Game.curCine (same pointer and uniqId) and not marked destroyed.
    static bool SceneCurrent(BossOutroActive a)
    {
        var cine=dc.pr.Game.Class.ME?.curCine;
        return cine!=null&&BossOutroRules.SameScene(Ptr(cine),cine.uniqId,a.CinePtr,a.CineUid)&&!cine.destroyed;
    }
    // Step 3: Harmony finalizer on MobsSynchronization.RunWithAuthoritativeClientMobDie(Mob, Action). Notes only; the scene is
    // ended by the next frame tick (never inside the base mod's death apply). void + __exception: HarmonyX rethrows.
    static void AfterAuthoritativeDie(Mob __0,Exception __exception)
    {
        if(!_published||Gate.Tripped||__0==null)return;
        try
        {
            var a=_active;var w=_watch;
            if(a==null&&w==null)return;
            long mob=Ptr(__0);
            if(a!=null&&a.BossPtr==mob){a.DeathApplied=true;a.DeathApplyThrew|=__exception!=null;Interlocked.Increment(ref _deathNotes);}
            if(w!=null&&w.Bosses.Any(b=>b.Ptr==mob)){w.DeathApplied.Add(mob);Interlocked.Increment(ref _deathNotes);}
        }
        catch(Exception error){Fault("death note",error);}
    }

    // ================================================================ frame ==================================================
    // DCCM IOnFrameUpdate (after the game's Boot.update; in the pack: forwarded by PackHost). Never throws.
    public static void Tick()
    {
        if(!_published||Gate.Tripped)return;
        try
        {
            if(!IsClient()){if(_active!=null||_watch!=null)Forget();return;}
            var game=dc.pr.Game.Class.ME;
            var cine=game?.curCine;
            long ptr=Ptr(cine);int uid=cine!=null?cine.uniqId:0;bool destroyed=cine!=null&&cine.destroyed;
            var a=_active;
            if(a!=null)
            {
                bool same=cine!=null&&BossOutroRules.SameScene(ptr,uid,a.CinePtr,a.CineUid);
                bool current=same&&!destroyed;
                // The native end marks the scene destroyed (f@29029) while it is still curCine; disposal clears curCine later.
                if(!current){Ended(a,cine==null||same?"scene finished":"replaced by "+cine.GetType().FullName);}
                else
                {
                    bool bossDestroyed=false;try{bossDestroyed=a.Boss==null||a.Boss.destroyed;}catch{bossDestroyed=true;}
                    var release=BossOutroRules.DecideRecorded(true,a.DeathApplied,bossDestroyed,Seconds(a.Started));
                    if(release!=BossOutroRules.Release.None)ReleaseScene(game,cine,ptr,uid,release,Seconds(a.Started),a.Boss,"recorded",a.DeathApplyThrew,
                        " held=animation:"+a.GuardCalls+",attacks:"+a.AttackHolds);
                }
                Gate.Ok();return;
            }
            string type=cine?.GetType().FullName??"";
            if(cine==null||destroyed||!BossOutroRules.IsPlainScene(type)){_watch=null;Gate.Ok();return;}
            var w=_watch;
            if(w==null||!BossOutroRules.SameScene(ptr,uid,w.CinePtr,w.CineUid))_watch=w=StartWatch(ptr,uid);
            if(w.Released||!w.BossLevel||w.Bosses.Count==0){Gate.Ok();return;}
            double age=Seconds(w.Started);
            if(age<BossOutroRules.StuckSeconds||(w.LastCheck!=0&&Seconds(w.LastCheck)<BossOutroRules.RecheckSeconds)){Gate.Ok();return;}
            w.LastCheck=Stopwatch.GetTimestamp();
            var (lethal,which)=AnyBossLethalOrGone(w);
            var verdict=BossOutroRules.DecideUnrecorded(true,_inRun(),w.BossLevel,type,destroyed,age,w.Bosses.Count,lethal);
            if(verdict!=BossOutroRules.Release.None){w.Released=true;ReleaseScene(game,cine,ptr,uid,verdict,age,which,"unrecorded",false,"");}
            Gate.Ok();
        }
        catch(Exception error){Fault("frame",error);}
    }
    static void Forget(){_active=null;_watch=null;_guardArmed=false;_guardBossPtr=0;}
    static void Ended(BossOutroActive a,string how)
    {
        _active=null;_guardArmed=false;_guardBossPtr=0;
        Interlocked.Increment(ref _nativeEnds);
        var grant=EndLog.Take();
        if(grant!=BossOutroLogBudget.Grant.Silent)
            Serilog.Log.Information("[CoopBossOutroFix] client outro ended after {Seconds:0.0} s ({How}); host animation held back on {Calls} calls; host attacks held back: {Attacks}{More}",
                Seconds(a.Started),how,a.GuardCalls,a.AttackHolds,
                grant==BossOutroLogBudget.Grant.LogLast?" (further end lines are counted, not logged)":"");
    }
    static BossOutroWatch StartWatch(long ptr,int uid)
    {
        var w=new BossOutroWatch{CinePtr=ptr,CineUid=uid,Started=Stopwatch.GetTimestamp()};
        if(!_inRun())return w;
        var hero=ModEntry.me;var level=hero?._level;string id=level?.map?.id?.ToString()??"";
        w.Level=id;
        _bossLevels??=(HashSet<string>)_fBossLevels.GetValue(null);
        w.BossLevel=id.Length>0&&_bossLevels!=null&&_bossLevels.Contains(id);
        if(!w.BossLevel)return w;
        long levelPtr=Ptr(level);
        foreach(var m in TrackedSnapshot())
        {
            if(m==null||m.destroyed||m.life<=0||Ptr(m._level)!=levelPtr||!_isBoss(m))continue;
            w.Bosses.Add((Ptr(m),m,m.GetType().Name));
        }
        return w;
    }
    // The base mod's tracked mobs, copied under its own lock (no other call is made while it is held).
    static Mob[] TrackedSnapshot()
    {
        _sync??=_fSync.GetValue(null);_tracked??=(List<Mob>)_fTracked.GetValue(null);
        if(_sync==null||_tracked==null)return Array.Empty<Mob>();
        lock(_sync)return _tracked.ToArray();
    }
    static (bool,Mob) AnyBossLethalOrGone(BossOutroWatch w)
    {
        var live=new HashSet<long>(TrackedSnapshot().Where(m=>m!=null).Select(Ptr));
        foreach(var (bossPtr,boss,_) in w.Bosses)
        {
            bool tracked=live.Contains(bossPtr),destroyed=false;int life=int.MaxValue;
            if(tracked)try{destroyed=boss.destroyed;life=boss.life;}catch{destroyed=true;}
            if(BossOutroRules.BossLethalOrGone(tracked,w.DeathApplied.Contains(bossPtr),destroyed,life))return (true,boss);
        }
        return (false,null);
    }
    // Steps 3 and 4: the base mod's own routines, then the fallback when they left the scene current or unmarked.
    static void ReleaseScene(dc.pr.Game game,dc.GameCinematic cine,long ptr,int uid,BossOutroRules.Release reason,double age,Mob boss,string kind,bool applyThrew,string held)
    {
        string steps="";
        var owner=ModEntry.Instance;
        if(owner!=null)
        {
            try{_suppress(owner,cine);steps+="suppress;";}catch(Exception e){steps+="suppress threw "+e.GetBaseException().GetType().Name+";";}
            try{_restore(owner);steps+="viewport;";}catch(Exception e){steps+="viewport threw "+e.GetBaseException().GetType().Name+";";}
        }
        else steps+="no mod instance;";
        bool stillCurrent=false,marked=false;
        try{var now=game?.curCine;stillCurrent=now!=null&&BossOutroRules.SameScene(Ptr(now),now.uniqId,ptr,uid);}catch{stillCurrent=true;}
        try{marked=cine.destroyed;}catch{}
        if(BossOutroRules.NeedsFallback(stillCurrent,marked))
        {
            Interlocked.Increment(ref _fallbacks);steps+="fallback;";
            try{cine.destroyed=true;}catch{}
            try{var now=game?.curCine;if(now!=null&&BossOutroRules.SameScene(Ptr(now),now.uniqId,ptr,uid))game.curCine=null;}catch{}
            var me=ModEntry.me;
            try{me?.unlockControls();}catch{}
            try{me?.cancelSkillControlLock();}catch{}
        }
        if(kind=="recorded"){_active=null;_guardArmed=false;_guardBossPtr=0;}
        Interlocked.Increment(ref _releases);
        LastRelease=BossOutroRules.Describe(reason)+"/"+kind;
        int life=0,max=0;bool bossDestroyed=false;
        try{if(boss!=null){bossDestroyed=boss.destroyed;life=boss.life;max=boss.maxLife;}}catch{}
        var grant=ReleaseLog.Take();
        if(grant!=BossOutroLogBudget.Grant.Silent)
            Serilog.Log.Warning("[CoopBossOutroFix] released client {Kind} scene: reason={Reason}{Threw} age={Age:0.0} s scene=dc.GameCinematic uid={Uid} boss={Boss} life={Life}/{Max} destroyed={Destroyed} steps={Steps}{Held}{More}",
                kind,BossOutroRules.Describe(reason),applyThrew?" (the death apply threw)":"",age,uid,boss?.GetType().Name??"none",life,max,bossDestroyed,steps,held??"",
                grant==BossOutroLogBudget.Grant.LogLast?" (further release lines are counted, not logged)":"");
    }
    static void Fault(string what,Exception error)
    {
        var (first,trippedNow)=Gate.Fail();
        if(trippedNow){_guardArmed=false;_active=null;_watch=null;}
        try
        {
            if(first)Serilog.Log.Warning("[CoopBossOutroFix] {What} fault: {Error}",what,error.GetBaseException().Message);
            if(trippedNow)Serilog.Log.Warning("[CoopBossOutroFix] Outro guard switched off for this session after {Faults} faults",Gate.Faults);
        }
        catch{}
    }
}
