using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using System.Threading;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using Hashlink.Wrapper.Callbacks;
using R=OptHostRules;
using Entity=dc.Entity;
using Mob=dc.en.Mob;
using Hero=dc.en.Hero;
using Level=dc.pr.Level;
using HlGame=dc.pr.Game;
using ModGame=ModCore.Modules.Game;

// T1, T2 and T3 of OptHost (see OptHost.cs): the host's attack-target selection, per enemy per frame.
public static partial class OptHost
{
    // =====================================================================================================
    // T1 (T1a). IsEntityOnCurrentCombatIdentity decides the owner prefix's region itself.
    //
    // Release IL 120327: e==null ? false : try{ lock(Sync){ DoesLevelMatchCurrentIdentityLocked(e._level) } } catch false.
    // CoopSyncFixes (pinned build, Branches.cs:48-53) prefixes DoesLevelMatchCurrentIdentityLocked with
    // MatchTrackedNativeLevel: current = currentLevel; level==null || current==null || !s_levelIdentityReady -> run the
    // release body; else level.HashlinkPointer == current.HashlinkPointer. T1 takes the same lock, reads e._level through
    // the same getter, reads the same two statics through compiled accessors (no FieldInfo.GetValue, no boxed bool) and
    // decides only inside that region; outside it the original runs unchanged. Armed only while that one prefix of that
    // owner (DLL hash, method IL) is the only patch on DoesLevelMatchCurrentIdentityLocked, every patch on
    // IsEntityOnCurrentCombatIdentity is ours and the owner's prefix and its Field helper carry no patch (checked again
    // every 2 s). Not installed with COOP_SYNC_DISABLE_BRANCHES=1.
    // Shadow: the decision is kept, the original runs (with the owner prefix) and a postfix compares.
    // =====================================================================================================
    static readonly HostItem T1=new("T1","calls","fastTrue","fastFalse","notArmed","nullEntity","noLevel","noCurrent","notReady"){Arm=ArmT1,PatchCheck=CheckT1};
    const int T1Calls=0,T1True=1,T1False=2,T1Waiting=3,T1Null=4,T1NoLevel=5,T1NoCurrent=6,T1NotReady=7;
    static MethodInfo IdentityMethod;
    static Func<object> SyncLockGetter;static Func<Level> CurrentLevel;static Func<bool> IdentityReady;
    static object SyncLock;
    static long IdentitySamples;
    static Guard T1Guard;
    [ThreadStatic] static long T1FastTicks,T1FastBytes,T1OrigT0,T1OrigB0;

