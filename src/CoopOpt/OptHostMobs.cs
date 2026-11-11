using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using HaxeProxy.Runtime.Internals.Cache;
using R=OptHostRules;
using Entity=dc.Entity;
using Mob=dc.en.Mob;
using Level=dc.pr.Level;
using Team=dc.tool.Team;

// A1 and A3 of OptHost (see OptHost.cs).
public static partial class OptHost
{
    // =====================================================================================================
    // A1. IsMobHostileToPlayers / IsSyncMob with raw pointer reads.
    //
    // Release IL: IsMobHostileToPlayers(m) = m==null ? false : try{ l=m._level; t=m._team; l!=null && t!=null && t==l.teamMob }
    // catch false (IL 119882). IsSyncMob(m) = !EnableMobsSync ? false : m==null ? false : try{ m.destroyed || m._level==null
    // ? false : IsMobHostileToPlayers(m) ? true : IsSyncMobByType(m) } catch false (IL 142564).
    // The three object reads become reads of the object's pointer slots. The offsets are the ones the generated getters
    // themselves read: taken from their own ObjFieldInfoCache (the getter IL is checked to use exactly that cache), used
    // only when the cache is filled, the field is an object pointer (HOBJ) and the offset is plausible; until then the
    // original runs. There is one proxy per HL object, so the original's reference comparison of two proxies held on the
    // stack is the comparison of their pointers. Everything else (EnableMobsSync first, destroyed through its getter,
    // IsSyncMobByType, the catch-all) is as in the release. Every slot is compared with the proxy getter's object before
    // any raw pointer is followed: on every call until each of the three slots was confirmed once with a non-null pointer
    // equal to the getter's, then on every shadow call. Dropped side effect: the getters may create a wrapper/handle for an
    // object that has none yet (not game state; changes CoopNetMeter's handle count). On a shadow call EnableMobsSync is
    // read twice (fast, then original); after its first normalisation the getter only locks and returns.
    // Guard: IsMobHostileToPlayers and IsSyncMob with everything they can run (the three getters, destroyed,
    // EnableMobsSync and its normalisation, IsSyncMobByType and its factory) carry no foreign patch.
    // =====================================================================================================
    static readonly HostItem A1=new("A1","hostileCalls","hostileFast","syncCalls","syncFast","notArmed","nullMob","bypass"){Arm=ArmA1,PatchCheck=CheckA1};
    const int A1HostileCalls=0,A1HostileFast=1,A1SyncCalls=2,A1SyncFast=3,A1Waiting=4,A1Null=5,A1Bypass=6;
    static MethodInfo HostileMethod,SyncMobMethod,ByTypeMethod,EnableMobsSyncGetter;
    static Func<Mob,bool> SyncMobByType,OriginalHostile,OriginalSyncMob;
    static FieldInfo CacheLevel,CacheTeam,CacheTeamMob;
    static int OffLevel,OffTeam,OffTeamMob;
    static bool LevelSlotConfirmed,TeamSlotConfirmed,TeamMobSlotConfirmed;
    static long HostileSamples,SyncSamples;
    static Guard A1Guard;
    [ThreadStatic] static int Bypass;          // >0 while a shadow runs the original: inner calls take the original path
    // The proxy reads the slots are compared with (shadow calls only): the very getters the original calls.
    static Func<Mob,Level> ReadLevel=m=>m._level;
    static Func<Mob,Team> ReadTeam=m=>m._team;
    static Func<Level,Team> ReadTeamMob=l=>l.teamMob;

    [MethodImpl(MethodImplOptions.NoInlining)] static bool OriginalHostileStub(Mob mob)=>throw new NotImplementedException("reverse patch target");
    [MethodImpl(MethodImplOptions.NoInlining)] static bool OriginalSyncMobStub(Mob mob)=>throw new NotImplementedException("reverse patch target");

