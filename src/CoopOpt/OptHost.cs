using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using HaxeProxy.Runtime.Internals.Cache;
using R=OptHostRules;
using Entity=dc.Entity;
using Mob=dc.en.Mob;
using Hero=dc.en.Hero;
using Level=dc.pr.Level;
using HlGame=dc.pr.Game;

// CoopOpt part OptHost: exact fast paths on the host's per-enemy path of the community mod 0.5.1 (designs in
// host-load-20261110\design, corrected by round2\host-per-enemy\skeptic.md and round2\target-selection\skeptic.md).
//   A1  IsMobHostileToPlayers / IsSyncMob read Entity._level, Entity._team, Level.teamMob as raw pointers
//       (offsets from the generated proxy's own field caches) instead of three proxy object reads each.
//   A3  TryActivateHostMobForRemotePlayer: the near-box test first; a mob that is not near returns false at once.
//   T1  IsEntityOnCurrentCombatIdentity decides CoopSyncFixes' MatchTrackedNativeLevel region itself (typed static
//       reads, no reflection, no boxed bool, no patched call); every other region runs the original. (T1a only.)
//   T2  IsAcquirablePlayerCombatTargetForMob: the second IsHardInvalidPlayerTargetEntity of a remote shell (IL_0016)
//       is dominated by a false result of the same call and is skipped.
//   T3  (= design D1) the local hero's canBeHitBy(mob) at IL_0046 calls the mod's two registered hook handlers
//       directly, with managed stand-ins for the natives f@2050 / f@664, instead of two hooked dispatches.
// Not built: A2 (superseded by T1, shadow and HarmonyX-order problems), A4 / A5 (deferred by the skeptic), T1b (raw
// _level read in T1: only if the stats timings show the getter is worth it).
//
// Every fast path is exact by argument and checked in the game: the first 4096 decided calls of each, then every
// 256th (COOP_OPT_SHADOW=all: every call) also run the original and the original's result is what the game gets; one
// difference switches that fast path off for the session. Kill switches COOP_OPT_HOST_<A1|A3|T1|T2|T3>_DISABLE=1 (and
// COOP_OPT_HOST_DISABLE / COOP_OPT_DISABLE in CoopOpt). COOP_OPT_STATS=1: one line per fast path every 5 s.
// Install() runs in CoopOpt.Initialize and only binds (reflection, pins, IL checks). The patches themselves are
// installed in one transaction at DCCM's IOnGameEndInit, as its last receiver: after every module's Initialize (DCCM
// initialises modules in folder order, so CoopOpt comes before CoopSyncFixes and the base mod), after the other
// modules' end-init work, and before the first game frame, so no caller of a target has run and every patch another
// module installs on a callee during initialisation is in place before our replacement bodies are compiled. Patches go
// only on MobsSynchronization (a plain managed class). Each fast path arms itself on its first calls in the game, once
// what it depends on exists (CoopSyncFixes' prefix, the mod's hook handlers, filled proxy field caches), and stays on the
// original code until then. Calls from any thread but DCCM's main thread always run the original.
public static partial class OptHost
{
    public const string Id="CoopOpt.Host.20261111";
    // Framework files the fast paths rely on (same pins as CoopLoadFix ChainCache, plus HaxeProxy and ModCore.Common
    // (the end-init event); GameProxy for T3).
    const string HashlinkSharpPin="207FC8DD54D8A0A804E4051F90D2375F2C58CF6E69EFC26EF3E7EC502DFE8B63";
    const string ModCorePin="630FBBCEDFE8B2E66C5F75234BE80C2EC42FB178C30706E7398583D9FA1A14AE";
    const string ModCoreCommonPin="0BCB0F339F63715ADDD4833108C30B5E226C7355E9048A999C6ACCD2D90D502F";
    const string HarmonyPin="70E7647876B6C3D874EA99B56627A95D1DCE2E1DD791981A002578A2FEBC9BBD";
    const string HaxeProxyPin="0DF9697335FFDBA4461E599CEE063BC41E4B76C881CCAEDB08AE16DC4A27A8B2";
    const string GameProxyPin="2A1803BA27DB053A21CD854CA4F720C5F5D874FE384F72603CA524F21BB98259";
    const string MobsSyncName="DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization";