    static void BindT1()
    {
        if(R.SwitchSet(Environment.GetEnvironmentVariable("COOP_SYNC_DISABLE_BRANCHES")))
            throw new InvalidOperationException("COOP_SYNC_DISABLE_BRANCHES=1: CoopSyncFixes installs no prefix whose region T1 could decide");
        if(OwnerPrefix==null)throw new InvalidOperationException(OwnerError??"CoopSyncFixes is not bound");
        IdentityMethod=SyncMethod("IsEntityOnCurrentCombatIdentity",typeof(bool),typeof(Entity));
        Pin(IdentityMethod,R.ModPins);
        var lockField=OptCore.Field(Sync,"Sync",typeof(object),true);
        if(!lockField.IsInitOnly)throw new InvalidOperationException("pinned MobsSynchronization.Sync is not readonly");
        SyncLockGetter=StaticGetter<object>(lockField);
        CurrentLevel=StaticGetter<Level>(OptCore.Field(Sync,"currentLevel",typeof(Level),true));
        IdentityReady=StaticGetter<bool>(OptCore.Field(Sync,"s_levelIdentityReady",typeof(bool),true));
        var levelGetter=new List<MethodBase>();
        ProxyGetter(typeof(Entity),"get__level","_level","GetFieldById",levelGetter);
        // What a decided call skips (the owner's prefix and its Field helper; the release body is not reached there) and
        // what a shadow call runs twice (the _level getter).
        T1Guard=NewGuard(new MethodBase[]{IdentityMethod,OwnerPrefix,OwnerField}.Concat(levelGetter),null);
    }
    static string CheckT1()
    {
        var f=Foreign(T1Guard);if(f!=null)return f;
        var info=Harmony.GetPatchInfo(LevelMatchMethod);
        if(info==null||info.Prefixes.Count==0)return "~CoopSyncFixes' prefix on DoesLevelMatchCurrentIdentityLocked is not installed yet";
        var all=AllPatches(info).ToList();var p=info.Prefixes[0];
        if(!R.OwnerPrefixExact(info.Prefixes.Count,p.owner,p.PatchMethod?.Name,p.PatchMethod?.DeclaringType?.FullName,all.Count-1)||!Same(p.PatchMethod,OwnerPrefix))
            return "DoesLevelMatchCurrentIdentityLocked is not patched exactly by CoopSyncFixes.MatchTrackedNativeLevel ("+string.Join(", ",all.Select(x=>x.owner+"/"+x.PatchMethod?.Name))+")";
        return null;
    }
    static string ArmT1()
    {
        var f=CheckT1();if(f!=null)return f;
        var gate=SyncLockGetter();
        if(gate==null)return "~MobsSynchronization.Sync not created yet";
        SyncLock=gate;
        return null;
    }
    static bool BeforeIdentity(Entity __0,ref bool __result,out sbyte __state)
    {
        __state=-1;
        if(!OnGameThread()){Interlocked.Increment(ref T1.OffThread);return true;}
        var v=T1.C.V;v[T1Calls]++;
        if(!T1.Ready()){v[T1Waiting]++;return true;}
        if(__0==null){v[T1Null]++;return true;}
        try
        {
            bool timed=Stats&&R.ShouldShadow(IdentitySamples+1,ShadowAll);
            long t0=0,b0=0;if(timed){t0=Now();b0=Bytes();}
            bool decided,match=false;
            var gate=SyncLock;bool taken=false;
            try
            {
                Monitor.Enter(gate,ref taken);
                var level=__0._level;                         // IL_0016-0017: inside the lock, through the getter
                var current=CurrentLevel();                   // the owner prefix: currentLevel, then (if both set) ready
                bool ready=level!=null&&current!=null&&IdentityReady();
                decided=R.IdentityDecided(level!=null,current!=null,ready);
                if(decided)match=R.IdentityMatch((long)level.HashlinkPointer,(long)current.HashlinkPointer);
                else v[level==null?T1NoLevel:current==null?T1NoCurrent:T1NotReady]++;
            }
            finally{if(taken)Monitor.Exit(gate);}
            if(!decided)return true;
            if(R.ShouldShadow(++IdentitySamples,ShadowAll))
            {
                T1.Shadows++;__state=match?(sbyte)1:(sbyte)0;
                if(timed){T1FastTicks=Now()-t0;T1FastBytes=Bytes()-b0;T1OrigT0=Now();T1OrigB0=Bytes();}
                return true;
            }
            v[match?T1True:T1False]++;T1.Fast++;
            __result=match;return false;
        }
        catch(Exception e){T1.Fault(e);__state=-1;return true;}
    }
    static void AfterIdentity(bool __result,sbyte __state)
    {
        if(__state<0)return;
        try
        {
            if(Stats&&T1OrigT0!=0){T1.Time(T1FastTicks,T1FastBytes,Now()-T1OrigT0,Bytes()-T1OrigB0);T1OrigT0=0;}
            if((__state==1)!=__result)T1.Mismatch("IsEntityOnCurrentCombatIdentity fast="+(__state==1)+" original="+__result);
            else T1.Gate.Ok();
        }
        catch(Exception e){T1.Fault(e);}
    }

    // =====================================================================================================
    // Transpiler shared by T2 and T3 on IsAcquirablePlayerCombatTargetForMob (IL 120474, 156 bytes, pinned): replaces
    // the single `call IsHardInvalidPlayerTargetEntity` (IL_0016) by ShellRecheck and the single
    // `callvirt Entity::canBeHitBy` (IL_0046) by HeroCanBeHitBy; same stack shapes, everything else (try regions,
    // trace calls, branches) unchanged. Each helper runs the replaced instruction itself whenever its fast path is off.
    // =====================================================================================================
    static MethodInfo AcquirableMethod,HardInvalidMethod,CanBeHitByMethod;
    static void BindAcquirable()
    {
        if(AcquirableMethod!=null)return;
        var acquirable=SyncMethod("IsAcquirablePlayerCombatTargetForMob",typeof(bool),typeof(Mob),typeof(Entity),typeof(bool));
        Pin(acquirable,R.ModPins);
        HardInvalidMethod=SyncMethod("IsHardInvalidPlayerTargetEntity",typeof(bool),typeof(Entity));
        Pin(HardInvalidMethod,R.ModPins);
        CanBeHitByMethod=ProxyCall(typeof(Entity),"canBeHitBy",921,typeof(Entity));
        if(!CanBeHitByMethod.IsVirtual||CanBeHitByMethod.IsStatic)throw new InvalidOperationException("pinned Entity.canBeHitBy changed");
        BindHookManagers();
        AcquirableMethod=acquirable;
    }
    static IEnumerable<CodeInstruction> TranspileAcquirable(IEnumerable<CodeInstruction> instructions)
    {
        var list=instructions.ToList();
        int hard=R.SingleIndex(list.Select(i=>i.opcode==OpCodes.Call&&i.operand is MethodBase m&&Same(m,HardInvalidMethod)).ToList());
        int hit=R.SingleIndex(list.Select(i=>i.opcode==OpCodes.Callvirt&&i.operand is MethodBase m&&Same(m,CanBeHitByMethod)).ToList());
        if(hard<0||hit<0||hard>=hit)throw new InvalidOperationException("IsAcquirablePlayerCombatTargetForMob body changed");
        if(T2.Installed)list[hard].operand=Own(nameof(ShellRecheck));
        if(T3.Installed){list[hit].opcode=OpCodes.Call;list[hit].operand=Own(nameof(HeroCanBeHitBy));}
        return list;
    }