    static void BindA1()
    {
        HostileMethod=SyncMethod("IsMobHostileToPlayers",typeof(bool),typeof(Mob));
        SyncMobMethod=SyncMethod("IsSyncMob",typeof(bool),typeof(Mob));
        ByTypeMethod=SyncMethod("IsSyncMobByType",typeof(bool),typeof(Mob));
        foreach(var m in new[]{HostileMethod,SyncMobMethod,ByTypeMethod})Pin(m,R.ModPins);
        EnableMobsSyncGetter=typeof(MultiplayerSettingsStorage).GetProperty("EnableMobsSync",BindingFlags.Public|BindingFlags.Static)?.GetMethod;
        if(EnableMobsSyncGetter==null||EnableMobsSyncGetter.ReturnType!=typeof(bool))throw new InvalidOperationException("pinned EnableMobsSync changed");
        SyncMobByType=OptCore.Delegate<Func<Mob,bool>>(ByTypeMethod);
        // The getters the original calls, and the caches whose offsets they read.
        CacheLevel=ProxyGetter(typeof(Entity),"get__level","_level","GetFieldById");
        CacheTeam=ProxyGetter(typeof(Entity),"get__team","_team","GetFieldById");
        CacheTeamMob=ProxyGetter(typeof(Level),"get_teamMob","teamMob","GetFieldById");
        ProxyGetter(typeof(Entity),"get_destroyed","destroyed","GetValueFieldById");
        A1Guard=NewGuard(null,new MethodBase[]{HostileMethod,SyncMobMethod});
    }
    // End-init: private copies of the two release bodies, for the shadow comparison (compiled after every module's
    // Initialize, so they see the same callee patches as the originals).
    static void PrepareA1()
    {
        var hostileCopy=Harmony.ReversePatch(HostileMethod,new HarmonyMethod(Own(nameof(OriginalHostileStub))));
        var syncCopy=Harmony.ReversePatch(SyncMobMethod,new HarmonyMethod(Own(nameof(OriginalSyncMobStub))));
        OriginalHostile=OptCore.Delegate<Func<Mob,bool>>(hostileCopy);
        OriginalSyncMob=OptCore.Delegate<Func<Mob,bool>>(syncCopy);
    }
    static string CheckA1()=>Foreign(A1Guard);
    static string ArmA1()
    {
        var f=CheckA1();if(f!=null)return f;
        var r=ReadOffset(CacheLevel,out int level);if(r!=null)return r;
        r=ReadOffset(CacheTeam,out int team);if(r!=null)return r;
        r=ReadOffset(CacheTeamMob,out int teamMob);if(r!=null)return r;
        OffLevel=level;OffTeam=team;OffTeamMob=teamMob;
        OptCore.Info("host A1 offsets _level={Level} _team={Team} teamMob={TeamMob} ({LevelType}, {TeamType})",level,team,teamMob,FieldTypeName(CacheLevel),FieldTypeName(CacheTeamMob));
        return null;
    }
    static string FieldTypeName(FieldInfo f){try{return ((ObjFieldInfoCache)f.GetValue(null)).field?.Name??"?";}catch{return "?";}}
    // "~..." while the getter has not filled its cache yet (the original fills it); an unusable filled cache never changes.
    static string ReadOffset(FieldInfo cache,out int offset)
    {
        offset=0;
        var c=(ObjFieldInfoCache)cache.GetValue(null);
        if(!c.hasCache)return "~proxy field cache "+cache.DeclaringType?.Name+"."+cache.Name+" not filled yet";
        bool obj=c.field!=null&&c.field.TypeKind==Hashlink.TypeKind.HOBJ;
        if(!R.OffsetUsable(c.hasCache,(long)c.offset,obj,IntPtr.Size))return "unusable proxy field cache "+cache.Name+" (offset "+(long)c.offset+", object "+obj+")";
        offset=(int)c.offset;
        return null;
    }
    static IntPtr Slot(IntPtr obj,int offset)=>Marshal.ReadIntPtr(obj,offset);
    static bool SlotsConfirmed=>LevelSlotConfirmed&&TeamSlotConfirmed&&TeamMobSlotConfirmed;
    static IntPtr Ptr(HaxeProxy.Runtime.HaxeProxyBase o)=>o==null?IntPtr.Zero:o.HashlinkPointer;
    // IsMobHostileToPlayers from the slots of the mob at p: _level, then _team, then (both set) level.teamMob.
    static bool RawHostile(IntPtr p)=>RawHostileAt(p,Slot(p,OffLevel));
    static bool RawHostileAt(IntPtr p,IntPtr level)
    {
        IntPtr team=Slot(p,OffTeam);
        return R.NeedsTeamMob((long)level,(long)team)&&R.Hostile((long)level,(long)team,(long)Slot(level,OffTeamMob));
    }
    // IsSyncMob's verdict inside its try region once destroyed is known: destroyed or no _level -> false; hostile -> true;
    // else by type. _level is not read for a destroyed mob (the original's || stops there).
    static R.SyncVerdict SyncFromSlots(bool destroyed,IntPtr p)
    {
        IntPtr level=destroyed?IntPtr.Zero:Slot(p,OffLevel);
        bool hostile=!destroyed&&level!=IntPtr.Zero&&RawHostileAt(p,level);
        return R.SyncMob(destroyed,(long)level,hostile);
    }
    // The slots against the proxy getters, before any slot value is followed. Null when they agree.
    static string CheckSlots(Mob mob,IntPtr p)
    {
        IntPtr lp=Slot(p,OffLevel),tp=Slot(p,OffTeam);
        var level=ReadLevel(mob);var team=ReadTeam(mob);
        IntPtr lg=Ptr(level),tg=Ptr(team);
        if(lp!=lg)return "_level slot 0x"+((long)lp).ToString("X")+" vs getter 0x"+((long)lg).ToString("X");
        if(tp!=tg)return "_team slot 0x"+((long)tp).ToString("X")+" vs getter 0x"+((long)tg).ToString("X");
        if(R.SlotConfirms((long)lp,(long)lg))LevelSlotConfirmed=true;
        if(R.SlotConfirms((long)tp,(long)tg))TeamSlotConfirmed=true;
        if(R.NeedsTeamMob((long)lp,(long)tp))
        {
            IntPtr mp=Slot(lp,OffTeamMob),mg=Ptr(ReadTeamMob(level));
            if(mp!=mg)return "teamMob slot 0x"+((long)mp).ToString("X")+" vs getter 0x"+((long)mg).ToString("X");
            if(R.SlotConfirms((long)mp,(long)mg))TeamMobSlotConfirmed=true;
        }
        return null;
    }
    static bool RunOriginalHostile(Mob mob){Bypass++;try{return OriginalHostile(mob);}finally{Bypass--;}}
    static bool RunOriginalSyncMob(Mob mob){Bypass++;try{return OriginalSyncMob(mob);}finally{Bypass--;}}

