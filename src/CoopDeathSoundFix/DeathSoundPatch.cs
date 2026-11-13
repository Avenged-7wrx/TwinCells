using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using dc.en;
using dc.en.mob;
using dc.tool.atk;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using File=System.IO.File;
using Path=System.IO.Path;

// CoopDeathSoundFix (Alpha2 unit U7, PLAN-ALPHA2.md issue 1): the client hears the enemy death sound for deaths the host
// confirms over the network, and never twice for its own killing blow.
//
// Root cause (verified): the generic death sound enm_zmb_die is played only by Mob.playDamageSounds (f@1075) when it runs
// with life <= 0, and its only caller is the end of Mob.onDamage (f@1072 op1302). The client never runs a native lethal hit
// to completion: the base mod (0.5.1, release DLL 378BE6B0) applies every host-confirmed death as "mob.life=0; mob.onDie()"
// inside MobsSynchronization.RunWithAuthoritativeClientMobDie (six callers in the release IL), so onDamage - and the
// sound - never runs. Only the client's own lethal hit plays it (inside its local onDamage, before the base mod suppresses
// that onDie and later applies the host's confirmation).
//
// The part (client only in effect; installed on both roles, inert on the host):
// 1. An observe-only DCCM hook on Mob.playDamageSounds (f@1075) and on its single override AxeStatue.playDamageSounds
//    (f@19523): whenever it runs with life <= 0, remember (native pointer, base level identity token, time).
// 2. A Harmony prefix + finalizer on the base mod's private RunWithAuthoritativeClientMobDie(Mob, Action) that push and pop
//    the mob on a [ThreadStatic] scope stack.
// 3. A Harmony prefix (Priority.Last, with __runOriginal; never skips) on the base mod's private Hook_Mob_onDie(orig, self)
//    that calls the native self.playDamageSounds(null) - before the native onDie, exactly where vanilla plays it - only
//    when DeathSoundRules.Decide says Play: the original body will run (no earlier prefix skipped it), self is the mob of
//    the innermost authoritative death on this thread, the peer is a client, the base authoritative depth is > 0, the mob
//    is not destroyed, onDieDone is not set, life <= 0, it is not a boss, not culled, CoopSyncFixes' SnapshotApplyDepth is
//    0 (read by reflection), no die sound ran for it within 2 s, no CoopShrineDeathFix retirement receipt names it, and the
//    base death ledger does not already mark it dead (the base hook would then return before the native onDie).
//    A deferred death that completes once the mob is awake in the client's room is audible (the client sees it die).
// Every patch body runs in try/catch behind a fault gate (8 consecutive / 64 total faults switch the part off for the
// session); the kill switch COOP_DEATH_SOUND_DISABLE=1 installs nothing. Logs are rate-limited (DeathSoundRules.LogLimiter).
// Pins: the base mod DLL, hlboot, HarmonyX and HaxeProxy files; the IL (length + SHA-256) of every base-mod method patched
// or called; the GameProxy members symbolically (DeathSoundProxyPin).
public static class DeathSoundPatch
{
    public const string Owner="CoopDeathSoundFix.NetworkDeathSound.20261112";
    public const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string NativePin="A37869A3C91443B50322B2CDF29595C9644AB8FC1280342F1F17C6F616A15534";
    public const string HarmonyPin="70E7647876B6C3D874EA99B56627A95D1DCE2E1DD791981A002578A2FEBC9BBD";
    public const string HaxeProxyPin="0DF9697335FFDBA4461E599CEE063BC41E4B76C881CCAEDB08AE16DC4A27A8B2";
    const string MobsType="DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization";
    const string BossType="DeadCellsMultiplayerMod.Mobs.Bosses.BossSyncHelpers";
    const string SessionType="DeadCellsMultiplayerMod.LobbySession";
    const string LedgerType="DeadCellsMultiplayerMod.PortableCore.MobLifecycleLedger";
    // Release IL of every base-mod method this part patches or calls (base 0.5.1, 378BE6B0; the release IL dump headers
    // IL:139129 RunWithAuthoritativeClientMobDie il=108, IL:134416 Hook_Mob_onDie il=634).
    public static readonly (string Type,string Name,int Length,string Sha)[] IlPins=
    {
        (MobsType,"RunWithAuthoritativeClientMobDie",108,"53D81C1E0736929F53FA15427855E1F2B59DDA3B9802C6C261DED1BE171D84C1"),
        (MobsType,"Hook_Mob_onDie",634,"424F3570E2A1E92BFC9425E64121B57AE90FA9B7A7FD0A4EDF35C101B30EED74"),
        (MobsType,"TryGetMobSyncId",436,"B41368DEA8483DEF3493266B343E052C56A4FA02A6529B6ECD0E3B9B2024E85C"),
        (MobsType,"TryGetCurrentLevelIdentityToken",37,"D54D6F4FAEDDAF2D5CB51930A7C3643B124796545C220625EC3B5F804EEF41F0"),
        (MobsType,"IsClient",23,"82DF264B773CB1F5B32D62D163CF28838FA6BB536495F168310E544575646054"),
        (BossType,"IsBossMob",88,"872788C212B308E8B0962721F77D4AAC9824FD4D716BE930D4D474C77D943533"),
        (SessionType,"get_NetRef",6,"6DA5F23A4D13988DE21B0F637DD992C0DF2805337998BF11708590761A8CC2A7"),
        (LedgerType,"IsDead",33,"87013FBE29336960DCDC699FEF0D80012C40F5FF0A4ADFCC9B24FD87D3B53B71"),
    };
    const BindingFlags S=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.DeclaredOnly;
    const BindingFlags I=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly;