    // =====================================================================================================
    // T2. Skip the dominated second IsHardInvalidPlayerTargetEntity of a remote shell.
    //
    // IL_0016 is reached only through IL_0007 after IsPreservablePlayerCombatTargetForMob(mob, e) returned true, which
    // requires IsPreservablePlayerCombatTargetEntity(e) true, which requires IsHardInvalidPlayerTargetEntity(e) false in
    // this same invocation (IL 120374-120378, 120393, 120479); IsHardInvalid's catch returns true, so that false came from
    // its normal path. Between the two calls only Entity.isOpponent (HL f@759 + f@62, read-only) and
    // IsRemotePlayerCombatShell (managed, read-only) run, so the second call returns false: T2 returns false without it.
    // Armed while no hook manager exists for f@759 / f@62 and nothing but us patches IsAcquirable, isOpponent, the chain
    // IsPreservablePlayerCombatTargetForMob / IsRemotePlayerCombatShell / IsHardInvalidPlayerTargetEntity and everything
    // those can run (IsCorpseLike, IsKnownPlayer, IsEntityDownedForCombat with the downed state and GetCurrentLevelId,
    // IsEntityOnCurrentCombatIdentity with DoesLevelMatchCurrentIdentityLocked, GetLevelTraceIdSafe and
    // ComputeLevelIdentityToken, the destroyed / life / _targetable / _level / map getters), except CoopSyncFixes' pinned
    // MatchTrackedNativeLevel prefix and SaltBranchIdentity postfix (same value for the same level within the call).
    // Shadow: the real call runs; any true is a mismatch (that true is returned).
    // =====================================================================================================
    static readonly HostItem T2=new("T2","calls","skipped","notArmed"){Arm=ArmT2,PatchCheck=CheckT2};
    const int T2Calls=0,T2Skipped=1,T2Waiting=2;
    static Func<Entity,bool> HardInvalid;
    static Guard T2Chain;
    static readonly HookGuard T2Guard=new(){Functions=new[]{759,62}};
    static long ShellSamples;
    static void BindT2()
    {
        BindAcquirable();
        HardInvalid=OptCore.Delegate<Func<Entity,bool>>(HardInvalidMethod);
        var preservable=SyncMethod("IsPreservablePlayerCombatTargetForMob",typeof(bool),typeof(Mob),typeof(Entity));
        var shell=SyncMethod("IsRemotePlayerCombatShell",typeof(bool),typeof(Entity));
        var chain=new List<MethodBase>
        {
            preservable,shell,
            SyncMethod("IsPreservablePlayerCombatTargetEntity",typeof(bool),typeof(Entity)),
            SyncMethod("IsCorpseLikeCombatTargetEntity",typeof(bool),typeof(Entity)),
            SyncMethod("IsKnownPlayerEntity",typeof(bool),typeof(Entity)),
            SyncMethod("IsEntityOnCurrentCombatIdentity",typeof(bool),typeof(Entity)),
        };
        foreach(var m in chain)Pin(m,R.ModPins);
        Pin(OptCore.Method(typeof(ModEntry),"IsEntityDownedForCombat",true,typeof(bool),typeof(Entity)),R.EntryPins);
        T2Chain=NewGuard(new MethodBase[]{AcquirableMethod,ProxyCall(typeof(Entity),"isOpponent",759,typeof(Entity))},new MethodBase[]{HardInvalidMethod,preservable,shell});
        foreach(var m in chain)if(!T2Chain.Keys.Contains(Key(m)))throw new InvalidOperationException("T2 guard misses "+m.Name);
    }
    static string CheckT2()=>Foreign(T2Chain);
    static string ArmT2()=>CheckT2()??ArmGuard(T2Guard);
    // Replaces `call IsHardInvalidPlayerTargetEntity` at IL_0016.
    public static bool ShellRecheck(Entity e)
    {
        int mode=ShellMode();
        if(mode==0)return HardInvalid(e);
        if(mode==1)return false;
        long t0=Stats?Now():0,b0=Stats?Bytes():0;
        bool real=HardInvalid(e);                         // an exception leaves as from the replaced call
        try
        {
            if(Stats)T2.Time(0,0,Now()-t0,Bytes()-b0);
            if(!R.ShellRecheckAgrees(real))T2.Mismatch("IsHardInvalidPlayerTargetEntity returned true at IL_0016 for "+e?.GetType().Name);
            else T2.Gate.Ok();
        }
        catch{}
        return real;
    }
    // 0 = run the replaced call, 1 = skip it (false), 2 = shadow. Never throws.
    static int ShellMode()
    {
        try
        {
            if(!OnGameThread()){Interlocked.Increment(ref T2.OffThread);return 0;}
            var v=T2.C.V;v[T2Calls]++;
            if(!T2.Ready()||!GuardIntact(T2Guard,T2)){v[T2Waiting]++;return 0;}
            if(R.ShouldShadow(++ShellSamples,ShadowAll)){T2.Shadows++;return 2;}
            v[T2Skipped]++;T2.Fast++;
            return 1;
        }
        catch(Exception e){T2.Fault(e);return 0;}
    }