    static bool BeforeHostile(Mob __0,ref bool __result)
    {
        if(Bypass!=0){A1.C.V[A1Bypass]++;return true;}
        if(!OnGameThread()){Interlocked.Increment(ref A1.OffThread);return true;}
        var v=A1.C.V;v[A1HostileCalls]++;
        if(!A1.Ready()){v[A1Waiting]++;return true;}
        if(__0==null){v[A1Null]++;return true;}
        try
        {
            IntPtr p=__0.HashlinkPointer;
            if(p==IntPtr.Zero){v[A1Null]++;return true;}
            if(!SlotsConfirmed||R.ShouldShadow(++HostileSamples,ShadowAll)){__result=ShadowHostile(__0,p);return false;}
            __result=RawHostile(p);
            v[A1HostileFast]++;A1.Fast++;
            return false;
        }
        catch(Exception e){A1.Fault(e);return true;}
    }
    static bool ShadowHostile(Mob mob,IntPtr p)
    {
        A1.Shadows++;
        string bad;
        try{bad=CheckSlots(mob,p);}catch(Exception e){A1.Fault(e);return RunOriginalHostile(mob);}
        if(bad!=null){bool o=RunOriginalHostile(mob);A1.Mismatch(bad);return o;}
        long t0=0,b0=0,t1=0,b1=0;
        if(Stats){t0=Now();b0=Bytes();}
        bool fast=RawHostile(p);
        if(Stats){t1=Now();b1=Bytes();}
        bool orig=RunOriginalHostile(mob);
        if(Stats)A1.Time(t1-t0,b1-b0,Now()-t1,Bytes()-b1);
        return Settle(A1,"IsMobHostileToPlayers",mob,fast,orig);
    }

