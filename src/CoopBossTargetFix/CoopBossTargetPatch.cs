using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using Entity=dc.Entity;
using Mob=dc.en.Mob;
using Hero=dc.en.Hero;
using R=CoopBossTargetRules;

// CoopBossTargetFix (PLAN-ALPHA2.md issue 9, unit U3), host only. Hooks (all on the release 0.5.1 base mod, IL-pinned in
// CoopBossTargetRules.IlPins):
//   1. postfix on MobsSynchronization.TryClearHostMobInvalidPlayerTargets(Mob) (private static). It runs every frame for a
//      boss while a player is downed (TryMaintainHostBossSurvivorTarget, from Hook_Mob_preUpdate before the native update)
//      and once per tracked mob after each player combat-state change (TryRepairHostMobAfterPlayerCombatStateChange). When
//      an allowlisted boss (dc.en.mob.boss.Behemoth) still has a downed/destroyed/gone player as nemesis and no skill is
//      queued or charging, it calls the native mob.setNemesisTarget(S) on the base mod's own survivor choice S
//      (TryResolveDetectedHostCombatTarget, else ResolveClosestLivingHostCombatTarget(mob, 2304)). Native setNemesisTarget
//      accepts a living KingSkin, adds threat and sets aTarget, so the same frame's TryMaintain sees a valid aTarget and the
//      per-frame null/client flip (and its lockAiS(0.25) re-arm) stops. When nobody is downed any more and the original
//      player is back, it calls setNemesisTarget(original) and forgets the pair.
//   2. prefixes on GhostHero.DisposeKingRuntime(GhostKing), ModEntry.DisposeClientSlot(int,bool) and
//      ModEntry.DisposeClientSlotForSubLevelTransition(int,bool): before the base mod destroys the avatar the boss was
//      handed to (disconnect, sub-level transition, save purge, re-creation), the original player is restored as nemesis,
//      so a destroyed GhostKing is never left as the boss's nemesis.
//   3. a frame tick (IOnFrameUpdate): records whose boss died or left the hero's level are forgotten; a record whose
//      survivor is gone or whose original player is back is restored even when no base-mod call reaches the postfix
//      (TryRepair skips culled mobs).
// Every body runs in try/catch behind a fault gate (8 consecutive / 64 total faults switch the part off for the
// session); every native call happens on the game thread only; log lines are rate-limited.
public static class CoopBossTargetPatch
{
    const BindingFlags S=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.DeclaredOnly;
    const BindingFlags I=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly;
    delegate bool TryResolveTarget(Mob mob,out Entity target);
    sealed class Record
    {
        public Mob Boss;public Entity Original;public Entity HandedTo;public long SinceTicks;public string OriginalName,HandedToName;
    }

    static readonly object InstallSync=new();
    static volatile bool Published;
    static Harmony Owner;
    static readonly CoopBossTargetFaultGate Gate=new();
    // One limiter per event kind, so a rare give-back line is never hidden behind a hand-over line of the same second.
    static readonly CoopBossTargetLogLimiter HandOverLog=new(Stopwatch.Frequency/2),RestoreLog=new(Stopwatch.Frequency/2),DropLog=new(Stopwatch.Frequency/2),
        DeferLog=new(Stopwatch.Frequency*5),RefuseLog=new(Stopwatch.Frequency*5);
    static readonly List<Record> Records=new();
    static int GameThread;
    static Func<Mob,bool> SkillBusyFn;
    static Func<Entity,bool> KnownPlayerFn,HardInvalidFn,DownedFn;
    static Func<bool> AnyDownedFn;
    static Func<NetNode,bool> IsHostFn;
    static Func<NetNode> NetRefFn;
    static TryResolveTarget DetectedFn;
    static Func<Mob,double,Entity> ClosestFn;
    static MethodBase[] Targets=Array.Empty<MethodBase>();