    // =====================================================================================================
    // T3 (= design D1). The local hero's canBeHitBy(mob) without the two hooked dispatches.
    //
    // Native path today: proxy dc.en.Hero.canBeHitBy -> f@2050 (hooked) -> HookEntry -> ModEntry.Hook_Hero_canBeHitBy
    // (orig, hero, mob) -> orig = native f@2050: f@921 (= this.canBeHit(), f@920; the mob argument is unused)
    // && !game.hasCinematic() && !game.paused with game = this._level.game, where hasCinematic is f@664 (hooked) ->
    // HookEntry -> ModEntry.Hook_Game_hasCinematic(orig, game) -> orig = native f@664: (curCine != null &&
    // !curCine.destroyed) || ModalPopUp.ME != null. All of these functions only read.
    // T3 invokes the two registered handler delegates themselves (the very objects in the hook managers, so the same
    // ModEntry instance and method), handing them managed stand-ins for f@2050 and f@664 that do the same reads in the
    // same order (canBeHit is the proxy call of f@920 itself). The result is the same as [handler -> native]; nothing is
    // remembered between calls. Taken only when, on every call: game thread; receiver of exact proxy type en.Hero and
    // the mod's local hero (ModEntry.me ?? Game.HeroInstance, as IsRemotePlayerCombatShell); each manager holds exactly
    // the mod's one handler and dispatches through its own entry delegate with no direct route (a Harmony patch on a
    // proxy method installs a processor there); no hook manager exists for f@920 / f@921; hero._level and its game are
    // set (else HL would raise NullCheck). Armed only while nothing patches the proxy members the stand-ins use,
    // IsAcquirable, the two handlers or anything they can run (IsEntityDownedForCombat with the downed state,
    // GetCurrentLevelId, IsModFakeDeathCine, the getters they read), and while the hook dispatcher HookEntry carries no
    // patch but CoopLoadFix's chain cache and CoopNetMeter's meter (checked every 2 s), with the pinned hlboot and framework
    // and a generated proxy that is the pinned file or passes the T3 symbolic check (Alpha2, VerifyT3Proxy below). Any
    // exception of the fast route falls back to the real call (the handlers only read; with no
    // foreign patch on them or their callees, running them again changes nothing).
    // Shadow: the fast route, then the real dispatched call; the real outcome (value or exception) is returned.
    // Dropped: two HookEntry dispatches (HookOrigClosure/HlCallback objects, GC handles, CoopLoadFix and CoopNetMeter
    // counters); the hook meter shows fewer en.Hero.canBeHitBy / pr.Game.hasCinematic dispatches.
    // =====================================================================================================
    static readonly HostItem T3=new("T3","calls","fast","notArmed","receiver","chain","noLevel","fastFault",
        "heroBlocked","canBeHitFalse","cineLive","modal","fakeDeathCine","paused","allTrue"){Arm=ArmT3,PatchCheck=CheckT3};
    const int T3Calls=0,T3Fast=1,T3Waiting=2,T3Receiver=3,T3Chain=4,T3NoLevel=5,T3FastFault=6,
        T3HeroBlocked=7,T3CanBeHitFalse=8,T3CineLive=9,T3Modal=10,T3FakeDeathCine=11,T3Paused=12,T3AllTrue=13;
    static MethodInfo HookHeroMethod,HookGameMethod;
    static Guard T3Proxy;
    static readonly HookGuard T3Guard=new(){Functions=new[]{920,921}};
    static List<Delegate> HeroHooks,GameHooks;
    static Delegate HeroHandler,GameHandler;
    static HlCallback HeroCallback,GameCallback;static Delegate HeroEntry,GameEntry;
    // The generated hook delegate types cannot be named from this add-on (the proxy assembly binds them to
    // System.Private.CoreLib): they are found by name, and two small compiled invokers call the handlers.
    static Type HookHeroType,OrigHeroType,HookGameType,OrigGameType;
    static Delegate HeroOrig,GameOrig;          // orig_canBeHitBy -> PortHero, orig_hasCinematic -> PortGame
    static Func<Delegate,Delegate,Hero,Entity,bool> InvokeHeroHandler;
    static Func<Delegate,Delegate,HlGame,bool> InvokeGameHandler;
    [ThreadStatic] static Hero CtxHero;[ThreadStatic] static HlGame CtxGame;[ThreadStatic] static bool HeroReached,GameReached;
    static long HitSamples;