    static bool BeforeSyncMob(Mob __0,ref bool __result)
    {
        if(Bypass!=0){A1.C.V[A1Bypass]++;return true;}
        if(!OnGameThread()){Interlocked.Increment(ref A1.OffThread);return true;}
        var v=A1.C.V;v[A1SyncCalls]++;
        if(!A1.Ready()){v[A1Waiting]++;return true;}
        if(__0==null){v[A1Null]++;return true;}
        IntPtr p;bool shadow;
        try
        {
            p=__0.HashlinkPointer;
            if(p==IntPtr.Zero){v[A1Null]++;return true;}
            shadow=!SlotsConfirmed||R.ShouldShadow(++SyncSamples,ShadowAll);
        }
        catch(Exception e){A1.Fault(e);return true;}
        // From here on the only exception that can leave is EnableMobsSync's, which leaves the original the same way.
        if(shadow){__result=ShadowSyncMob(__0,p);return false;}
        __result=FastSyncMob(__0,p);
        v[A1SyncFast]++;A1.Fast++;
        return false;
    }
    static bool FastSyncMob(Mob mob,IntPtr p)
    {
        if(!MultiplayerSettingsStorage.EnableMobsSync)return false;      // IL_0000, read first and outside the try, as in the release
        try
        {
            var verdict=SyncFromSlots(mob.destroyed,p);
            return verdict==R.SyncVerdict.ByType?SyncMobByType(mob):R.SyncValue(verdict);
        }
        catch{return false;}                                              // the release catch-all (IL_0038)
    }
    static bool ShadowSyncMob(Mob mob,IntPtr p)
    {
        A1.Shadows++;
        string bad;
        try{bad=CheckSlots(mob,p);}catch(Exception e){A1.Fault(e);return RunOriginalSyncMob(mob);}
        if(bad!=null){bool o=RunOriginalSyncMob(mob);A1.Mismatch(bad);return o;}
        long t0=0,b0=0,t1=0,b1=0;
        if(Stats){t0=Now();b0=Bytes();}
        bool fast=FastSyncMob(mob,p);
        if(Stats){t1=Now();b1=Bytes();}
        bool orig=RunOriginalSyncMob(mob);
        if(Stats)A1.Time(t1-t0,b1-b0,Now()-t1,Bytes()-b1);
        return Settle(A1,"IsSyncMob",mob,fast,orig);
    }

    // =====================================================================================================
    // A3. Near-box pre-check in TryActivateHostMobForRemotePlayer (only caller: RunHostRemotePlayerActivationPass, up
    // to 96 tracked mobs per frame).
    //
    // Release IL 144297: every path before the near test returns false (null, !IsSyncMob, !hostile, destroyed, life<=0,
    // exception), the near test itself is IsMobNearAnyRemotePlayer(mob, s_hostRemoteActivationPlayers) (IL_0047, outside
    // the try) and false there returns false (IL_0054); PromoteMobToSyncVisibleState, QueueHostMobDirty and
    // TryAssignHostAttackTarget run only after a true near test. The near test (IsWithinRangeBox with its own catch,
    // value reads) is pure, so it is evaluated first, with the same list object on the same thread; a mob that is not
    // near returns false at once. A near mob, a null mob and any problem run the original unchanged. Dropped for skipped
    // mobs: the pure IsSyncMob / hostile / value reads, EnableMobsSync's idempotent normalisation and the idempotent
    // s_syncMobTypeCache.GetOrAdd.
    // Shadow: the original runs for the call; a postfix on IsMobNearAnyRemotePlayer records what the original's own near
    // test returned; the call agrees when the original returned false and did not find the mob near. If the near test is
    // never observed in 4096 shadowed calls (observer bypassed) the check cannot work and A3 switches off.
    // Guard: TryActivate, everything its calls before IL_0047 can run (IsSyncMob, IsMobHostileToPlayers and the four value
    // getters with their closures) and the near test's closure (it runs twice for a near mob) carry no foreign patch.
    // =====================================================================================================
    static readonly HostItem A3=new("A3","calls","skipped","near","notArmed","nullMob","observed","unseen"){Arm=ArmA3,PatchCheck=CheckA3};
    const int A3Calls=0,A3Skipped=1,A3Near=2,A3Waiting=3,A3Null=4,A3Observed=5,A3Unseen=6;
    static MethodInfo ActivateMethod,NearMethod,RangeBoxMethod,WorldXMethod,WorldYMethod;
    static Func<Mob,List<Entity>,bool> NearTest;
    static FieldInfo PlayersField;
    static List<Entity> ActivationPlayers;
    static long ActivationSamples,ActivationObserved;
    static Guard A3Guard;
    [ThreadStatic] static bool ObserveNear;
    [ThreadStatic] static int NearObserved;
    [ThreadStatic] static long A3FastTicks,A3FastBytes,A3OrigT0,A3OrigB0;