    // Read by the unit probe (CoopBossTargetProbe "boss-state") and by the logs.
    public static bool Ready=>Published;
    public static bool Disabled{get;private set;}
    public static string InitializationError{get;private set;}
    public static string ProxyPinMode{get;private set;}
    public static long HandOvers,Restores,Forgotten,Deferred,Refused,OffThread,PostfixCalls,DisposeCalls,TickRestores;
    public static long Faults=>Gate.Faults;
    public static bool Tripped=>Gate.Tripped;
    public static int RecordCount=>Records.Count;
    public static string LastDecision{get;private set;}="";

    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);
    static MethodInfo Method(Type type,string name,bool isStatic,Type ret,params Type[] args)
    {
        var m=type.GetMethod(name,isStatic?S:I,null,args,null);
        if(m==null||m.IsStatic!=isStatic||(ret!=null&&m.ReturnType!=ret))throw new InvalidOperationException("Pinned method mismatch: "+type.Name+"."+name);
        return m;
    }
    static MethodInfo Unique(Type type,string name)
    {
        var all=type.GetMethods(S|I).Where(m=>m.Name==name).ToArray();
        if(all.Length!=1)throw new InvalidOperationException("Pinned method not unique: "+type.Name+"."+name+" x"+all.Length);
        return all[0];
    }
    static T Bind<T>(MethodInfo m) where T:Delegate=>(T)m.CreateDelegate(typeof(T));

    // CoopBossTargetFix.Initialize. Throws (and leaves nothing patched) when the part cannot install.
    public static void Install()
    {
        lock(InstallSync)
        {
            if(Published)return;
            if(InitializationError!=null)throw new InvalidOperationException("Earlier boss target initialization failed: "+InitializationError);
            Disabled=R.SwitchSet(Environment.GetEnvironmentVariable(R.KillSwitch));
            if(Disabled)return;
            Harmony harmony=null;var patched=new List<MethodBase>();
            try
            {
                var mod=typeof(ModEntry).Assembly;
                if(mod.GetName().Version!=new Version(0,5,1,0)||Hash(mod.Location)!=R.BasePin)
                    throw new InvalidOperationException("CoopBossTargetFix requires DeadCellsMultiplayerMod 0.5.1 (pinned release 378BE6B0)");
                var boot=Path.Combine(Path.GetDirectoryName(typeof(Hero).Assembly.Location)??"","hlboot.dat");
                if(Hash(boot)!=R.NativePin)throw new InvalidOperationException("CoopBossTargetFix requires the pinned native game build (hlboot A37869A3)");
                ProxyPinMode=CoopBossTargetProxyCompatibility.ValidateLoaded(typeof(Hero).Assembly);
                var sync=mod.GetType(R.MobsSync,true);var entry=typeof(ModEntry);var ghost=mod.GetType(R.Ghost,true);var lobby=mod.GetType(R.Lobby,true);
                var king=mod.GetType("DeadCellsMultiplayerMod.Ghost.GhostBase.GhostKing",true);
                if(!typeof(dc.en.KingSkin).IsAssignableFrom(king))throw new InvalidOperationException("GhostKing is no longer a KingSkin");
                var entityRef=typeof(Entity).MakeByRefType();
                var clear=Method(sync,"TryClearHostMobInvalidPlayerTargets",true,typeof(bool),typeof(Mob));
                var disposeKing=Method(ghost,"DisposeKingRuntime",true,typeof(void),king);
                var disposeSlot=Method(entry,"DisposeClientSlot",false,typeof(void),typeof(int),typeof(bool));
                var disposeSlotSub=Method(entry,"DisposeClientSlotForSubLevelTransition",false,typeof(void),typeof(int),typeof(bool));
                var called=new Dictionary<string,MethodInfo>(StringComparer.Ordinal){
                    ["TryResolveDetectedHostCombatTarget"]=Method(sync,"TryResolveDetectedHostCombatTarget",true,typeof(bool),typeof(Mob),entityRef),
                    ["ResolveClosestLivingHostCombatTarget"]=Method(sync,"ResolveClosestLivingHostCombatTarget",true,typeof(Entity),typeof(Mob),typeof(double)),
                    ["HasLocalQueuedOrChargingSkill"]=Method(sync,"HasLocalQueuedOrChargingSkill",true,typeof(bool),typeof(Mob)),
                    ["IsKnownPlayerEntity"]=Method(sync,"IsKnownPlayerEntity",true,typeof(bool),typeof(Entity)),
                    ["IsHardInvalidPlayerTargetEntity"]=Method(sync,"IsHardInvalidPlayerTargetEntity",true,typeof(bool),typeof(Entity)),
                    ["IsHost"]=Method(sync,"IsHost",true,typeof(bool),typeof(NetNode)),
                    ["IsEntityDownedForCombat"]=Method(entry,"IsEntityDownedForCombat",true,typeof(bool),typeof(Entity)),
                    ["HasAnyPlayerDownedForCombat"]=Method(entry,"HasAnyPlayerDownedForCombat",true,typeof(bool)),
                    ["get_NetRef"]=Method(lobby,"get_NetRef",true,typeof(NetNode)),
                };
                // Every pinned body: patched, called and relied-on methods (the relied-on ones resolved by unique name).
                foreach(var pin in R.IlPins)
                {
                    MethodInfo m=pin.Method switch{
                        "TryClearHostMobInvalidPlayerTargets"=>clear,"DisposeKingRuntime"=>disposeKing,"DisposeClientSlot"=>disposeSlot,
                        "DisposeClientSlotForSubLevelTransition"=>disposeSlotSub,
                        _=>called.TryGetValue(pin.Method,out var c)?c:Unique(mod.GetType(pin.Type,true),pin.Method)};
                    if(m.DeclaringType?.FullName!=pin.Type)throw new InvalidOperationException("Pinned method moved: "+pin.Method);
                    if(!R.PinMatches(m.GetMethodBody()?.GetILAsByteArray(),pin.Length,pin.Sha))throw new InvalidOperationException("Pinned IL changed: "+pin.Type+"."+pin.Method);
                }
                SkillBusyFn=Bind<Func<Mob,bool>>(called["HasLocalQueuedOrChargingSkill"]);
                KnownPlayerFn=Bind<Func<Entity,bool>>(called["IsKnownPlayerEntity"]);
                HardInvalidFn=Bind<Func<Entity,bool>>(called["IsHardInvalidPlayerTargetEntity"]);
                DownedFn=Bind<Func<Entity,bool>>(called["IsEntityDownedForCombat"]);
                AnyDownedFn=Bind<Func<bool>>(called["HasAnyPlayerDownedForCombat"]);
                IsHostFn=Bind<Func<NetNode,bool>>(called["IsHost"]);
                NetRefFn=Bind<Func<NetNode>>(called["get_NetRef"]);
                DetectedFn=Bind<TryResolveTarget>(called["TryResolveDetectedHostCombatTarget"]);
                ClosestFn=Bind<Func<Mob,double,Entity>>(called["ResolveClosestLivingHostCombatTarget"]);
                harmony=new Harmony(R.HarmonyId);
                HarmonyMethod Own(string name)=>new(typeof(CoopBossTargetPatch).GetMethod(name,S)??throw new MissingMethodException("CoopBossTargetPatch",name));
                patched.Add(clear);harmony.Patch(clear,postfix:Own(nameof(AfterClear)));
                patched.Add(disposeKing);harmony.Patch(disposeKing,prefix:Own(nameof(BeforeDisposeKing)));
                patched.Add(disposeSlot);harmony.Patch(disposeSlot,prefix:Own(nameof(BeforeDisposeSlot)));
                patched.Add(disposeSlotSub);harmony.Patch(disposeSlotSub,prefix:Own(nameof(BeforeDisposeSlot)));
                foreach(var t in patched)
                {
                    var info=Harmony.GetPatchInfo(t)??throw new InvalidOperationException("Patch missing: "+t.Name);
                    int pre=info.Prefixes.Count(p=>p.owner==R.HarmonyId),post=info.Postfixes.Count(p=>p.owner==R.HarmonyId);
                    bool expectPost=ReferenceEquals(t,clear);
                    if(pre!=(expectPost?0:1)||post!=(expectPost?1:0)||info.Transpilers.Any(p=>p.owner==R.HarmonyId)||info.Finalizers.Any(p=>p.owner==R.HarmonyId))
                        throw new InvalidOperationException("Patch not installed exactly once: "+t.Name);
                }
                Owner=harmony;Targets=patched.ToArray();Published=true;
            }
            catch(Exception error)
            {
                Published=false;InitializationError=error.GetBaseException().Message;
                if(harmony!=null)foreach(var t in patched)try{harmony.Unpatch(t,HarmonyPatchType.All,R.HarmonyId);}
                    catch(Exception rollback){InitializationError+="; rollback of "+t.Name+": "+rollback.Message;}
                throw;
            }
        }
    }
    public static int PatchedTargets=>Targets.Length;

    // ---- helpers (game thread) ----
    // DCCM runs the game loop on the thread that initialised its core (ModCore.Core.MainThread), read on first use as
    // CoopOpt does (OptHost.cs ResolveGameThread). Native calls happen on that thread only.
    static bool OnGameThread()
    {
        int g=GameThread;
        if(g==0)g=ResolveGameThread();
        if(g>0&&Environment.CurrentManagedThreadId==g)return true;
        Interlocked.Increment(ref OffThread);return false;
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
                GameThread=-1;
                try{Serilog.Log.Warning(R.LogPrefix+"Game thread check failed ({Error}); the hand-over stays inactive",e.GetBaseException().Message);}catch{}
            }
            return GameThread;
        }
    }
    static bool IsHostNow(){var net=NetRefFn();return net!=null&&IsHostFn(net);}
    static Record Find(Mob mob){foreach(var r in Records)if(ReferenceEquals(r.Boss,mob))return r;return null;}
    static IntPtr LevelOf(Entity e){var l=e?._level;return l==null?IntPtr.Zero:l.HashlinkPointer;}
    static bool SameLevel(Entity a,Entity b){var la=LevelOf(a);return la!=IntPtr.Zero&&la==LevelOf(b);}
    static bool IsCurrentClient(Entity e)
    {
        var clients=ModEntry.clients;
        if(clients!=null)for(int i=0;i<clients.Length;i++)if(clients[i]!=null&&ReferenceEquals(clients[i],e))return true;
        return false;
    }
    static bool Gone(Entity e)=>e==null||e.destroyed||!(ReferenceEquals(e,ModEntry.me)||IsCurrentClient(e));
    static string Name(Entity e)
    {
        if(e==null)return "none";
        if(ReferenceEquals(e,ModEntry.me))return "host";
        var clients=ModEntry.clients;
        if(clients!=null)for(int i=0;i<clients.Length;i++)if(clients[i]!=null&&ReferenceEquals(clients[i],e))return "client#"+i+"(id="+ModEntry.clientIds[i]+")";
        return e.GetType().Name;
    }
    static void Info(CoopBossTargetLogLimiter limiter,string template,params object[] args)
    {
        try
        {
            if(!limiter.Allow(Stopwatch.GetTimestamp(),out long suppressed))return;
            Serilog.Log.Information(R.LogPrefix+template+(suppressed>0?" (+"+suppressed+" similar suppressed)":""),args);
        }catch{}
    }
    static void Fault(string where,Exception error)
    {
        bool tripped=Gate.Fail();
        try
        {
            if(Gate.Faults==1)Serilog.Log.Warning(R.LogPrefix+"Fault in {Where}; original behaviour kept for this call: {Error}",where,error.GetBaseException().Message);
            if(tripped)Serilog.Log.Warning(R.LogPrefix+"Boss nemesis hand-over switched off for this session after {Faults} faults (last in {Where}: {Error})",Gate.Faults,where,error.GetBaseException().Message);
        }catch{}
    }

    // ---- 1. postfix on TryClearHostMobInvalidPlayerTargets ----
    static void AfterClear(Mob __0)
    {
        if(!Published||Gate.Tripped)return;
        try
        {
            var mob=__0;
            // The allowlist first: every other mob (TryRepair passes all tracked mobs) returns here.
            if(mob==null||!R.IsAllowlisted(mob.GetType().FullName))return;
            if(!OnGameThread())return;
            Interlocked.Increment(ref PostfixCalls);
            Evaluate(mob,false);
            Gate.Ok();
        }
        catch(Exception error){Fault("postfix",error);}
    }

    static void Evaluate(Mob mob,bool fromTick)
    {
        var rec=Find(mob);
        var v=new R.View{Allowlisted=R.IsAllowlisted(mob.GetType().FullName),IsHost=IsHostNow(),HasRecord=rec!=null};
        if(!v.IsHost||!v.Allowlisted)return;
        v.MobAlive=!mob.destroyed&&mob.life>0;
        Entity nemesis=null,survivor=null;
        if(v.MobAlive)
        {
            nemesis=mob.nemesisTarget;
            if(nemesis!=null&&KnownPlayerFn(nemesis))
            {
                v.NemesisIsPlayer=true;
                v.NemesisInvalid=HardInvalidFn(nemesis);
                v.NemesisGone=Gone(nemesis);
            }
            if(rec!=null)
            {
                v.NemesisIsHandedTo=nemesis!=null&&ReferenceEquals(nemesis,rec.HandedTo);
                var o=rec.Original;
                v.OriginalAlive=o!=null&&!o.destroyed&&o.life>0;
                v.OriginalDowned=o!=null&&DownedFn(o);
                v.OriginalSameLevel=o!=null&&SameLevel(o,mob);
            }
            v.AnyDowned=AnyDownedFn();
            if(!fromTick&&v.NemesisIsPlayer&&v.NemesisInvalid)
            {
                v.SkillBusy=SkillBusyFn(mob);
                if(!v.SkillBusy)
                {
                    if(!DetectedFn(mob,out survivor)||survivor==null)survivor=ClosestFn(mob,R.SurvivorRangePx);
                    v.HasSurvivor=survivor!=null;
                    v.SurvivorIsNemesis=survivor!=null&&ReferenceEquals(survivor,nemesis);
                }
            }
        }
        var act=R.Decide(v);
        if(fromTick)act=R.TickAct(act);
        switch(act)
        {
            case R.Act.HandOver:HandOver(mob,rec,nemesis,survivor);break;
            case R.Act.Restore:Restore(rec,v.NemesisGone?"survivor-gone":"revived",fromTick);break;
            case R.Act.Forget:Drop(rec,v.MobAlive?"nemesis-changed-elsewhere":"boss-dead");break;
            case R.Act.Defer:
                Interlocked.Increment(ref Deferred);
                Info(DeferLog,"Hand-over deferred: boss {Boss} has a queued or charging skill; nemesis={Nemesis}",mob.GetType().Name,Name(nemesis));
                break;
        }
    }

    static void HandOver(Mob mob,Record rec,Entity from,Entity to)
    {
        string fromName=Name(from),toName=Name(to);
        mob.setNemesisTarget(to);   // virtual: Behemoth's own override (f@11646), as the game's intro calls it
        if(!ReferenceEquals(mob.nemesisTarget,to))
        {
            Interlocked.Increment(ref Refused);
            LastDecision="refused hand-over "+fromName+"->"+toName;
            Info(RefuseLog,"Hand-over refused by the game: boss {Boss} nemesis stays {From} (survivor {To})",mob.GetType().Name,fromName,toName);
            return;
        }
        switch(R.AfterHandOver(rec!=null,rec!=null&&ReferenceEquals(to,rec.Original)))
        {
            case R.RecordOp.Create:
                if(Records.Count>=R.MaxRecords)Records.RemoveAt(0);
                Records.Add(new Record{Boss=mob,Original=from,HandedTo=to,SinceTicks=Stopwatch.GetTimestamp(),OriginalName=fromName,HandedToName=toName});
                break;
            case R.RecordOp.Update:rec.HandedTo=to;rec.HandedToName=toName;break;
            case R.RecordOp.Drop:Records.Remove(rec);break;
        }
        Interlocked.Increment(ref HandOvers);
        LastDecision="hand-over "+fromName+"->"+toName;
        Info(HandOverLog,"Boss nemesis handed over: boss={Boss} from={From} to={To} records={Records}",mob.GetType().Name,fromName,toName,Records.Count);
    }

    static void Restore(Record rec,string reason,bool fromTick)
    {
        var mob=rec.Boss;var to=rec.Original;string toName=Name(to),fromName=Name(mob.nemesisTarget);
        Records.Remove(rec);
        mob.setNemesisTarget(to);
        bool ok=ReferenceEquals(mob.nemesisTarget,to);
        if(ok){Interlocked.Increment(ref Restores);if(fromTick)Interlocked.Increment(ref TickRestores);}
        else Interlocked.Increment(ref Refused);
        double held=(Stopwatch.GetTimestamp()-rec.SinceTicks)/(double)Stopwatch.Frequency;
        LastDecision=(ok?"restore ":"refused restore ")+fromName+"->"+toName+" ("+reason+")";
        Info(RestoreLog,"Boss nemesis given back: boss={Boss} from={From} to={To} reason={Reason} ok={Ok} heldSeconds={Held:0.0}",mob.GetType().Name,fromName,toName,reason,ok,held);
    }

    static void Drop(Record rec,string reason)
    {
        if(rec==null||!Records.Remove(rec))return;
        Interlocked.Increment(ref Forgotten);
        LastDecision="forget ("+reason+")";
        Info(DropLog,"Boss nemesis record dropped: original={Original} handedTo={HandedTo} reason={Reason}",rec.OriginalName,rec.HandedToName,reason);
    }

    // ---- 2. prefixes on the avatar disposers ----
    static void BeforeDisposeKing(DeadCellsMultiplayerMod.Ghost.GhostBase.GhostKing __0){HandleDisposal(__0,"avatar-dispose");}
    static void BeforeDisposeSlot(int __0)
    {
        if(!Published||Gate.Tripped||Records.Count==0)return;
        var clients=ModEntry.clients;
        if(clients!=null&&__0>=0&&__0<clients.Length)HandleDisposal(clients[__0],"client-slot-dispose");
    }
    static void HandleDisposal(Entity king,string why)
    {
        if(!Published||Gate.Tripped||king==null||Records.Count==0)return;
        try
        {
            if(!OnGameThread())return;
            Interlocked.Increment(ref DisposeCalls);
            for(int i=Records.Count-1;i>=0;i--)
            {
                if(i>=Records.Count)continue;
                var rec=Records[i];
                var mob=rec.Boss;bool alive=mob!=null&&!mob.destroyed&&mob.life>0;
                bool nemesisIsK=alive&&ReferenceEquals(mob.nemesisTarget,king);
                var o=rec.Original;bool usable=alive&&o!=null&&!o.destroyed&&o.life>0&&SameLevel(o,mob);
                switch(R.DisposeAct(ReferenceEquals(rec.HandedTo,king),alive,nemesisIsK,usable))
                {
                    case R.Act.Restore:Restore(rec,why,false);break;
                    case R.Act.Forget:Drop(rec,why);break;
                }
            }
            Gate.Ok();
        }
        catch(Exception error){Fault("dispose",error);}
    }

    // ---- 3. frame tick (CoopBossTargetFix.OnFrameUpdate) ----
    public static void Tick()
    {
        if(!Published||Gate.Tripped||Records.Count==0)return;
        try
        {
            if(!OnGameThread())return;
            var me=ModEntry.me;
            for(int i=Records.Count-1;i>=0;i--)
            {
                if(i>=Records.Count)continue;
                var rec=Records[i];var mob=rec.Boss;
                if(mob==null||mob.destroyed||me==null||!SameLevel(mob,me)){Drop(rec,"boss-gone-or-level-changed");continue;}
                Evaluate(mob,true);
            }
            Gate.Ok();
        }
        catch(Exception error){Fault("tick",error);}
    }
}