    static void BindT3()
    {
        BindAcquirable();
        VerifyT3Proxy();
        if(typeof(Hero).BaseType!=typeof(Entity)||typeof(Hero).GetMethod("canBeHit",OptCore.I,null,Type.EmptyTypes,null)!=null)
            throw new InvalidOperationException("proxy en.Hero no longer inherits Entity.canBeHit");
        var members=new List<MethodBase>
        {
            ProxyCall(typeof(Hero),"canBeHitBy",2050,typeof(Entity)),
            CanBeHitByMethod,
            ProxyCall(typeof(Entity),"canBeHit",920),
            ProxyCall(typeof(HlGame),"hasCinematic",664),
        };
        ProxyGetter(typeof(Entity),"get__level","_level","GetFieldById",members);
        ProxyGetter(typeof(Level),"get_game","game","GetFieldById",members);
        ProxyGetter(typeof(HlGame),"get_curCine","curCine","GetFieldById",members);
        ProxyGetter(typeof(dc.libs.Process),"get_destroyed","destroyed","GetValueFieldById",members);
        ProxyGetter(typeof(dc.libs.Process),"get_paused","paused","GetValueFieldById",members);
        ProxyGetter(typeof(dc.ui._ModalPopUp),"get_ME","ME","GetFieldById",members);
        var popupClass=typeof(dc.ui.ModalPopUp).GetMethod("get_Class",OptCore.S,null,Type.EmptyTypes,null)??throw new MissingMethodException("ModalPopUp","get_Class");
        if(!R.GlobalGetterShape(popupClass.GetMethodBody()?.GetILAsByteArray(),2072))throw new InvalidOperationException("ModalPopUp.Class no longer reads global g@2072 (the one f@664 reads)");
        members.Add(popupClass);
        var proxy=typeof(Hero).Assembly;
        HookHeroType=proxy.GetType("dc.en.Hook_Hero+hook_canBeHitBy",true);OrigHeroType=proxy.GetType("dc.en.Hook_Hero+orig_canBeHitBy",true);
        HookGameType=proxy.GetType("dc.pr.Hook_Game+hook_hasCinematic",true);OrigGameType=proxy.GetType("dc.pr.Hook_Game+orig_hasCinematic",true);
        DelegateShape(OrigHeroType,typeof(Hero),typeof(Entity));DelegateShape(HookHeroType,OrigHeroType,typeof(Hero),typeof(Entity));
        DelegateShape(OrigGameType,typeof(HlGame));DelegateShape(HookGameType,OrigGameType,typeof(HlGame));
        HeroOrig=Delegate.CreateDelegate(OrigHeroType,Own(nameof(PortHero)));
        GameOrig=Delegate.CreateDelegate(OrigGameType,Own(nameof(PortGame)));
        InvokeHeroHandler=(Func<Delegate,Delegate,Hero,Entity,bool>)HandlerInvoker("CoopOpt_InvokeHeroHandler",HookHeroType,OrigHeroType,typeof(Hero),typeof(Entity)).CreateDelegate(typeof(Func<Delegate,Delegate,Hero,Entity,bool>));
        InvokeGameHandler=(Func<Delegate,Delegate,HlGame,bool>)HandlerInvoker("CoopOpt_InvokeGameHandler",HookGameType,OrigGameType,typeof(HlGame)).CreateDelegate(typeof(Func<Delegate,Delegate,HlGame,bool>));
        HookHeroMethod=OptCore.Method(typeof(ModEntry),"Hook_Hero_canBeHitBy",false,typeof(bool),OrigHeroType,typeof(Hero),typeof(Entity));
        HookGameMethod=OptCore.Method(typeof(ModEntry),"Hook_Game_hasCinematic",false,typeof(bool),OrigGameType,typeof(HlGame));
        Pin(HookHeroMethod,R.EntryPins);Pin(HookGameMethod,R.EntryPins);
        var fakeDeath=OptCore.Method(typeof(ModEntry),"IsModFakeDeathCine",true,typeof(bool),typeof(dc.GameCinematic));
        var downed=OptCore.Method(typeof(ModEntry),"IsEntityDownedForCombat",true,typeof(bool),typeof(Entity));
        Pin(fakeDeath,R.EntryPins);Pin(downed,R.EntryPins);
        members.Add(AcquirableMethod);
        T3Proxy=NewGuard(members,new MethodBase[]{HookHeroMethod,HookGameMethod});
        foreach(var m in new MethodBase[]{fakeDeath,downed})if(!T3Proxy.Keys.Contains(Key(m)))throw new InvalidOperationException("T3 guard misses "+m.Name);
    }
    // Alpha2 (unit U12, PLAN-ALPHA2.md issue 14; OptHostT3Proxy.cs): the GameProxy check of T3. Alpha1 required the raw
    // file 2A1803BA (PinFile), which a regenerated DCCM cache never matches, so T3 stayed off on every other PC. The raw
    // pin still decides first (same file: same route, no new line); any other file must pass the symbolic check of
    // exactly what T3 uses (generator pins, loaded identity, hero subclasses, T3 fingerprint), else T3 is not installed
    // with the reason. COOP_QA_OPT_T3_SYMBOLIC=1 (QA only) forces the symbolic route on the pinned file too; it can only
    // refuse more. The loaded hero subclasses are checked on every route: T3's receiver whitelist (HitMode) relies on
    // Beheaded / Richter declaring no canBeHitBy / canBeHit / canBeDetected of their own.
    static void VerifyT3Proxy()
    {
        var proxy=typeof(Hero).Assembly;
        if(string.IsNullOrEmpty(proxy.Location))throw new InvalidOperationException("pinned file changed: GameProxy (no file location)");
        foreach(var sub in new[]{typeof(dc.en.hero.Beheaded),typeof(dc.en.hero.Richter)})
        {
            if(sub.BaseType!=typeof(Hero))throw new InvalidOperationException("proxy "+sub.FullName+" no longer derives from en.Hero");
            foreach(var slot in OptHostT3Proxy.HeroSlots)
                if(sub.GetMember(slot,MemberTypes.Method,OptCore.I|BindingFlags.Static).Length!=0)throw new InvalidOperationException("proxy "+sub.FullName+" declares "+slot);
        }
        string raw=Hash(proxy.Location);
        bool qa=R.SwitchSet(Environment.GetEnvironmentVariable(OptHostT3Proxy.QaSymbolicSwitch));
        if(OptHostT3Proxy.RawAccepted(raw,GameProxyPin,qa))return;
        var o=OptHostT3Proxy.Verify(proxy,raw);      // throws with the reason when refused
        OptCore.Info("host T3 GameProxy accepted by its symbolic fingerprint: raw={Raw} symbolic={Symbolic} image={Image} members={Types}/{Methods}/{Fields} ms={Ms}{Qa}",
            o.Raw,o.Symbolic,o.Image,o.Types,o.Methods,o.Fields,o.Ms,qa?" (QA: "+OptHostT3Proxy.QaSymbolicSwitch+"=1"+(raw==GameProxyPin?", the raw pin matches too)":")"):"");
    }
    static void DelegateShape(Type d,params Type[] args)
    {
        var invoke=d.GetMethod("Invoke");
        if(!typeof(Delegate).IsAssignableFrom(d)||invoke==null||invoke.ReturnType!=typeof(bool)||!invoke.GetParameters().Select(p=>p.ParameterType).SequenceEqual(args))
            throw new InvalidOperationException("pinned hook delegate changed: "+d.FullName);
    }
    // bool (Delegate handler, Delegate orig, args...) => ((hook)handler).Invoke((orig)orig, args...)
    static DynamicMethod HandlerInvoker(string name,Type hook,Type orig,params Type[] args)
    {
        var dm=new DynamicMethod(name,typeof(bool),new[]{typeof(Delegate),typeof(Delegate)}.Concat(args).ToArray(),typeof(OptHost).Module,true);
        var il=dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Castclass,hook);
        il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Castclass,orig);
        for(int i=0;i<args.Length;i++)il.Emit(OpCodes.Ldarg,(short)(i+2));
        il.Emit(OpCodes.Callvirt,hook.GetMethod("Invoke"));il.Emit(OpCodes.Ret);
        return dm;
    }
    static string CheckT3()=>Foreign(T3Proxy)??HookEntryForeign();
    static string ArmT3()
    {
        var f=CheckT3();if(f!=null)return f;
        var managers=Managers();
        object heroKey=Entry(2050),gameKey=Entry(664);
        if(!managers.Contains(heroKey)||!managers.Contains(gameKey))return "~hook managers of en.Hero.canBeHitBy / pr.Game.hasCinematic not created yet";
        object hm=managers[heroKey],gm=managers[gameKey];
        if(((Hashlink.Reflection.Members.HashlinkFunction)MgrFunction.GetValue(hm)).FunctionIndex!=2050||((Hashlink.Reflection.Members.HashlinkFunction)MgrFunction.GetValue(gm)).FunctionIndex!=664)
            return "hook manager function index mismatch";
        var hh=(List<Delegate>)MgrHooks.GetValue(hm);var gh=(List<Delegate>)MgrHooks.GetValue(gm);
        if(hh.Count!=1||gh.Count!=1)return "~hook handlers en.Hero.canBeHitBy="+hh.Count+" pr.Game.hasCinematic="+gh.Count+" (exactly the mod's one each is required)";
        Delegate heroHandler=hh[0],gameHandler=gh[0];
        if(heroHandler==null||heroHandler.GetType()!=HookHeroType||!Same(heroHandler.Method,HookHeroMethod)||!(heroHandler.Target is ModEntry)||heroHandler.GetInvocationList().Length!=1)
            return "~the en.Hero.canBeHitBy handler is not the mod's";
        if(gameHandler==null||gameHandler.GetType()!=HookGameType||!Same(gameHandler.Method,HookGameMethod)||!(gameHandler.Target is ModEntry)||gameHandler.GetInvocationList().Length!=1)
            return "~the pr.Game.hasCinematic handler is not the mod's";
        var hcb=(HlCallback)MgrCallback.GetValue(hm);var gcb=(HlCallback)MgrCallback.GetValue(gm);
        Delegate he=hcb.Target,ge=gcb.Target;
        if(he==null||!ReferenceEquals(he.Target,hm)||he.Method?.Name!=R.AdaptName(2050)||ge==null||!ReferenceEquals(ge.Target,gm)||ge.Method?.Name!=R.AdaptName(664))
            return "the hooked functions are not dispatched through their own hook entry ("+he?.Method?.Name+", "+ge?.Method?.Name+")";
        if(hcb.RedirectTarget!=IntPtr.Zero||gcb.RedirectTarget!=IntPtr.Zero)return "~a hooked function is routed around its handlers";
        var g=ArmGuard(T3Guard);if(g!=null)return g;
        HeroHooks=hh;GameHooks=gh;HeroHandler=heroHandler;GameHandler=gameHandler;HeroCallback=hcb;GameCallback=gcb;HeroEntry=he;GameEntry=ge;
        return null;
    }
    static bool ChainIntact()
    {
        if(!GuardIntact(T3Guard,T3))return false;
        var hh=HeroHooks;var gh=GameHooks;
        return R.HandlerChainIntact(hh.Count,hh.Count==1&&ReferenceEquals(hh[0],HeroHandler),ReferenceEquals(HeroCallback.Target,HeroEntry),(long)HeroCallback.RedirectTarget)
            &&R.HandlerChainIntact(gh.Count,gh.Count==1&&ReferenceEquals(gh[0],GameHandler),ReferenceEquals(GameCallback.Target,GameEntry),(long)GameCallback.RedirectTarget);
    }

    // Replaces `callvirt Entity::canBeHitBy` at IL_0046 (inside the try region IL_002C-005F).
    public static bool HeroCanBeHitBy(Entity receiver,Entity mob)
    {
        int mode=HitMode(receiver,out var hero,out var game);
        if(mode==0)return receiver.canBeHitBy(mob);
        if(mode==1)
        {
            try{return FastHit(hero,game,mob);}
            catch(Exception e){T3.C.V[T3FastFault]++;T3.Fault(e);}
            return receiver.canBeHitBy(mob);
        }
        return ShadowHit(receiver,hero,game,mob);
    }
    // 0 = the virtual call, 1 = fast route, 2 = shadow. Never throws.
    static int HitMode(Entity receiver,out Hero hero,out HlGame game)
    {
        hero=null;game=null;
        try
        {
            if(!OnGameThread()){Interlocked.Increment(ref T3.OffThread);return 0;}
            var v=T3.C.V;v[T3Calls]++;
            if(!T3.Ready()){v[T3Waiting]++;return 0;}
            // The game never creates a plain en.Hero: the hero is en.hero.Beheaded (or Richter), the only New ops of hero
            // types being $Beheaded.create f@18786 and $Richter.create f@8063. Neither overrides vtable slot 95 canBeHitBy
            // or slot 94 canBeHit (bytecode), and neither proxy subclass declares canBeHitBy / canBeHit / canBeDetected
            // (pinned GameProxy 2A1803BA, DeclaredOnly reflection) - so their call lands on en.Hero f@2050 exactly like a
            // plain Hero. Exact whitelist (not `is Hero`): an unknown subclass keeps the virtual call.
            // Before this, every native call was a "receiver" reject (opt-shadow-02, opt-ab-on-01/02 host logs).
            var rt=receiver?.GetType();
            bool exact=rt!=null&&(rt==typeof(Hero)||rt==typeof(dc.en.hero.Beheaded)||rt==typeof(dc.en.hero.Richter));
            if(!R.ReceiverEligible(exact,exact&&ReferenceEquals(receiver,ModEntry.me??ModGame.Instance?.HeroInstance))){v[T3Receiver]++;return 0;}
            if(!ChainIntact()){v[T3Chain]++;return 0;}
            var h=(Hero)receiver;var g=h._level?.game;
            if(g==null){v[T3NoLevel]++;return 0;}
            hero=h;game=g;
            if(R.ShouldShadow(++HitSamples,ShadowAll)){T3.Shadows++;return 2;}
            v[T3Fast]++;T3.Fast++;
            return 1;
        }
        catch(Exception e){T3.Fault(e);hero=null;game=null;return 0;}
    }
    static bool FastHit(Hero hero,HlGame game,Entity mob)
    {
        CtxHero=hero;CtxGame=game;HeroReached=false;
        try
        {
            bool r=InvokeHeroHandler(HeroHandler,HeroOrig,hero,mob);
            if(!HeroReached)T3.C.V[T3HeroBlocked]++;       // the handler answered without the original (downed, dead, untargetable)
            return r;
        }
        finally{CtxHero=null;CtxGame=null;}
    }
    // Stand-in for native f@2050 (en.Hero.canBeHitBy), called by the mod's handler as its "orig".
    static bool PortHero(Hero self,Entity by)
    {
        HeroReached=true;
        var v=T3.C.V;
        if(!self.canBeHit()){v[T3CanBeHitFalse]++;return R.HeroCanBeHit(false,false,false);}    // ops 0-1: f@921 = CallThis canBeHit (f@920)
        // ops 2-5: _level.game with NullCheck. The same objects HitMode read for this call (nothing in between writes).
        var game=ReferenceEquals(self,CtxHero)?CtxGame:null;
        if(game==null)game=(self._level??throw new NullReferenceException("hero._level")).game??throw new NullReferenceException("level.game");
        GameReached=false;
        bool cine=InvokeGameHandler(GameHandler,GameOrig,game);                                 // op 6: Call1 f@664 (hooked)
        if(!cine&&!GameReached)v[T3FakeDeathCine]++;          // the handler answered false itself (live mod fake-death cine)
        if(cine)return R.HeroCanBeHit(true,true,false);                                          // op 7
        bool paused=game.paused;                                                                  // ops 8-13 (same _level.game)
        v[paused?T3Paused:T3AllTrue]++;
        return R.HeroCanBeHit(true,false,paused);
    }
    // Stand-in for native f@664 (pr.Game.hasCinematic), called by the mod's handler as its "orig".
    static bool PortGame(HlGame self)
    {
        GameReached=true;
        var cine=self.curCine;                                                                   // ops 0-5
        bool present=cine!=null,destroyed=present&&cine.destroyed;
        if(present&&!destroyed){T3.C.V[T3CineLive]++;return R.GameHasCinematic(true,false,false);}
        bool modal=dc.ui.ModalPopUp.Class.ME!=null;                                              // ops 6-12
        if(modal)T3.C.V[T3Modal]++;
        return R.GameHasCinematic(present,destroyed,modal);
    }
    static bool ShadowHit(Entity receiver,Hero hero,HlGame game,Entity mob)
    {
        long t0=0,b0=0,t1=0,b1=0;
        if(Stats){t0=Now();b0=Bytes();}
        bool fast=false,fastThrew=false;
        try{fast=FastHit(hero,game,mob);}catch{fastThrew=true;}
        if(Stats){t1=Now();b1=Bytes();}
        bool real=false;Exception realError=null;
        try{real=receiver.canBeHitBy(mob);}catch(Exception e){realError=e;}
        try
        {
            if(Stats)T3.Time(t1-t0,b1-b0,Now()-t1,Bytes()-b1);
            if(!R.OutcomesAgree(fastThrew,fast,realError!=null,real))
                T3.Mismatch("hero canBeHitBy fast="+(fastThrew?"threw":fast.ToString())+" real="+(realError!=null?realError.GetType().Name:real.ToString()));
            else T3.Gate.Ok();
        }
        catch{}
        if(realError!=null)ExceptionDispatchInfo.Capture(realError).Throw();
        return real;
    }
}