    static readonly object Gate=new();        // install transaction
    static readonly object StateGate=new();   // fault gate and log limiter (game thread in practice)
    static volatile bool Published,Tripped,Patched;
    public static bool ReadyToRun=>Published;
    public static bool Disabled{get;private set;}
    public static bool SwitchedOffAfterFaults=>Tripped;
    public static string InitializationError{get;private set;}
    public static bool SnapshotFenceBound{get;private set;}
    public static bool ShrineReceiptsBound{get;private set;}
    public static string ProxyFingerprint{get;private set;}

    // ---- statistics (read by the unit probe; never by the part's own decision) ----
    static readonly long[] Verdicts=new long[DeathSoundRules.VerdictCount];
    static long observed,played,playSkippedNoAudio,faults;
    public static long ObservedDieSounds=>Interlocked.Read(ref observed);
    public static long PlayedSounds=>Interlocked.Read(ref played);
    public static long PlaySkippedNoAudio=>Interlocked.Read(ref playSkippedNoAudio);
    public static long Faults=>Interlocked.Read(ref faults);
    public static long VerdictTotal(int verdict)=>verdict>=0&&verdict<Verdicts.Length?Interlocked.Read(ref Verdicts[verdict]):0;
    public static string Stats()=>string.Join(" ",Enum.GetValues(typeof(DeathSoundRules.Verdict)).Cast<DeathSoundRules.Verdict>()
        .Where(DeathSoundRules.Counted).Select(v=>v+"="+VerdictTotal((int)v)))+" observed="+ObservedDieSounds+" noAudio="+PlaySkippedNoAudio+" faults="+Faults;

    // ---- bound members ----
    static MethodInfo RunWithAuthority,DeathHook;
    delegate bool SyncIdReader(Mob mob,out int syncId);
    delegate bool TokenReader(out int token);
    static SyncIdReader TryGetSyncId;
    static TokenReader TryGetToken;
    static Func<NetNode> NetRef;
    static Func<NetNode,bool> IsClientNet;
    static Func<Mob,bool> IsBoss;
    static Func<int> ReadAuthoritativeDepth;
    static Func<object> ReadSync;
    static Func<int,int,bool> LedgerIsDead;
    static Func<int> ReadSnapshotDepth;          // null: CoopSyncFixes absent (no snapshot path exists)
    static FieldInfo ShrineReceipts,ShrineReplicaPointer;   // null: CoopShrineDeathFix absent (no retirement path exists)
    static Harmony HarmonyInstance;

