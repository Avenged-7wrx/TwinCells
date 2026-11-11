using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using dc.en;
using dc.pr;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using ModCore.Storage;

// Part OptClient of CoopOpt (will join CoopPack 0.1.0-alpha): the CLIENT per-enemy steady state of
// MobsSynchronization.Hook_Mob_preUpdate (design-client-per-enemy.json D1 with round2\client-per-enemy\skeptic.md F1-F8).
//
// On a client, for a mob in state S (OptClientRules.cs header), the release body of Hook_Mob_preUpdate does nothing
// observable except orig(self): IsSyncMob twice, EnsureMobTracked, UpdateClientMobAiAuthority only read. One Harmony
// prefix (Priority.Last, on the plain managed class MobsSynchronization : System.Object) re-tests S with the same live
// reads, calls orig itself and skips the body. Anything else, or any exception before orig, runs the original body.
// What is saved per S mob-frame: 2 x IsSyncMob (2 settings locks + normalisation, the hostile/team proxy reads),
// EnsureMobTracked with CoopSyncFixes' reflective identity prefix, 3 of 4 lock(Sync), RefreshClientNetworkAttackState,
// TryLockClientMobAiAuthority. The 4 game-facing tests (role, by-type, cine/boss, skill) are the mod's own methods.
//
// This file is the game side only: binding, pins, the install transaction, the guard's Harmony queries and the Glue
// (the reads, as OptClientRules.IEnv). The decision, the sampling, the probe and the verdicts are OptClientPath in
// OptClientRules.cs, executed by the managed checks; the prefix and finalizer here only forward to it.
//
// Validation (CoopLoadFix ChainCache model): the first 4096 S-calls, the first 64 after every level-identity change and
// then every 256th are NOT served: the literal original body runs with `orig` replaced by a one-shot probe that compares,
// at the moment the body calls orig, a snapshot of the mod's tracking state (Count and _version of the 7 collections,
// validation flag, level identity, the mob's index / lock / attack membership, its aTarget) with the one taken in the
// prefix (skeptic F1). The finalizer judges it (orig exactly once, with self, nothing changed). One mismatch switches the
// fast path off for the session (the call itself already ran the original). The first hit is served only after 4096
// samples were judged equal (a probe that never reached the body or the finalizer switches the fast path off instead).
// COOP_OPT_SHADOW=all: every S-call is compared.
// Guard (F3): at install, at the first S-call and on each level-identity change, Harmony.GetPatchInfo of the hook and of
// every method whose work is skipped on the S path, including what those methods call there (settings normalisation
// helpers, Config<T>, the by-type lambda, the proxy getters). Allowed: our own prefix and finalizer, CoopSyncFixes'
// MatchTrackedNativeLevel on DoesLevelMatchCurrentIdentityLocked (pinned by its IL), and exactly the host part's A1
// prefixes BeforeSyncMob on IsSyncMob and BeforeHostile on IsMobHostileToPlayers. Anything else: no fast path.
// Pins: the mod DLL and the native game (OptCore.CheckPins), HarmonyX 2.16.1 (the probe relies on its `ref object __0`
// aliasing, __runOriginal and finalizer rethrow), ModCore, HashlinkSharp, HaxeProxy, and the IL of 20 release bodies.
// Switches: COOP_OPT_CLIENT_DISABLE=1 / COOP_OPT_DISABLE=1 (nothing is patched, CoopOpt.cs), COOP_OPT_SHADOW=all,
// COOP_OPT_STATS=1 (one "[CoopOpt] client ..." line every 5 s).
//
// Known differences (documented, not game-observable): EnsureDataNormalizedUnsafe's write-back of out-of-range settings
// (interpolation quality, HP multipliers, perk id) moves to the next other settings read (OnFrameUpdateCore reads
// EnableMobsSync every frame; in practice the data is always in range after the first normalisation); fewer DCCM wrapper
// conversions and less garbage; IsSyncMobByType fills the mod's per-type cache also for types the original answers
// through the hostile branch (pure, idempotent); an exception thrown by orig shows Harmony's replacement frame in its
// stack trace; the host part's A1 prefixes on IsSyncMob / IsMobHostileToPlayers are not reached for S mobs on the client
// (their counters are lower there); sampled and declined calls run the mod's pure tests twice (prefix and body).
public static class OptClient
{
    public const string Id=OptClientRules.ClientOwner;
    const string MobsType="DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization";
    // Framework files (same pins as OptHost and CoopLoadFix ChainCache).
    const string HarmonyPin="70E7647876B6C3D874EA99B56627A95D1DCE2E1DD791981A002578A2FEBC9BBD";
    const string ModCorePin="630FBBCEDFE8B2E66C5F75234BE80C2EC42FB178C30706E7398583D9FA1A14AE";
    const string HashlinkSharpPin="207FC8DD54D8A0A804E4051F90D2375F2C58CF6E69EFC26EF3E7EC502DFE8B63";
    const string HaxeProxyPin="0DF9697335FFDBA4461E599CEE063BC41E4B76C881CCAEDB08AE16DC4A27A8B2";
    static readonly object InstallSync=new();
    static bool _attempted,_installed,_stats;
    static readonly OptFaultGate Gate=new("OptClient");
    // The prefix and finalizer forward here. Set once by Install (switched off if the install fails, so a patch that a
    // failed rollback leaves behind stays inert); replaced only by notes\client\bind-check.ps1 in its offline process.
    static OptClientPathBase<Mob> _path;
    static long _startedAt;
    static Timer _timer;