    // One fast path: its kill switch, life cycle, counters, sampling and fault gate.
    sealed class HostItem
    {
        public readonly string Id;public readonly R.CounterSet C;public readonly OptFaultGate Gate;
        public bool Installed;                  // bound at Install and part of the install transaction
        public int State=R.StateOff;            // R.StateWaiting / StateOn / StateOff (Volatile)
        public string Reason="not installed";
        public Func<string> Arm;                // game thread: null = armed, "~why" = not yet, other = switch off
        public Func<string> PatchCheck;         // any thread (Harmony patch info only): null = fine
        public long WaitingCalls,OffThread,Shadows,Mismatches,Timed,FastTicks,OrigTicks,FastBytes,OrigBytes,Fast;
        string _lastWait;
        public HostItem(string id,params string[] counters){Id=id;C=new R.CounterSet(counters);Gate=new OptFaultGate("OptHost."+id);}
        public int Current=>Volatile.Read(ref State);
        public bool On=>Volatile.Read(ref State)==R.StateOn;
        // Game thread. True when the fast path may run now; a waiting item tries to arm on its first call and then
        // on every 512th.
        public bool Ready()
        {
            int s=Volatile.Read(ref State);
            if(s==R.StateOn)return true;
            if(s!=R.StateWaiting||!R.ArmAttemptDue(++WaitingCalls))return false;
            string r;
            try{r=Arm();}catch(Exception e){r="arm failed: "+e.GetBaseException().Message;}
            if(r==null)
            {
                if(Interlocked.CompareExchange(ref State,R.StateOn,R.StateWaiting)!=R.StateWaiting)return false;
                Reason="armed";OptCore.Info("host {Item} armed; shadowAll={All} gameThread={Thread}",Id,ShadowAll,GameThread);
                return true;
            }
            if(r.StartsWith("~",StringComparison.Ordinal))
            {
                if(r!=_lastWait){_lastWait=r;Reason=r.Substring(1);OptCore.Info("host {Item} waiting: {Reason}",Id,Reason);}
                return false;
            }
            SwitchOff(r);return false;
        }
        public void SwitchOff(string reason)
        {
            int was=Interlocked.Exchange(ref State,R.StateOff);
            if(was==R.StateOff)return;
            Reason=reason;
            OptCore.Warn("host {Item} switched off for this session, the original code is in effect: {Reason}",Id,reason);
        }
        public void Fault(Exception e){if(Gate.Fail(e))SwitchOff("faults: "+e.GetBaseException().Message);}
        public void Mismatch(string detail){Interlocked.Increment(ref Mismatches);SwitchOff("shadow comparison differs: "+detail);}
        public void Time(long fastTicks,long fastBytes,long origTicks,long origBytes){Timed++;FastTicks+=fastTicks;FastBytes+=fastBytes;OrigTicks+=origTicks;OrigBytes+=origBytes;}
    }
    // The verdict of one shadowed call whose two routes both returned: a difference switches the item off. The game
    // always gets the original's value. Never throws.
    static bool Settle(HostItem item,string what,object subject,bool fast,bool orig)
    {
        try
        {
            if(fast!=orig)item.Mismatch(what+" fast="+fast+" original="+orig+" ("+subject?.GetType().Name+")");
            else item.Gate.Ok();
        }
        catch{}
        return orig;
    }

    static readonly object InstallSync=new();
    static bool _bound,_patchAttempted;
    static string InstallError;
    static bool ShadowAll,Stats;
    static int GameThread;                      // DCCM's main thread (ModCore.Core.MainThread), resolved on the first call
    static string GameThreadError;
    static Timer Ticker;static long TickCount;
    static Type Sync;
    static Harmony Owner;
    static HostItem[] Items=Array.Empty<HostItem>();

    // CoopOpt.Initialize: binds every item (reflection only: no patch, no compiled copy, no game state) and registers
    // the end-init receiver that installs the patches. Throws when no item can be bound.
    public static void Install()
    {
        lock(InstallSync)
        {
            if(_bound)return;
            OptCore.CheckPins();
            ShadowAll=R.ShadowAllMode(Environment.GetEnvironmentVariable("COOP_OPT_SHADOW"));
            Stats=R.SwitchSet(Environment.GetEnvironmentVariable("COOP_OPT_STATS"));
            Items=new[]{A1,A3,T1,T2,T3};
            BindShared();
            foreach(var item in Items)
            {
                if(R.SwitchSet(Environment.GetEnvironmentVariable(R.ItemSwitch(item.Id)))){item.Reason="switched off by "+R.ItemSwitch(item.Id)+"=1";continue;}
                try{BindItem(item);item.Installed=true;}
                catch(Exception e){item.Installed=false;item.Reason="not installed: "+e.GetBaseException().Message;OptCore.Warn("host {Item} not installed: {Error}",item.Id,e.GetBaseException().Message);}
            }
            if(!Items.Any(i=>i.Installed))throw new InvalidOperationException("no host fast path is installable ("+string.Join("; ",Items.Select(i=>i.Id+": "+i.Reason))+")");
            ModCore.Events.EventSystem.AddReceiver(EndInitReceiver);
            _bound=true;
            foreach(var item in Items)if(item.Installed)item.Reason="bound, patched at game end-init";
            OptCore.Info("host fast paths bound: {Items}; their patches are installed at the game's end-init, after every module has initialised",
                string.Join(", ",Items.Select(i=>i.Id+"="+(i.Installed?"bound":i.Reason))));
        }
    }