    static readonly DeathSoundRules.RecentSounds Recent=new DeathSoundRules.RecentSounds();
    static readonly object RecentGate=new();
    static readonly DeathSoundRules.FaultGate FaultState=new DeathSoundRules.FaultGate();
    static readonly DeathSoundRules.LogLimiter Logs=new DeathSoundRules.LogLimiter();
    [ThreadStatic] static DeathSoundRules.ScopeStack Scopes;

    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);
    static double Now()=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;

    public static void Install()
    {
        lock(Gate)
        {
            if(Published)return;
            if(InitializationError!=null)throw new InvalidOperationException("Earlier death sound initialization failed: "+InitializationError);
            try
            {
                Disabled=DeathSoundRules.SwitchOn(Environment.GetEnvironmentVariable(DeathSoundRules.KillSwitch));
                if(!Disabled)
                {
                    Bind();
                    PatchManaged();
                    HookNative();
                }
                Published=true;
            }
            catch(Exception error)
            {
                Published=false;InitializationError=error.GetBaseException().Message;
                try{UnhookNative();}catch(Exception rollback){InitializationError+="; native rollback: "+rollback.Message;}
                try{UnpatchManaged();}catch(Exception rollback){InitializationError+="; rollback: "+rollback.Message;}
                throw;
            }
        }
    }

    // Reflection and pins only (no patch, no hook, no game state): also run offline by Test-Offline.ps1.
    public static void Bind()
    {
        var baseAssembly=typeof(ModEntry).Assembly;
        if(baseAssembly.GetName().Version!=new Version(0,5,1,0)||Hash(baseAssembly.Location)!=BasePin)
            throw new InvalidOperationException("CoopDeathSoundFix requires the exact community 0.5.1 release (378BE6B0)");
        if(Hash(Path.Combine(Path.GetDirectoryName(typeof(Mob).Assembly.Location),"hlboot.dat"))!=NativePin)
            throw new InvalidOperationException("CoopDeathSoundFix requires the exact native 35.13.5 game (hlboot A37869A3)");
        if(Hash(typeof(Harmony).Assembly.Location)!=HarmonyPin)throw new InvalidOperationException("Pinned HarmonyX 2.16.1 file changed");
        if(Hash(typeof(HaxeProxy.Runtime.HaxeProxyBase).Assembly.Location)!=HaxeProxyPin)throw new InvalidOperationException("Pinned HaxeProxy file changed");
        foreach(var pin in IlPins)
        {
            var type=baseAssembly.GetType(pin.Type,true);
            var candidates=type.GetMethods(S|I).Where(m=>m.Name==pin.Name).ToArray();
            if(candidates.Length!=1)throw new InvalidOperationException("Pinned base method not unique: "+pin.Name);
            var il=candidates[0].GetMethodBody()?.GetILAsByteArray();
            if(il==null||il.Length!=pin.Length||Convert.ToHexString(SHA256.HashData(il))!=pin.Sha)
                throw new InvalidOperationException("Pinned release IL changed: "+type.Name+"."+pin.Name);
        }
        var mobs=baseAssembly.GetType(MobsType,true);
        RunWithAuthority=Method(mobs,"RunWithAuthoritativeClientMobDie",typeof(void),typeof(Mob),typeof(Action));
        Parameters(RunWithAuthority,"mob","action");
        DeathHook=mobs.GetMethod("Hook_Mob_onDie",S);
        if(DeathHook==null||DeathHook.ReturnType!=typeof(void))throw new InvalidOperationException("Pinned death hook signature changed");
        var hookParams=DeathHook.GetParameters();
        if(hookParams.Length!=2||hookParams[0].ParameterType.FullName!="dc.en.Hook_Mob+orig_onDie"||hookParams[1].ParameterType!=typeof(Mob))
            throw new InvalidOperationException("Pinned death hook signature changed");
        Parameters(DeathHook,"orig","self");
        TryGetSyncId=(SyncIdReader)Delegate.CreateDelegate(typeof(SyncIdReader),Method(mobs,"TryGetMobSyncId",typeof(bool),typeof(Mob),typeof(int).MakeByRefType()));
        TryGetToken=(TokenReader)Delegate.CreateDelegate(typeof(TokenReader),Method(mobs,"TryGetCurrentLevelIdentityToken",typeof(bool),typeof(int).MakeByRefType()));
        IsClientNet=(Func<NetNode,bool>)Delegate.CreateDelegate(typeof(Func<NetNode,bool>),Method(mobs,"IsClient",typeof(bool),typeof(NetNode)));
        IsBoss=(Func<Mob,bool>)Delegate.CreateDelegate(typeof(Func<Mob,bool>),Method(baseAssembly.GetType(BossType,true),"IsBossMob",typeof(bool),typeof(Mob)));
        NetRef=(Func<NetNode>)Delegate.CreateDelegate(typeof(Func<NetNode>),Method(baseAssembly.GetType(SessionType,true),"get_NetRef",typeof(NetNode)));
        ReadAuthoritativeDepth=StaticReader<int>(StaticField(mobs,"authoritativeClientMobDieDepth",typeof(int),false));
        ReadSync=StaticReader<object>(StaticField(mobs,"Sync",typeof(object),true));
        var ledgerField=StaticField(mobs,"s_clientMobLifecycle",baseAssembly.GetType(LedgerType,true),true);
        var isDead=ledgerField.FieldType.GetMethod("IsDead",I,null,new[]{typeof(int),typeof(int)},null);
        if(isDead?.ReturnType!=typeof(bool))throw new InvalidOperationException("Pinned death ledger ABI changed");
        LedgerIsDead=LedgerReader(ledgerField,isDead);
        // Companions in the same installation (CoopPack parts, or the separate legacy modules without the pack).
        var csf=Companion("CoopSyncFixes","CoopSyncFixes");
        ReadSnapshotDepth=null;SnapshotFenceBound=false;
        if(csf!=null)
        {
            var depth=csf.GetField("SnapshotApplyDepth",S);
            if(depth==null||depth.FieldType!=typeof(int)||!depth.IsDefined(typeof(ThreadStaticAttribute),false))
                throw new InvalidOperationException("CoopSyncFixes snapshot fence field changed (SnapshotApplyDepth)");
            ReadSnapshotDepth=StaticReader<int>(depth);SnapshotFenceBound=true;
        }
        var shrine=Companion("CoopShrineDeathFix","ShrineLateDeathPatch");
        ShrineReceipts=ShrineReplicaPointer=null;ShrineReceiptsBound=false;
        if(shrine!=null)
        {
            var receipt=shrine.GetNestedType("Receipt",BindingFlags.NonPublic|BindingFlags.Public)??throw new InvalidOperationException("CoopShrineDeathFix receipt type changed");
            var list=shrine.GetField("Receipts",S);
            if(list==null||!list.IsInitOnly||list.FieldType!=typeof(List<>).MakeGenericType(receipt))throw new InvalidOperationException("CoopShrineDeathFix receipt list changed");
            var replica=receipt.GetField("ReplicaPointer",I);
            if(replica==null||replica.FieldType!=typeof(IntPtr))throw new InvalidOperationException("CoopShrineDeathFix receipt replica pointer changed");
            ShrineReceipts=list;ShrineReplicaPointer=replica;ShrineReceiptsBound=true;
        }
        // GameProxy members: symbolic pin (never the raw file).
        var fingerprint=DeathSoundProxyPin.Compute(typeof(Mob).Assembly,out _);
        if(fingerprint!=DeathSoundProxyPin.FingerprintPin)throw new InvalidOperationException("Unsupported GameProxy members (symbolic pin "+fingerprint+")");
        ProxyFingerprint=fingerprint;
    }

    static MethodInfo Method(Type type,string name,Type result,params Type[] args)
    {
        var m=type.GetMethod(name,S,null,args,null);
        if(m==null||m.ReturnType!=result)throw new InvalidOperationException("Pinned method signature changed: "+type.Name+"."+name);
        return m;
    }
    static void Parameters(MethodInfo m,params string[] names)
    {
        var p=m.GetParameters();
        if(p.Length!=names.Length||!p.Select(x=>x.Name).SequenceEqual(names))throw new InvalidOperationException("Pinned parameter names changed: "+m.Name);
    }
    static FieldInfo StaticField(Type type,string name,Type fieldType,bool readOnly)
    {
        var f=type.GetField(name,S);
        if(f==null||f.FieldType!=fieldType||f.IsInitOnly!=readOnly)throw new InvalidOperationException("Pinned field changed: "+type.Name+"."+name);
        return f;
    }
    // A static field read compiled once (works for [ThreadStatic] fields: the reading thread's value).
    static Func<T> StaticReader<T>(FieldInfo f)
    {
        if(!f.IsStatic||f.FieldType!=typeof(T))throw new InvalidOperationException("Static field type changed: "+f.Name);
        var dm=new DynamicMethod("DeathSound_Read_"+f.Name,typeof(T),Type.EmptyTypes,typeof(DeathSoundPatch).Module,true);
        var il=dm.GetILGenerator();il.Emit(OpCodes.Ldsfld,f);il.Emit(OpCodes.Ret);
        return (Func<T>)dm.CreateDelegate(typeof(Func<T>));
    }
    // (generation, id) => s_clientMobLifecycle.IsDead(generation, id); the caller holds the base Sync lock as the base does.
    static Func<int,int,bool> LedgerReader(FieldInfo ledger,MethodInfo isDead)
    {
        var dm=new DynamicMethod("DeathSound_LedgerIsDead",typeof(bool),new[]{typeof(int),typeof(int)},typeof(DeathSoundPatch).Module,true);
        var il=dm.GetILGenerator();il.Emit(OpCodes.Ldsfld,ledger);il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Callvirt,isDead);il.Emit(OpCodes.Ret);
        return (Func<int,int,bool>)dm.CreateDelegate(typeof(Func<int,int,bool>));
    }
    // CoopPack: CoopPackRuntime.PackParts.FindType(part, type) (null when the part is absent, failed or switched off).
    // Without the pack: the single loaded assembly that declares the type (the separate legacy module), or null.
    static Type Companion(string part,string typeName)
    {
        var packs=AppDomain.CurrentDomain.GetAssemblies().Select(a=>{try{return a.GetType("CoopPackRuntime.PackParts",false);}catch{return null;}}).Where(t=>t!=null).Distinct().ToArray();
        if(packs.Length>1)throw new InvalidOperationException("More than one CoopPack runtime is loaded");
        if(packs.Length==1)
        {
            var find=packs[0].GetMethod("FindType",BindingFlags.Public|BindingFlags.Static,null,new[]{typeof(string),typeof(string)},null);
            if(find?.ReturnType!=typeof(Type))throw new InvalidOperationException("CoopPack companion lookup changed");
            return (Type)find.Invoke(null,new object[]{part,typeName});
        }
        var hits=AppDomain.CurrentDomain.GetAssemblies().Select(a=>{try{return a.GetType(typeName,false);}catch{return null;}}).Where(t=>t!=null).Distinct().ToArray();
        if(hits.Length>1)throw new InvalidOperationException("More than one "+typeName+" is loaded");
        return hits.FirstOrDefault();
    }

    public static void PatchManaged()
    {
        if(Patched)return;
        HarmonyInstance=new Harmony(Owner);
        HarmonyInstance.Patch(RunWithAuthority,
            prefix:new HarmonyMethod(typeof(DeathSoundPatch).GetMethod(nameof(EnterAuthoritativeDeath),BindingFlags.NonPublic|BindingFlags.Static)),
            finalizer:new HarmonyMethod(typeof(DeathSoundPatch).GetMethod(nameof(LeaveAuthoritativeDeath),BindingFlags.NonPublic|BindingFlags.Static)));
        Patched=true;
        HarmonyInstance.Patch(DeathHook,
            prefix:new HarmonyMethod(typeof(DeathSoundPatch).GetMethod(nameof(BeforeDeathHook),BindingFlags.NonPublic|BindingFlags.Static)){priority=Priority.Last});
        int Ours(IEnumerable<Patch> ps)=>ps.Count(p=>p.owner==Owner);
        var a=Harmony.GetPatchInfo(RunWithAuthority);var b=Harmony.GetPatchInfo(DeathHook);
        if(a==null||Ours(a.Prefixes)!=1||Ours(a.Finalizers)!=1||Ours(a.Postfixes)!=0||Ours(a.Transpilers)!=0||
           b==null||Ours(b.Prefixes)!=1||Ours(b.Finalizers)!=0||Ours(b.Postfixes)!=0||Ours(b.Transpilers)!=0)
            throw new InvalidOperationException("Death sound patches were not installed exactly once");
    }
    public static void UnpatchManaged()
    {
        if(HarmonyInstance==null)return;
        if(RunWithAuthority!=null)HarmonyInstance.Unpatch(RunWithAuthority,HarmonyPatchType.All,Owner);
        if(DeathHook!=null)HarmonyInstance.Unpatch(DeathHook,HarmonyPatchType.All,Owner);
        Patched=false;
    }
    // DCCM hooks on f@1075 (Mob.playDamageSounds) and f@19523 (AxeStatue.playDamageSounds). The generated hook delegates
    // live in GameProxy, which is compiled against System.Private.CoreLib; the pack's reference set (PSHOME ref facades)
    // cannot name them at compile time, so each handler is emitted against the event's own delegate type, as
    // CoopShrineDeathFix and the QA probes do: ObserveDieSound(self); orig.Invoke(self, a). No other instruction.
    static readonly List<(EventInfo Event,Delegate Handler)> NativeHooks=new List<(EventInfo,Delegate)>();
    static void HookNative()
    {
        foreach(var owner in new[]{"dc.en.Hook_Mob","dc.en.mob.Hook_AxeStatue"})
        {
            var ev=typeof(Mob).Assembly.GetType(owner,true).GetEvent("playDamageSounds")??throw new InvalidOperationException("Native hook event missing: "+owner);
            var handler=ObserverAdapter(ev.EventHandlerType);
            ev.AddEventHandler(null,handler);
            NativeHooks.Add((ev,handler));
        }
    }
    static void UnhookNative()
    {
        Exception failure=null;
        for(int i=NativeHooks.Count-1;i>=0;i--){try{NativeHooks[i].Event.RemoveEventHandler(null,NativeHooks[i].Handler);}catch(Exception e){failure??=e;}}
        NativeHooks.Clear();
        if(failure!=null)throw failure;
    }
    internal static Delegate ObserverAdapter(Type handlerType)
    {
        var invoke=handlerType.GetMethod("Invoke");
        var types=invoke?.GetParameters().Select(p=>p.ParameterType).ToArray();
        if(invoke?.ReturnType!=typeof(void)||types==null||types.Length!=3||!typeof(Mob).IsAssignableFrom(types[1])||types[2]!=typeof(AttackData))
            throw new InvalidOperationException("Native hook delegate shape changed: "+handlerType.FullName);
        var orig=types[0].GetMethod("Invoke");
        if(orig?.ReturnType!=typeof(void)||!orig.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{types[1],types[2]}))
            throw new InvalidOperationException("Native original delegate shape changed: "+types[0].FullName);
        var dm=new DynamicMethod("DeathSound_Observe_"+types[1].Name,typeof(void),types,typeof(DeathSoundPatch).Module,true);
        var il=dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Call,typeof(DeathSoundPatch).GetMethod(nameof(ObserveDieSound),BindingFlags.NonPublic|BindingFlags.Static));
        il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldarg_2);il.Emit(OpCodes.Callvirt,orig);il.Emit(OpCodes.Ret);
        return dm.CreateDelegate(handlerType);
    }

    // ---- 1. observe every die sound (life <= 0); never throws, so orig always runs exactly once ----
    static void ObserveDieSound(Mob self)
    {
        if(!Published||Tripped||self==null)return;
        try
        {
            if(!IsClientNet(NetRef()))return;
            if(self.life>0)return;
            int token=TryGetToken(out var t)?t:0;
            lock(RecentGate)Recent.Remember(self.HashlinkPointer,token,Now());
            Interlocked.Increment(ref observed);
            lock(StateGate)FaultState.Ok();
        }
        catch(Exception error){Fault("observe",error);}
    }

    // ---- 2. the authoritative death scope ----
    static void EnterAuthoritativeDeath(Mob mob,out bool __state)
    {
        __state=false;
        if(!Published||Tripped||mob==null)return;
        try
        {
            (Scopes??=new DeathSoundRules.ScopeStack()).Push(mob.HashlinkPointer);
            __state=true;
        }
        catch(Exception error){Fault("scope",error);}
    }
    static Exception LeaveAuthoritativeDeath(bool __state,Exception __exception)
    {
        if(__state)try{Scopes?.Pop();}catch{}
        return __exception;   // never replaces or swallows the original's exception
    }

    // ---- 3. the death hook: play once, before the native onDie ----
    struct LiveFacts:DeathSoundRules.IFacts
    {
        public Mob Self;public IntPtr Pointer;public int SyncId;
        public bool IsClient()=>IsClientNet(NetRef());
        public int AuthoritativeDepth()=>ReadAuthoritativeDepth();   // same thread that raised it (the game thread)
        public bool Destroyed()=>Self.destroyed;
        public bool OnDieDone()=>Self.onDieDone;
        public int Life()=>Self.life;
        public bool IsBoss()=>DeathSoundPatch.IsBoss(Self);
        public bool OutOfGame()=>Self.isOutOfGame;
        public bool OnScreen()=>Self.isOnScreen;
        public int SnapshotDepth()=>ReadSnapshotDepth==null?0:ReadSnapshotDepth();
        public bool RecentSound(){int token=TryGetToken(out var t)?t:0;lock(RecentGate)return Recent.Recent(Pointer,token,Now());}
        public bool ShrineReceipt()=>ShrineNames(Pointer);
        public bool LedgerDead()
        {
            // The base hook's own test (DeathHooks.cs:37-46, IL:134435-134458): sync id and identity token, then the ledger under Sync.
            if(!TryGetSyncId(Self,out var id))return false;
            SyncId=id;
            if(!TryGetToken(out var generation))return false;
            lock(ReadSync())return LedgerIsDead(generation,id);
        }
    }
    static void BeforeDeathHook(Mob self,bool __runOriginal)
    {
        if(!Published||Tripped||self==null)return;
        try
        {
            // Routine deaths (host deaths, suppressed local client deaths, a skipped original) never touch the mob here.
            var scopes=Scopes;bool top=false;IntPtr pointer=IntPtr.Zero;
            if(__runOriginal&&scopes!=null&&scopes.Depth>0){pointer=self.HashlinkPointer;top=scopes.IsTop(pointer);}
            var facts=new LiveFacts{Self=self,Pointer=pointer,SyncId=-1};
            var verdict=DeathSoundRules.Decide(ref facts,__runOriginal,top);
            if(DeathSoundRules.Counted(verdict))Interlocked.Increment(ref Verdicts[(int)verdict]);
            if(verdict==DeathSoundRules.Verdict.Play)Play(self,pointer,facts.SyncId);
            lock(StateGate)FaultState.Ok();
            if(DeathSoundRules.Counted(verdict))Summarize();
        }
        catch(Exception error){Fault("death",error);}
    }
    static void Play(Mob self,IntPtr pointer,int syncId)
    {
        // playDamageSounds' die branch (f@1075 ops 0-33) reads _level.lAudio without a fallback: never call it without both.
        var level=self._level;
        if(level==null||level.lAudio==null){Interlocked.Increment(ref playSkippedNoAudio);return;}
        self.playDamageSounds(null);   // virtual: AxeStatue's override plays its own die sound; AttackData is not read when life <= 0
        int token=TryGetToken(out var t)?t:0;
        lock(RecentGate)Recent.Remember(pointer,token,Now());
        Interlocked.Increment(ref played);
        bool detail;lock(StateGate)detail=Logs.Detail();
        if(detail)try{Serilog.Log.Information("[CoopDeathSoundFix] Played network death sound syncId={SyncId} type={Type}",syncId,self.GetType().Name);}catch{}
    }
    static bool ShrineNames(IntPtr pointer)
    {
        if(ShrineReceipts==null)return false;
        lock(ReadSync())   // CoopShrineDeathFix reads and writes its receipts only under the base Sync lock
        {
            if(ShrineReceipts.GetValue(null) is not IList list)return false;
            foreach(var receipt in list)if(receipt!=null&&(IntPtr)ShrineReplicaPointer.GetValue(receipt)==pointer)return true;
        }
        return false;
    }
    static void Summarize()
    {
        long signature=0;for(int i=0;i<Verdicts.Length;i++)signature+=Interlocked.Read(ref Verdicts[i]);
        bool write;lock(StateGate)write=Logs.Summary(Now(),signature);
        if(write)try{Serilog.Log.Information("[CoopDeathSoundFix] Network death sounds: {Stats}",Stats());}catch{}
    }
    static void Fault(string where,Exception error)
    {
        long n=Interlocked.Increment(ref faults);bool tripped;
        lock(StateGate)tripped=FaultState.Fail();
        if(tripped&&!Tripped)
        {
            Tripped=true;
            try{Serilog.Log.Warning("[CoopDeathSoundFix] Death sound switched off for this session after {Faults} faults; last at {Where}: {Error}",n,where,error.GetBaseException().Message);}catch{}
        }
        else if(n==1)try{Serilog.Log.Warning("[CoopDeathSoundFix] Death sound skipped at {Where}: {Error}",where,error.GetBaseException().Message);}catch{}
    }
}