    // ---- bound members (verified against the pinned 0.5.1 DLL at install) ----
    static MethodInfo _hook;
    static Type _origType;      // dc.en.Hook_Mob.orig_preUpdate (resolved by reflection, see OptClientRules.Invoker)
    static (MethodInfo Method,OptClientRules.GuardTarget Kind)[] _guarded;
    static Func<NetNode> _netRef;
    static Func<NetNode,bool> _isHost,_isClient;
    static Func<Mob,bool> _byType,_isBoss,_skill;
    static Func<bool> _cine,_ready,_pending;
    static Func<int> _token;
    static Func<Level> _current;
    static FieldInfo _fSync,_fTracked,_fIndices,_fLocked,_fActive,_fStart,_fLease,_fMobToId,_fSettingsRoot,_fConfig;
    // captured at the first call (the mod's type initializer has run: one of its instances is calling us)
    static object _settingsRoot;
    static Config<MultiplayerSettingsData> _config;

    public static bool FastPathOn{get{var p=_path;return _installed&&p!=null&&!p.Off;}}
    public static string SwitchOffReason=>_path?.OffReason;

    public static void Install()
    {
        lock(InstallSync)
        {
            if(_attempted)return;     // one attempt per session (a failed one leaves the part off)
            _attempted=true;
            var harmony=new Harmony(Id);MethodInfo target=null;
            try
            {
                OptCore.CheckPins();
                Bind();
                bool all=Environment.GetEnvironmentVariable("COOP_OPT_SHADOW")=="all";
                _stats=Environment.GetEnvironmentVariable("COOP_OPT_STATS")=="1";
                var foreign=GuardViolation(false);
                if(foreign!=null)throw new InvalidOperationException("patched by another module: "+foreign);
                _path=new OptClientPath<Mob,Level,Glue>(default,_origType,new OptClientRules.Sampler(all));   // builds the orig stubs for the real delegate type
                target=_hook;     // before Patch: a throw half way is still rolled back (Unpatch by owner)
                harmony.Patch(target,prefix:new HarmonyMethod(Own(nameof(BeforePreUpdate))){priority=Priority.Last},finalizer:new HarmonyMethod(Own(nameof(AfterPreUpdate))));
                var info=Harmony.GetPatchInfo(target);
                if(info==null||info.Prefixes.Count(p=>p.owner==Id)!=1||info.Finalizers.Count(p=>p.owner==Id)!=1||info.Postfixes.Any(p=>p.owner==Id)||info.Transpilers.Any(p=>p.owner==Id)||info.ILManipulators.Any(p=>p.owner==Id))
                    throw new InvalidOperationException("Hook_Mob_preUpdate patch not installed exactly once");
                var after=GuardViolation(true);
                if(after!=null)throw new InvalidOperationException("guard after install: "+after);
                _startedAt=Stopwatch.GetTimestamp();
                if(_stats)_timer=new Timer(_=>Report(),null,5000,5000);
                _installed=true;
                OptCore.Info("OptClient: client steady-state path on MobsSynchronization.Hook_Mob_preUpdate (prefix Priority.Last + finalizer); shadow={Shadow}; stats={Stats}; prefixes on the hook: {Owners}",
                    all?"all":"first 4096, 64 after each level change, then 1/256",_stats?"5 s":"off",Owners(target));
            }
            catch(Exception e)
            {
                // First, before the rollback (review finding 4): if Unpatch fails too, the prefix left behind forwards to
                // a switched-off path and the original body serves every call.
                _path?.SwitchOff("install failed: "+e.GetBaseException().Message);
                _timer?.Dispose();_timer=null;
                if(target!=null)try{harmony.Unpatch(target,HarmonyPatchType.All,Id);}catch(Exception r){OptCore.Warn("OptClient rollback failed (the patch stays inert): {Error}",r.Message);}
                throw;
            }
        }
    }
    static MethodInfo Own(string name)=>typeof(OptClient).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic)??throw new MissingMethodException("OptClient",name);
    static string Owners(MethodBase m){var i=Harmony.GetPatchInfo(m);return i==null?"":string.Join(",",i.Prefixes.Select(p=>p.owner+"/"+p.priority));}

    // ---- the patch: one-line forwarders (the logic is OptClientPath, run by the managed checks) ----
    // Positional __0/__1 (parameter TYPES are checked at install, F7; __0 is the orig_preUpdate delegate, taken as object).
    static bool BeforePreUpdate(ref object __0,Mob __1,bool __runOriginal)
    {
        var p=_path;
        return p==null?__runOriginal:p.Before(ref __0,__1,__runOriginal);
    }
    // void + __exception: HarmonyX rethrows the exception unchanged, stack trace kept (checked by bind-check.ps1).
    static void AfterPreUpdate(object __0,Exception __exception)
    {
        _path?.After(__0);
    }

    // IL body SHA-256 of every method the fast path re-tests or calls (System.Reflection.Metadata dump of the pinned
    // DLL, 378BE6B0...0D28; lengths equal the release IL dump headers). The DLL hash pins them already; these make a
    // mis-resolved overload fail closed and document what was checked.
    static void Pin(MethodInfo m,int length,string sha)
    {
        var il=m.GetMethodBody()?.GetILAsByteArray();
        if(il==null||il.Length!=length||Convert.ToHexString(SHA256.HashData(il))!=sha)throw new InvalidOperationException("Pinned body changed: "+m.DeclaringType?.Name+"."+m.Name);
    }
    static void PinFile(Assembly a,string pin)
    {
        if(string.IsNullOrEmpty(a.Location)||global::CoopPackRuntime.PackPins.Sha256(a.Location)!=pin)throw new InvalidOperationException("pinned file changed: "+a.GetName().Name);
    }

    static void Bind()
    {
        // framework files the path relies on (review finding 1: the probe depends on this HarmonyX build)
        PinFile(typeof(Harmony).Assembly,HarmonyPin);
        PinFile(typeof(ModCore.Mods.ModBase).Assembly,ModCorePin);
        PinFile(typeof(Hashlink.Marshaling.HashlinkMarshal).Assembly,HashlinkSharpPin);
        PinFile(typeof(HaxeProxy.Runtime.HaxeProxyBase).Assembly,HaxeProxyPin);
        if(typeof(Config<MultiplayerSettingsData>).Assembly!=typeof(ModCore.Mods.ModBase).Assembly)throw new InvalidOperationException("ModCore Config<T> moved");
        var ms=OptCore.ModType(MobsType);
        if(ms.BaseType!=typeof(object))throw new InvalidOperationException("MobsSynchronization is not a plain managed class");
        _origType=typeof(Mob).Assembly.GetType("dc.en.Hook_Mob+orig_preUpdate",false)??throw new InvalidOperationException("dc.en.Hook_Mob.orig_preUpdate not found");
        var origInvoke=_origType.GetMethod("Invoke",BindingFlags.Instance|BindingFlags.Public|BindingFlags.DeclaredOnly);
        if(!typeof(MulticastDelegate).IsAssignableFrom(_origType)||origInvoke==null||origInvoke.ReturnType!=typeof(void)||_origType.GetConstructor(new[]{typeof(object),typeof(IntPtr)})==null||
            !origInvoke.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(Mob)}))
            throw new InvalidOperationException("Pinned orig_preUpdate signature changed");
        _hook=OptCore.Method(ms,"Hook_Mob_preUpdate",false,typeof(void),_origType,typeof(Mob));
        MethodInfo M(string name,Type ret,params Type[] args)=>OptCore.Method(ms,name,true,ret,args);
        var isSync=M("IsSyncMob",typeof(bool),typeof(Mob));
        var byType=M("IsSyncMobByType",typeof(bool),typeof(Mob));
        var hostile=M("IsMobHostileToPlayers",typeof(bool),typeof(Mob));
        var ensure=M("EnsureMobTracked",typeof(void),typeof(Mob));
        var ready=M("IsLevelIdentityReadyLocked",typeof(bool),typeof(Level));
        var match=M("DoesLevelMatchCurrentIdentityLocked",typeof(bool),typeof(Level));
        var find=M("FindTrackedMobIndexLocked",typeof(int),typeof(Mob));
        var update=M("UpdateClientMobAiAuthority",typeof(void),typeof(Mob));
        var refresh=M("RefreshClientNetworkAttackState",typeof(void),typeof(Mob));
        var active=M("IsClientNetworkAttackActive",typeof(bool),typeof(Mob));
        var tryLock=M("TryLockClientMobAiAuthority",typeof(void),typeof(Mob));
        var skill=M("HasLocalQueuedOrChargingSkill",typeof(bool),typeof(Mob));
        var isHost=M("IsHost",typeof(bool),typeof(NetNode));
        var isClient=M("IsClient",typeof(bool),typeof(NetNode));
        var byTypeLambda=OptCore.Method(ms.GetNestedType("<>c",BindingFlags.NonPublic)??throw new InvalidOperationException("MobsSynchronization.<>c not found"),"<IsSyncMobByType>b__656_0",false,typeof(bool),typeof(Type));
        var settings=typeof(MultiplayerSettingsStorage);
        var getEnable=OptCore.Method(settings,"get_EnableMobsSync",true,typeof(bool));
        var normalize=OptCore.Method(settings,"EnsureDataNormalizedUnsafe",true,typeof(MultiplayerSettingsData));
        var clamp=OptCore.Method(settings,"Clamp",true,typeof(double),typeof(double),typeof(double),typeof(double));
        var approx=OptCore.Method(settings,"Approximately",true,typeof(bool),typeof(double),typeof(double));
        var perk=OptCore.Method(settings,"NormalizePerkId",true,typeof(string),typeof(string));
        var save=OptCore.Method(settings,"SaveUnsafe",true,typeof(void));
        var data=typeof(MultiplayerSettingsData);
        MethodInfo D(string name,Type ret,params Type[] args)=>OptCore.Method(data,name,false,ret,args);
        var config=typeof(Config<MultiplayerSettingsData>);
        MethodInfo Cf(string name,Type ret,params Type[] args)=>OptCore.Method(config,name,false,ret,args);
        var entity=typeof(dc.Entity);
        var getTeam=OptCore.Method(entity,"get__team",false,null);
        var lobby=OptCore.ModType("DeadCellsMultiplayerMod.LobbySession");
        var netRef=OptCore.Method(lobby,"get_NetRef",true,typeof(NetNode));
        var cine=OptCore.Method(typeof(ModEntry),"IsLocalBossIntroCineActive",true,typeof(bool));
        var boss=OptCore.Method(OptCore.ModType("DeadCellsMultiplayerMod.Mobs.Bosses.BossSyncHelpers"),"IsBossMob",true,typeof(bool),typeof(Mob));

        Pin(_hook,165,"106DBF9967576B728863CF3BE42C89909E3A42A3DBED481BE386D234DB614173");
        Pin(isSync,63,"44E9CFA7DFECC432A5ED21E780C8FBF561F1E699FE68F4005B860082426B7CFD");
        Pin(byType,48,"4FE11774F507F3F07123AF688573FDEB3822E7E73DC4DC1C6FF254DB5468C105");
        Pin(hostile,49,"4271A32C899B5E37A2C76EB7C24E1CF965EEBD02A09C0087CFC0EAA7E4E0A42B");
        Pin(ensure,96,"FF0F6A3A71F2EC04CB009C258BB02C4B3D43EE3585996BEB0F5502EFDB243B2A");
        Pin(ready,7,"398CC2CDDC59CA2EC630A763CC8E6AA1A7B32E70CC45FBC64726845BA10C1D7B");
        Pin(match,105,"B4FE24495AA464AD47FFAED2DDEE170288CCD157386272006D0A3A77041F36B0");
        Pin(find,265,"90B51152C2CF68334E2805E6C5CEDBF7212249AD538E54AC2A30C4DC122A16D2");
        Pin(update,72,"034CAAFA87BF09D4667D51700841D9B4AECD6833C1D751A93471C70A0ABE8A4D");
        Pin(refresh,293,"8BB28E9949C217B185A144120C53F1C6674D0045AB39FE83D21F325E97B4EDC2");
        Pin(active,47,"2316EF386154A89BA2122815049338A1F4DC85955FD0E05BE9933DF2A6AD0BF9");
        Pin(tryLock,69,"6A6DBFAE3EC27137893C44101CF23C721C28240E0082ABA48AC7BC16B3C1F6D7");
        Pin(skill,76,"8C53CC715B6A2457CBEBE63135EA4EF1507689E5702914B2BB828E576D02AB46");
        Pin(isHost,20,"7B3A1D50BBFE9BB7A5C5BD21B2A63C574AB7D699B7C56C43E2DD3AC3C4EBEF4D");
        Pin(isClient,23,"82DF264B773CB1F5B32D62D163CF28838FA6BB536495F168310E544575646054");
        Pin(getEnable,41,"D7FF8307839B0D6221F9D2251C4B43931867597691E442AC46E61A574585B88E");
        Pin(normalize,259,"2F6D64693E957DB8F92D2FAB2FFB76B4F61C3D000B4384606E858184CDE11F5F");
        Pin(netRef,6,"6DA5F23A4D13988DE21B0F637DD992C0DF2805337998BF11708590761A8CC2A7");
        Pin(cine,54,"8C05FB1920557CB56DB8B15119AB6028AA732FC46A2838832B7E912D8C4A5709");
        Pin(boss,88,"872788C212B308E8B0962721F77D4AAC9824FD4D716BE930D4D474C77D943533");

        // called through the mod's own methods (patches on them stay in effect, e.g. CoopPerfFix's IsBossMob memo)
        _netRef=OptCore.Delegate<Func<NetNode>>(netRef);
        _isHost=OptCore.Delegate<Func<NetNode,bool>>(isHost);
        _isClient=OptCore.Delegate<Func<NetNode,bool>>(isClient);
        _byType=OptCore.Delegate<Func<Mob,bool>>(byType);
        _isBoss=OptCore.Delegate<Func<Mob,bool>>(boss);
        _skill=OptCore.Delegate<Func<Mob,bool>>(skill);
        _cine=OptCore.Delegate<Func<bool>>(cine);

        // statics written by RebuildMobArray / ResetMobTrackingStateLocked / FindTrackedMobIndexLocked: read live
        _current=StaticGetter<Level>(OptCore.Field(ms,"currentLevel",typeof(Level),true));
        _ready=StaticGetter<bool>(OptCore.Field(ms,"s_levelIdentityReady",typeof(bool),true));
        _token=StaticGetter<int>(OptCore.Field(ms,"s_levelIdentityToken",typeof(int),true));
        _pending=StaticGetter<bool>(OptCore.Field(ms,"s_trackedMobValidationPending",typeof(bool),true));
        // static readonly: captured once at the first call
        _fSync=ReadOnly(ms,"Sync",typeof(object));
        _fTracked=ReadOnly(ms,"trackedMobs",typeof(List<Mob>));
        _fIndices=ReadOnly(ms,"trackedMobIndices",typeof(Dictionary<Mob,int>));
        _fLocked=ReadOnly(ms,"clientAiLockedMobs",typeof(HashSet<Mob>));
        _fActive=ReadOnly(ms,"clientActiveNetworkAttackMobs",typeof(HashSet<Mob>));
        _fStart=ReadOnly(ms,"clientNetworkAttackStartFrame",typeof(Dictionary<Mob,double>));
        _fLease=ReadOnly(ms,"clientBossSkillCallbackLeaseMobs",typeof(HashSet<Mob>));
        _fMobToId=ReadOnly(ms,"MobToId",typeof(Dictionary<Mob,int>));
        _fSettingsRoot=ReadOnly(settings,"SyncRoot",typeof(object));
        _fConfig=ReadOnly(settings,"Config",typeof(Config<MultiplayerSettingsData>));
        // the snapshot's _version getters must exist on this runtime (fail closed)
        OptClientRules.VersionGetter<List<Mob>>();OptClientRules.VersionGetter<Dictionary<Mob,int>>();
        OptClientRules.VersionGetter<HashSet<Mob>>();OptClientRules.VersionGetter<Dictionary<Mob,double>>();

        // Every method whose work the fast path skips on the S path, and what those methods call there (review finding 2:
        // a patch on a normalisation helper or on Config<T> would be bypassed as well). Methods only reached outside S
        // (AddTrackedMobLocked, QueueInitialMobSync, the identity slow path, TryUnlock / TryRepair, the setters' callers)
        // are not skipped work: when they would run, the fast path declines.
        const OptClientRules.GuardTarget Mir=OptClientRules.GuardTarget.Mirrored;
        _guarded=new[]
        {
            (_hook,OptClientRules.GuardTarget.Hook),
            (match,OptClientRules.GuardTarget.LevelMatch),
            // MobsSynchronization
            (isSync,Mir),(hostile,Mir),(byType,Mir),(byTypeLambda,Mir),(ensure,Mir),(ready,Mir),(find,Mir),
            (update,Mir),(refresh,Mir),(active,Mir),(tryLock,Mir),
            // settings: get_EnableMobsSync -> EnsureDataNormalizedUnsafe and everything it calls (IL 62820-62906)
            (getEnable,Mir),(normalize,Mir),(clamp,Mir),(approx,Mir),(perk,Mir),(save,Mir),
            (D("get_EnableMobsSync",typeof(bool)),Mir),
            (D("get_MobsInterpolationQuality",typeof(double)),Mir),(D("set_MobsInterpolationQuality",typeof(void),typeof(double)),Mir),
            (D("get_MobsHpMultiplier",typeof(double)),Mir),(D("set_MobsHpMultiplier",typeof(void),typeof(double)),Mir),
            (D("get_BossesHpMultiplier",typeof(double)),Mir),(D("set_BossesHpMultiplier",typeof(void),typeof(double)),Mir),
            (D("get_DebugStartPerkId",typeof(string)),Mir),(D("set_DebugStartPerkId",typeof(void),typeof(string)),Mir),
            (Cf("get_Value",data),Mir),(Cf("set_Value",typeof(void),data),Mir),(Cf("Save",typeof(void)),Mir),
            // proxy getters the skipped calls read (IsSyncMob, IsMobHostileToPlayers, EnsureMobTracked): query only
            (OptCore.Method(entity,"get_destroyed",false,typeof(bool)),Mir),(OptCore.Method(entity,"get__level",false,typeof(Level)),Mir),
            (getTeam,Mir),(OptCore.Method(typeof(Level),"get_teamMob",false,getTeam.ReturnType),Mir),
        };
    }
    static FieldInfo ReadOnly(Type t,string name,Type type)
    {
        var f=OptCore.Field(t,name,type,true);
        if(!f.IsInitOnly)throw new InvalidOperationException("Pinned field is not readonly: "+t.Name+"."+name);
        return f;
    }
    static Func<T> StaticGetter<T>(FieldInfo f)
    {
        var dm=new DynamicMethod("OptClient_get_"+f.Name,typeof(T),Type.EmptyTypes,typeof(OptClient).Module,true);
        var il=dm.GetILGenerator();il.Emit(OpCodes.Ldsfld,f);il.Emit(OpCodes.Ret);
        return (Func<T>)dm.CreateDelegate(typeof(Func<T>));
    }

    // Game thread, first prefix call: the static readonly instances (never reassigned after the type initializer).
    static OptClientState<Mob> Capture()
    {
        var state=new OptClientState<Mob>(_fSync.GetValue(null),(List<Mob>)_fTracked.GetValue(null),(Dictionary<Mob,int>)_fIndices.GetValue(null),
            (HashSet<Mob>)_fLocked.GetValue(null),(HashSet<Mob>)_fActive.GetValue(null),(Dictionary<Mob,double>)_fStart.GetValue(null),
            (HashSet<Mob>)_fLease.GetValue(null),(Dictionary<Mob,int>)_fMobToId.GetValue(null));
        var root=_fSettingsRoot.GetValue(null)??throw new InvalidOperationException("settings lock missing");
        var config=(Config<MultiplayerSettingsData>)_fConfig.GetValue(null)??throw new InvalidOperationException("settings config missing");
        _settingsRoot=root;_config=config;
        return state;
    }

    // ---- guard (skeptic F3) ----
    // The one foreign patch accepted: CoopSyncFixes' MatchTrackedNativeLevel prefix, pinned by its IL (pinned CSF build
    // 62842b60..., CoopSyncFixes.dll FFF00CA3...; Branches.cs:48-53: pointer compare when ready and both levels non-null,
    // no write). Any other body under that name counts as foreign.
    const int CsfLevelPrefixIl=74;
    const string CsfLevelPrefixSha="1FFCFAEC28AE4A113C1E03377B6920CF085BF767AE61AC17D0CDD605E428DADD";
    static string PatchName(Patch p)
    {
        var m=p.PatchMethod;if(m==null)return null;
        if(p.owner!=OptClientRules.CsfOwner||m.Name!=OptClientRules.CsfLevelPrefix)return m.Name;
        try
        {
            var il=m.GetMethodBody()?.GetILAsByteArray();
            if(il!=null&&il.Length==CsfLevelPrefixIl&&m.Module.Assembly==typeof(OptClient).Assembly&&m.DeclaringType!=null&&m.DeclaringType.FullName=="CoopSyncFixes")return m.Name;
        }
        catch{}
        return m.Name+"(unpinned body)";
    }
    static string GuardViolation(bool expectOwn)
    {
        var own=typeof(OptClient).Assembly;
        foreach(var (m,kind) in _guarded)
        {
            var info=Harmony.GetPatchInfo(m);
            var facts=new List<OptClientRules.PatchFact>();
            if(info!=null)
            {
                void Add(IEnumerable<Patch> ps,OptClientRules.PatchKind k)
                {
                    foreach(var p in ps)facts.Add(new OptClientRules.PatchFact(k,p.owner,PatchName(p),p.PatchMethod?.DeclaringType?.Name,p.PatchMethod?.DeclaringType?.Assembly==own,p.priority));
                }
                Add(info.Prefixes,OptClientRules.PatchKind.Prefix);Add(info.Postfixes,OptClientRules.PatchKind.Postfix);
                Add(info.Transpilers,OptClientRules.PatchKind.Transpiler);Add(info.Finalizers,OptClientRules.PatchKind.Finalizer);
                Add(info.ILManipulators,OptClientRules.PatchKind.ILManipulator);
            }
            var bad=OptClientRules.GuardViolation(kind,m.DeclaringType?.Name+"."+m.Name,facts,expectOwn);
            if(bad!=null)return bad;
        }
        return null;
    }
    static string AllowedForeign()
    {
        var list=new List<string>();
        foreach(var (m,_) in _guarded)
        {
            var info=Harmony.GetPatchInfo(m);if(info==null)continue;
            foreach(var p in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Concat(info.ILManipulators))
                if(p.owner!=Id)list.Add(m.Name+"<-"+p.owner+"."+p.PatchMethod?.Name);
        }
        return list.Count==0?"none":string.Join(", ",list);
    }

    // ---- the game side of OptClientRules.IEnv: the reads the decision makes (the mod's own methods through delegates,
    // the proxy getters the release body calls) and the plumbing. A struct: every call is resolved at JIT time.
    struct Glue:OptClientRules.IEnv<Mob,Level>
    {
        public void Role(out bool host,out bool client){var net=_netRef();host=_isHost(net);client=_isClient(net);}
        public bool MobsSyncEnabled()
        {
            bool taken=false;
            try{Monitor.Enter(_settingsRoot,ref taken);var data=_config.Value;return data!=null&&data.EnableMobsSync;}
            finally{if(taken)Monitor.Exit(_settingsRoot);}
        }
        public bool Destroyed(Mob m)=>m.destroyed;
        public Level Level(Mob m)=>m._level;
        public bool SyncByType(Mob m)=>_byType(m);
        public bool BossIntroCineActive()=>_cine();
        public bool IsBoss(Mob m)=>_isBoss(m);
        public bool QueuedOrChargingSkill(Mob m)=>_skill(m);
        public bool IdentityReady()=>_ready();
        public int IdentityToken()=>_token();
        public Level CurrentLevel()=>_current();
        public bool Pending()=>_pending();
        public object Target(Mob m)=>m.aTarget;
        public OptClientState<Mob> Capture()=>OptClient.Capture();
        public string GuardViolation()=>OptClient.GuardViolation(true);
        public string AllowedForeign()=>OptClient.AllowedForeign();
        public bool Fault(Exception e)=>Gate.Fail(e);
        public void Ok()=>Gate.Ok();
        public void Info(string line)=>OptCore.Info("{Line:l}",line);
        public void Warn(string line)=>OptCore.Warn("{Line:l}",line);
    }

    // ---- stats (COOP_OPT_STATS=1): timer thread, reads counters only ----
    static long[] _lastC,_lastR;static long _lastAt;
    static void Report()
    {
        try
        {
            var p=_path;if(p==null)return;
            long now=Stopwatch.GetTimestamp();
            var c=new long[p.C.Length];for(int i=0;i<c.Length;i++)c[i]=Volatile.Read(ref p.C[i]);
            var r=new long[p.Rs.Length];for(int i=0;i<r.Length;i++)r[i]=Volatile.Read(ref p.Rs[i]);
            double seconds=(now-(_lastAt==0?_startedAt:_lastAt))/(double)Stopwatch.Frequency;
            OptCore.Info("{Line:l}",OptClientRules.StatsLine(seconds,c,_lastC,r,_lastR,Stopwatch.Frequency,!p.Off,p.OffReason));
            _lastC=c;_lastR=r;_lastAt=now;
        }
        catch{}
    }
}