    // DCCM's IOnGameEndInit, as the last receiver (game thread, once, before the first game frame).
    sealed class EndInit:ModCore.Events.IEventReceiver,ModCore.Events.Interfaces.Game.IOnGameEndInit
    {
        int ModCore.Events.IEventReceiver.Priority=>int.MaxValue;
        public void OnGameEndInit(){try{PatchNow();}catch{}}
    }
    static readonly EndInit EndInitReceiver=new();

    // The install transaction. Never throws: any failure unpatches every target by owner and leaves every item off.
    static void PatchNow()
    {
        lock(InstallSync)
        {
            if(!_bound||_patchAttempted)return;
            _patchAttempted=true;
            var harmony=new Harmony(Id);var patched=new List<MethodBase>();
            try
            {
                foreach(var item in Items)
                {
                    if(!item.Installed)continue;
                    try{Prepare(item);}
                    catch(Exception e){item.Installed=false;item.Reason="not installed: "+e.GetBaseException().Message;OptCore.Warn("host {Item} not installed: {Error}",item.Id,e.GetBaseException().Message);}
                }
                var plan=Plan();
                if(plan.Count==0)throw new InvalidOperationException("no host fast path is installable ("+string.Join("; ",Items.Select(i=>i.Id+": "+i.Reason))+")");
                foreach(var p in plan)
                {
                    patched.Add(p.Target);   // before Patch: a throw half way is still rolled back (Unpatch by owner)
                    harmony.Patch(p.Target,p.Prefix,p.Postfix,p.Transpiler);
                }
                foreach(var p in plan)ExactlyOnce(p);
                Owner=harmony;
                foreach(var item in Items)if(item.Installed){item.Reason="installed, arms on its first calls in the game";Volatile.Write(ref item.State,R.StateWaiting);}
                Ticker=new Timer(OnTick,null,1000,1000);
                OptCore.Info("host patches installed: {Items}; shadowAll={All} stats={Stats}",
                    string.Join(", ",Items.Select(i=>i.Id+"="+(i.Installed?"waiting":i.Reason))),ShadowAll,Stats);
            }
            catch(Exception e)
            {
                foreach(var t in patched)try{harmony.Unpatch(t,HarmonyPatchType.All,Id);}catch(Exception r){OptCore.Warn("host rollback of {Target} failed: {Error}",t.Name,r.Message);}
                InstallError=e.GetBaseException().Message;
                foreach(var item in Items){item.Installed=false;item.Reason="not installed: "+InstallError;Volatile.Write(ref item.State,R.StateOff);}
                OptCore.Warn("host patches not installed, the original code is in effect: {Error}",InstallError);
            }
        }
    }