    static void BindA3()
    {
        // The methods a skipped call no longer runs (also bound here when A1 is switched off).
        HostileMethod=SyncMethod("IsMobHostileToPlayers",typeof(bool),typeof(Mob));
        SyncMobMethod=SyncMethod("IsSyncMob",typeof(bool),typeof(Mob));
        ByTypeMethod=SyncMethod("IsSyncMobByType",typeof(bool),typeof(Mob));
        ActivateMethod=SyncMethod("TryActivateHostMobForRemotePlayer",typeof(bool),typeof(Mob));
        NearMethod=SyncMethod("IsMobNearAnyRemotePlayer",typeof(bool),typeof(Mob),typeof(List<Entity>));
        RangeBoxMethod=SyncMethod("IsWithinRangeBox",typeof(bool),typeof(Mob),typeof(Entity),typeof(double),typeof(double));
        WorldXMethod=SyncMethod("GetWorldX",typeof(double),typeof(Entity));
        WorldYMethod=SyncMethod("GetWorldY",typeof(double),typeof(Entity));
        foreach(var m in new[]{ActivateMethod,NearMethod,RangeBoxMethod,WorldXMethod,WorldYMethod,HostileMethod,SyncMobMethod,ByTypeMethod})Pin(m,R.ModPins);
        PlayersField=OptCore.Field(Sync,"s_hostRemoteActivationPlayers",typeof(List<Entity>),true);
        if(!PlayersField.IsInitOnly)throw new InvalidOperationException("pinned s_hostRemoteActivationPlayers is not readonly");
        NearTest=OptCore.Delegate<Func<Mob,List<Entity>,bool>>(NearMethod);
        // The pinned body calls IsSyncMob, IsMobHostileToPlayers, destroyed, life, isOutOfGame, isOnScreen before IL_0047,
        // then IsMobNearAnyRemotePlayer at IL_004D.
        var before=Callees(ActivateMethod,0,R.ActivationNearTestAt).ToList();
        if(before.Count!=6||!Same(before[0],SyncMobMethod)||!Same(before[1],HostileMethod)||!Callees(ActivateMethod,R.ActivationNearTestAt).Any(m=>Same(m,NearMethod)))
            throw new InvalidOperationException("TryActivateHostMobForRemotePlayer no longer has the pinned call layout");
        A3Guard=NewGuard(new MethodBase[]{ActivateMethod},before.Append(NearMethod));
    }
    static string CheckA3()=>Foreign(A3Guard);
    static string ArmA3()
    {
        var f=CheckA3();if(f!=null)return f;
        var players=(List<Entity>)PlayersField.GetValue(null);       // static readonly: the list the original reads at IL_0048
        if(players==null)return "~remote activation player list not created yet";
        ActivationPlayers=players;
        return null;
    }

    static bool BeforeActivate(Mob __0,ref bool __result,out bool __state)
    {
        __state=false;ObserveNear=false;
        if(!OnGameThread()){Interlocked.Increment(ref A3.OffThread);return true;}
        var v=A3.C.V;v[A3Calls]++;
        if(!A3.Ready()){v[A3Waiting]++;return true;}
        if(__0==null){v[A3Null]++;return true;}
        try
        {
            bool timed=Stats&&R.ShouldShadow(ActivationSamples+1,ShadowAll);
            long t0=0,b0=0;if(timed){t0=Now();b0=Bytes();}
            bool near=NearTest(__0,ActivationPlayers);
            if(near){v[A3Near]++;return true;}
            if(R.ShouldShadow(++ActivationSamples,ShadowAll))
            {
                A3.Shadows++;NearObserved=R.NearUnseen;ObserveNear=true;__state=true;
                if(timed){A3FastTicks=Now()-t0;A3FastBytes=Bytes()-b0;A3OrigT0=Now();A3OrigB0=Bytes();}
                return true;
            }
            v[A3Skipped]++;A3.Fast++;
            __result=false;return false;
        }
        catch(Exception e){ObserveNear=false;A3.Fault(e);return true;}
    }
    static void AfterActivate(bool __result,bool __state)
    {
        if(!__state)return;
        try
        {
            ObserveNear=false;int seen=NearObserved;NearObserved=R.NearUnseen;
            if(Stats&&A3OrigT0!=0){A3.Time(A3FastTicks,A3FastBytes,Now()-A3OrigT0,Bytes()-A3OrigB0);A3OrigT0=0;}
            if(seen!=R.NearUnseen){ActivationObserved++;A3.C.V[A3Observed]++;}else A3.C.V[A3Unseen]++;
            if(!R.ActivationShadowAgrees(__result,seen)){A3.Mismatch("TryActivateHostMobForRemotePlayer original="+__result+" nearSeen="+seen);return;}
            A3.Gate.Ok();
            if(R.ActivationUnverifiable(A3.Shadows,ActivationObserved))A3.SwitchOff("the original's near test was not observed in "+A3.Shadows+" shadowed calls");
        }
        catch(Exception e){A3.Fault(e);}
    }
    // Observer of the original's own near test during an A3 shadow call. Never throws.
    static void AfterNear(bool __result){if(ObserveNear)NearObserved=R.NearSeen(__result);}
}