    // ---- binding (Initialize: reflection only, no game state is read) ----
    static void BindShared()
    {
        Sync=OptCore.ModType(MobsSyncName);
        if(Sync.BaseType!=typeof(object))throw new InvalidOperationException("MobsSynchronization is not a plain managed class");
        PinFile(typeof(Hashlink.Marshaling.HashlinkMarshal).Assembly,HashlinkSharpPin);
        PinFile(typeof(ModCore.Mods.ModBase).Assembly,ModCorePin);
        PinFile(typeof(ModCore.Events.EventSystem).Assembly,ModCoreCommonPin);
        PinFile(typeof(Harmony).Assembly,HarmonyPin);
        PinFile(typeof(HaxeProxy.Runtime.HaxeProxyBase).Assembly,HaxeProxyPin);
        if(typeof(ModEntry).BaseType!=typeof(ModCore.Mods.ModBase))throw new InvalidOperationException("ModEntry base changed");
        BindOwner();
    }
    static void BindItem(HostItem item)
    {
        switch(item.Id)
        {
            case "A1":BindA1();break;
            case "A3":BindA3();break;
            case "T1":BindT1();break;
            case "T2":BindT2();break;
            case "T3":BindT3();break;
            default:throw new InvalidOperationException("unknown item "+item.Id);
        }
    }
    // End-init, before the patches: work that compiles code (kept out of Initialize so it sees the other modules' patches).
    static void Prepare(HostItem item){if(item.Id=="A1")PrepareA1();}
    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);
    static void PinFile(Assembly a,string pin)
    {
        if(string.IsNullOrEmpty(a.Location)||Hash(a.Location)!=pin)throw new InvalidOperationException("pinned file changed: "+a.GetName().Name);
    }
    static MethodInfo SyncMethod(string name,Type ret,params Type[] args)=>OptCore.Method(Sync,name,true,ret,args);
    static void Pin(MethodBase m,(string Name,int Length,string Sha)[] table)
    {
        foreach(var p in table)
            if(p.Name==m.Name)
            {
                if(!R.PinMatches(m.GetMethodBody()?.GetILAsByteArray(),p.Length,p.Sha))throw new InvalidOperationException("pinned IL changed: "+m.DeclaringType?.Name+"."+m.Name);
                return;
            }
        throw new InvalidOperationException("no pin for "+m.Name);
    }
    static bool Same(MemberInfo a,MemberInfo b)=>a!=null&&b!=null&&a.MetadataToken==b.MetadataToken&&a.Module==b.Module;
    // A generated proxy getter: ldarg.0; ldstr name; ldsflda cache; call HaxeProxyHelper.<helper><T>; ret.
    // Returns the cache field it reads (the field the getter's own offset lives in).
    static FieldInfo ProxyGetter(Type owner,string getter,string fieldName,string helper,List<MethodBase> collect=null)
    {
        var m=owner.GetMethod(getter,OptCore.I,null,Type.EmptyTypes,null)??throw new MissingMethodException(owner.Name,getter);
        collect?.Add(m);
        var il=m.GetMethodBody()?.GetILAsByteArray();
        if(!R.FieldGetterShape(il))throw new InvalidOperationException("proxy getter shape changed: "+owner.Name+"."+getter);
        var module=m.Module;
        if(module.ResolveString(R.Int32At(il,R.GetterStringAt))!=fieldName)throw new InvalidOperationException("proxy getter field changed: "+owner.Name+"."+getter);
        var cache=module.ResolveField(R.Int32At(il,R.GetterFieldAt));
        if(cache==null||!cache.IsStatic||cache.FieldType!=typeof(ObjFieldInfoCache)||cache.DeclaringType!=owner)throw new InvalidOperationException("proxy getter cache changed: "+owner.Name+"."+getter);
        var h=module.ResolveMethod(R.Int32At(il,R.GetterHelperAt));
        if(h==null||h.Name!=helper||h.DeclaringType!=typeof(HaxeProxy.Runtime.Internals.HaxeProxyHelper))throw new InvalidOperationException("proxy getter helper changed: "+owner.Name+"."+getter);
        return cache;
    }
    static MethodInfo ProxyCall(Type owner,string name,int findex,params Type[] args)
    {
        var m=owner.GetMethod(name,OptCore.I,null,args,null)??throw new MissingMethodException(owner.Name,name);
        if(!R.CallsFunction(m.GetMethodBody()?.GetILAsByteArray(),findex))throw new InvalidOperationException("proxy method no longer calls f@"+findex+": "+owner.Name+"."+name);
        return m;
    }
    // A static field read compiled once (no reflection and no boxing per call).
    static Func<T> StaticGetter<T>(FieldInfo f)
    {
        if(!f.IsStatic||f.FieldType!=typeof(T))throw new InvalidOperationException("pinned static field changed: "+f.Name);
        var dm=new DynamicMethod("CoopOpt_Read_"+f.Name,typeof(T),Type.EmptyTypes,typeof(OptHost).Module,true);
        var il=dm.GetILGenerator();il.Emit(OpCodes.Ldsfld,f);il.Emit(OpCodes.Ret);
        return (Func<T>)dm.CreateDelegate(typeof(Func<T>));
    }

    // ---- CoopSyncFixes (pinned build): the owner of the two level-identity patches T1 replicates and T2 may run ----
    static MethodInfo LevelMatchMethod,ComputeTokenMethod,OwnerPrefix,OwnerField,OwnerSalt;
    static string OwnerError;
    static Type FindOwnerModule()
    {
        {var t=global::CoopPackRuntime.PackParts.FindType("CoopSyncFixes","CoopSyncFixes");if(t!=null)return t;}
        return null;
    }
    // Every module assembly is loaded before any module initialises (DCCM loads them while collecting mod infos).
    static void BindOwner()
    {
        LevelMatchMethod=SyncMethod("DoesLevelMatchCurrentIdentityLocked",typeof(bool),typeof(Level));
        ComputeTokenMethod=SyncMethod("ComputeLevelIdentityToken",typeof(int),typeof(Level));
        Pin(LevelMatchMethod,R.ModPins);
        OwnerPrefix=OwnerField=OwnerSalt=null;
        try
        {
            var owner=FindOwnerModule()??throw new InvalidOperationException("CoopSyncFixes is not loaded");
            if(string.IsNullOrEmpty(owner.Assembly.Location)||owner.Assembly!=typeof(OptHost).Assembly)throw new InvalidOperationException("CoopSyncFixes is not the pinned build");
            var prefix=OptCore.Method(owner,"MatchTrackedNativeLevel",true,typeof(bool),typeof(Level),typeof(bool).MakeByRefType());
            var field=OptCore.Method(owner,"Field",true,typeof(object),typeof(string));
            var salt=OptCore.Method(owner,"SaltBranchIdentity",true,typeof(void),typeof(Level),typeof(int).MakeByRefType());
            /* R12: in CoopPack the owner's IL is pinned at build time (G1a, G4), not by raw IL bytes, whose metadata tokens differ in a merged assembly */
            OwnerPrefix=prefix;OwnerField=field;OwnerSalt=salt;OwnerError=null;
        }
        catch(Exception e){OwnerError=e.GetBaseException().Message;}
    }

    // ---- the install transaction ----
    sealed class PatchPlan{public MethodBase Target;public HarmonyMethod Prefix,Postfix,Transpiler;}
    static MethodInfo Own(string name)=>typeof(OptHost).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public)??throw new MissingMethodException("OptHost",name);
    // Prefixes last (a measuring prefix of another module would start before ours), postfixes first (ours compares the
    // original's own result before any other postfix sees it). Arming refuses any foreign patch on these targets.
    static HarmonyMethod Pre(string n)=>new(Own(n)){priority=Priority.Last};
    static HarmonyMethod Post(string n)=>new(Own(n)){priority=Priority.First};
    static List<PatchPlan> Plan()
    {
        // A callee is patched before its caller, so the caller's replacement body is compiled against an already
        // detoured callee (a detoured method is not inlined into code compiled afterwards).
        var plan=new List<PatchPlan>();
        if(A1.Installed)
        {
            plan.Add(new PatchPlan{Target=HostileMethod,Prefix=Pre(nameof(BeforeHostile))});
            plan.Add(new PatchPlan{Target=SyncMobMethod,Prefix=Pre(nameof(BeforeSyncMob))});
        }
        if(A3.Installed)
        {
            plan.Add(new PatchPlan{Target=NearMethod,Postfix=Post(nameof(AfterNear))});
            plan.Add(new PatchPlan{Target=ActivateMethod,Prefix=Pre(nameof(BeforeActivate)),Postfix=Post(nameof(AfterActivate))});
        }
        if(T1.Installed)plan.Add(new PatchPlan{Target=IdentityMethod,Prefix=Pre(nameof(BeforeIdentity)),Postfix=Post(nameof(AfterIdentity))});
        if(T2.Installed||T3.Installed)plan.Add(new PatchPlan{Target=AcquirableMethod,Transpiler=new HarmonyMethod(Own(nameof(TranspileAcquirable)))});
        return plan;
    }
    static void ExactlyOnce(PatchPlan p)
    {
        var info=Harmony.GetPatchInfo(p.Target)??throw new InvalidOperationException("host patch missing: "+p.Target.Name);
        int Ours(IEnumerable<Patch> ps)=>ps.Count(x=>x.owner==Id);
        if(Ours(info.Prefixes)!=(p.Prefix!=null?1:0)||Ours(info.Postfixes)!=(p.Postfix!=null?1:0)||Ours(info.Transpilers)!=(p.Transpiler!=null?1:0)||Ours(info.Finalizers)!=0)
            throw new InvalidOperationException("host patch not installed exactly once: "+p.Target.Name);
    }
    static IEnumerable<Patch> AllPatches(Patches i)=>i.Prefixes.Concat(i.Postfixes).Concat(i.Transpilers).Concat(i.Finalizers).Concat(i.ILManipulators);

    // ---- guards: what a fast path must see unpatched ----
    // A fast path skips calls the original makes (or, on a shadow call, makes them twice). Every method the skipped part
    // can run - the mod's own methods followed transitively through their IL, and the generated proxy and framework
    // members those call - must carry no patch but ours, except CoopSyncFixes' two pinned level-identity patches (pure
    // for the purpose: same value for the same level). Checked when the item arms and every 2 s while it is on.
    sealed class Guard{public MethodBase[] Methods=Array.Empty<MethodBase>();public readonly HashSet<(Module,int)> Keys=new();}
    static (Module,int) Key(MethodBase m)=>(m.Module,m.MetadataToken);
    // Members of these assemblies are guarded; the mod's own methods are also followed into their callees.
    static bool Guarded(MethodBase m)
    {
        var a=m.Module.Assembly;
        return a==OptCore.Mod||a==typeof(Entity).Assembly||a==typeof(ModCore.Mods.ModBase).Assembly||a==typeof(ModCore.Events.EventSystem).Assembly;
    }
    // The methods a body names at its call sites (call, callvirt, newobj, jmp, ldftn, ldvirtftn) between two IL offsets.
    static IEnumerable<MethodBase> Callees(MethodBase m,int from=0,int to=int.MaxValue)
    {
        var il=m.GetMethodBody()?.GetILAsByteArray();
        if(il==null)yield break;
        var sites=R.CallSites(il)??throw new InvalidOperationException("cannot decode the IL of "+m.DeclaringType?.Name+"."+m.Name);
        var typeArgs=m.DeclaringType!=null&&m.DeclaringType.IsGenericType?m.DeclaringType.GetGenericArguments():null;
        var methodArgs=m.IsGenericMethod?m.GetGenericArguments():null;
        foreach(var s in sites)
            if(s.Offset>=from&&s.Offset<to)
                yield return m.Module.ResolveMethod(s.Token,typeArgs,methodArgs)??throw new InvalidOperationException("unresolved call in "+m.Name);
    }
    // direct: guarded as given; closure: guarded together with everything they can run.
    static Guard NewGuard(IEnumerable<MethodBase> direct,IEnumerable<MethodBase> closure)
    {
        var g=new Guard();var list=new List<MethodBase>();var queue=new Queue<MethodBase>();var followed=new HashSet<(Module,int)>();
        void Add(MethodBase m,bool follow)
        {
            if(g.Keys.Add(Key(m)))
            {
                list.Add(m);
                if(list.Count>R.MaxGuarded)throw new InvalidOperationException("guard closure larger than "+R.MaxGuarded+" methods");
            }
            if(follow&&m.Module.Assembly==OptCore.Mod&&followed.Add(Key(m)))queue.Enqueue(m);
        }
        foreach(var m in direct??Enumerable.Empty<MethodBase>())Add(m??throw new InvalidOperationException("unbound guard member"),false);
        foreach(var m in closure??Enumerable.Empty<MethodBase>())
        {
            if(m==null||!Guarded(m))throw new InvalidOperationException("guard root outside the guarded assemblies: "+m?.Name);
            Add(m,true);
        }
        while(queue.Count>0)foreach(var c in Callees(queue.Dequeue()))if(Guarded(c))Add(c,true);
        g.Methods=list.ToArray();
        return g;
    }
    // Null when every patched member of the guard carries only allowed patches; else who patches what.
    static string Foreign(Guard g)
    {
        foreach(var m in Harmony.GetAllPatchedMethods())
            if(m!=null&&g.Keys.Contains(Key(m))){var f=Foreign(m);if(f!=null)return f;}
        return null;
    }
    // Null when every patch on m is ours, is CoopSyncFixes' pinned one on its level-identity helper, or there is none.
    static string Foreign(MethodBase m)
    {
        var info=Harmony.GetPatchInfo(m);
        if(info==null)return null;
        var all=AllPatches(info).ToList();
        if(R.OnlyOwner(all.Select(p=>p.owner),Id))return null;
        var foreign=all.Where(p=>p.owner!=Id).ToList();var p0=foreign[0];
        if(OwnerPrefix!=null&&Same(m,LevelMatchMethod)&&info.Prefixes.Contains(p0)&&Same(p0.PatchMethod,OwnerPrefix)&&
           R.OwnerPrefixExact(info.Prefixes.Count(x=>x.owner!=Id),p0.owner,p0.PatchMethod?.Name,p0.PatchMethod?.DeclaringType?.FullName,foreign.Count-1))return null;
        if(OwnerSalt!=null&&Same(m,ComputeTokenMethod)&&info.Postfixes.Contains(p0)&&Same(p0.PatchMethod,OwnerSalt)&&
           R.OwnerPostfixExact(info.Postfixes.Count(x=>x.owner!=Id),p0.owner,p0.PatchMethod?.Name,p0.PatchMethod?.DeclaringType?.FullName,foreign.Count-1))return null;
        return "foreign patch by "+p0.owner+" ("+p0.PatchMethod?.DeclaringType?.Name+"."+p0.PatchMethod?.Name+") on "+m.DeclaringType?.Name+"."+m.Name;
    }

    // ---- the game thread ----
    // DCCM runs the game loop on the thread that initialised its core (ModCore.Core.MainThread); read on first use
    // (reading it at install time would run ModCore.Core's static constructor earlier than the framework does).
    static bool OnGameThread()
    {
        int g=GameThread;
        if(g==0)g=ResolveGameThread();
        return g>0&&Environment.CurrentManagedThreadId==g;
    }
    static int ResolveGameThread()
    {
        lock(InstallSync)
        {
            if(GameThread!=0)return GameThread;
            try
            {
                var core=typeof(ModCore.Mods.ModBase).Assembly.GetType("ModCore.Core",true);
                var p=core.GetProperty("MainThread",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
                if(p==null||p.PropertyType!=typeof(Thread))throw new InvalidOperationException("ModCore.Core.MainThread changed");
                var t=(Thread)p.GetValue(null)??throw new InvalidOperationException("ModCore.Core.MainThread is null");
                GameThread=t.ManagedThreadId;
            }
            catch(Exception e)
            {
                GameThread=-1;GameThreadError=e.GetBaseException().Message;
                OptCore.Warn("host fast paths stay off: the game thread is not known ({Error})",GameThreadError);
            }
            return GameThread;
        }
    }

    // ---- hook managers (T2 / T3): the framework's per-function hook managers, keyed by entry address ----
    static IDictionary HookManagers;
    static FieldInfo ManagersField,MgrHooks,MgrCallback,MgrFunction;
    static MethodInfo HookEntryMethod;
    // A guard keeps a fast path on only while no hook manager exists for functions whose behaviour it relies on.
    sealed class HookGuard{public int[] Functions;public object[] Keys;public int Seen=-1;}
    static IntPtr Entry(int findex)=>((Hashlink.Reflection.Members.HashlinkFunction)Hashlink.Marshaling.HashlinkMarshal.Module.GetFunctionByFIndex(findex)).EntryPointer;
    static void BindHookManagers()
    {
        if(ManagersField!=null)return;
        var f=typeof(ModCore.Modules.HashlinkHooks).GetField("managers",OptCore.I)??throw new MissingFieldException("HashlinkHooks","managers");
        var t=f.FieldType;
        if(!t.IsGenericType||t.GetGenericTypeDefinition()!=typeof(Dictionary<,>)||t.GetGenericArguments()[0]!=typeof(IntPtr)||t.GetGenericArguments()[1].FullName!="ModCore.Hooks.HashlinkHookManager")
            throw new InvalidOperationException("pinned HashlinkHooks.managers changed");
        var mgr=t.GetGenericArguments()[1];
        MgrHooks=OptCore.Field(mgr,"hooks",typeof(List<Delegate>),false);
        MgrCallback=OptCore.Field(mgr,"callback",typeof(Hashlink.Wrapper.Callbacks.HlCallback),false);
        MgrFunction=OptCore.Field(mgr,"function",typeof(Hashlink.Reflection.Members.HashlinkFunction),false);
        foreach(var x in new[]{f,MgrHooks,MgrCallback,MgrFunction})if(!x.IsInitOnly)throw new InvalidOperationException("pinned hook manager field is not readonly: "+x.Name);
        // The dispatcher every hooked call enters (T3's fast route does not): exactly one, object HookEntry(object[]).
        var entries=mgr.GetMethods(OptCore.I).Where(m=>m.Name=="HookEntry").ToArray();
        if(entries.Length!=1||entries[0].ReturnType!=typeof(object)||!entries[0].GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(object[])}))
            throw new InvalidOperationException("pinned HashlinkHookManager.HookEntry changed");
        HookEntryMethod=entries[0];
        ManagersField=f;
    }
    // Null while HookEntry carries only the known patches of CoopLoadFix (chain cache) and CoopNetMeter (QA meter).
    static string HookEntryForeign()
    {
        var info=Harmony.GetPatchInfo(HookEntryMethod);
        if(info==null)return null;
        foreach(var (p,kind) in info.Prefixes.Select(x=>(x,0)).Concat(info.Postfixes.Select(x=>(x,1))).Concat(info.Transpilers.Concat(info.Finalizers).Concat(info.ILManipulators).Select(x=>(x,2))))
            if(!R.HookEntryPatchAllowed(kind==0,kind==1,p.owner,p.PatchMethod?.DeclaringType?.FullName,p.PatchMethod?.Name))
                return "unknown patch on the hook dispatcher HashlinkHookManager.HookEntry by "+p.owner+" ("+p.PatchMethod?.DeclaringType?.Name+"."+p.PatchMethod?.Name+")";
        return null;
    }
    // Game thread (arming): the manager table, read once (the field is readonly).
    static IDictionary Managers()
    {
        var m=HookManagers;
        if(m!=null)return m;
        return HookManagers=(IDictionary)ManagersField.GetValue(ModCore.Modules.HashlinkHooks.Instance);
    }
    static string ArmGuard(HookGuard g)
    {
        var managers=Managers();
        if(g.Keys==null)g.Keys=g.Functions.Select(i=>(object)Entry(i)).ToArray();
        for(int i=0;i<g.Keys.Length;i++)if(managers.Contains(g.Keys[i]))return "a hook manager exists for HL function f@"+g.Functions[i];
        g.Seen=managers.Count;
        return null;
    }
    // Per call: only when the table changed size are the guarded functions looked up again.
    static bool GuardIntact(HookGuard g,HostItem item)
    {
        var managers=HookManagers;int n=managers.Count;
        if(n==g.Seen)return true;
        var r=ArmGuard(g);
        if(r!=null){item.SwitchOff(r);return false;}
        return true;
    }

    // ---- timer: patch checks every 2 s, stats every 5 s, one hint after 3 minutes ----
    static void OnTick(object _)
    {
        try
        {
            long n=Interlocked.Increment(ref TickCount);
            if(R.Due(n,R.VerifyPeriodSeconds))Verify();
            if(Stats&&R.Due(n,R.StatsPeriodSeconds))Report();
            if(n==R.QuietHintSeconds)QuietHint();
        }
        catch{}
    }
    static void Verify()
    {
        foreach(var item in Items)
        {
            if(!item.On||item.PatchCheck==null)continue;
            string r;
            try{r=item.PatchCheck();}catch(Exception e){r="patch check failed: "+e.GetBaseException().Message;}
            if(r!=null)item.SwitchOff(r.TrimStart('~'));
        }
    }
    static void Report()
    {
        double us=1e6/Stopwatch.Frequency,ns=1e9/Stopwatch.Frequency;
        foreach(var it in Items)
        {
            try
            {
                long timed=it.Timed;
                string timing=timed>0?string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $" timed={timed} fastNs={R.PerCall(it.FastTicks,timed,ns):0} origNs={R.PerCall(it.OrigTicks,timed,ns):0} fastB={R.PerCall(it.FastBytes,timed,1):0.0} origB={R.PerCall(it.OrigBytes,timed,1):0.0}"):"";
                OptCore.Info("host {Line:l}",it.Id+" state="+R.StateName(it.Current)+" "+it.C.Deltas()+" offThread="+Interlocked.Read(ref it.OffThread)+
                    " shadows="+Interlocked.Read(ref it.Shadows)+" mismatches="+Interlocked.Read(ref it.Mismatches)+" faults="+it.Gate.Faults+timing+
                    (it.Current==R.StateOn?"":" reason="+it.Reason));
            }
            catch{}
        }
    }
    static void QuietHint()
    {
        foreach(var it in Items)
        {
            if(!it.Installed)continue;
            if(it.Current!=R.StateOn)OptCore.Warn("host {Item} is not active after 3 minutes: {Reason}",it.Id,it.Reason);
            else if(Interlocked.Read(ref it.Fast)==0&&Interlocked.Read(ref it.Shadows)==0)
                OptCore.Warn("host {Item} is active but has served no call in 3 minutes (not called, or bypassed by an inlined copy)",it.Id);
        }
    }
    static long Now()=>Stopwatch.GetTimestamp();
    static long Bytes()=>GC.GetAllocatedBytesForCurrentThread();
}
