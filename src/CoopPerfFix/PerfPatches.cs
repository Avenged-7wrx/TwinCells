using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using dc.en;
using DeadCellsMultiplayerMod;
using DeadCellsMultiplayerMod.Mobs.Bosses;
using HarmonyLib;
using File=System.IO.File;
using Path=System.IO.Path;

// One per-frame saving that the mod's author made after release 0.5.1 (upstream commit
// e626986, "per-tick performance upgrade"), applied to the released 0.5.1 DLL without
// changing what it computes or sends:
//
// BossSyncHelpers.IsBossMob and its twin IsBossEncounterCombatant (identical body in the pinned
// DLL, checked by IL) are asked about every tracked mob several times per frame (measured:
// about 10 000 IsBossMob calls per second on a host in a 135-mob level, and in the timing
// profile profile-baseline-23 the twin cost as much again). Each call converts
// mob.type to a managed string and scans it against ~30 tokens. The answer depends only on
// the mob's type, so it is remembered per mob object. The live "is this the level's boss"
// identity check is NOT remembered (boss phases re-point it).
//
// A second candidate from the same upstream commit (skipping the animation-name conversion
// for non-hero entities in Hook_AnimManager_play) was built and measured in native run
// perf-backport-14: 6-17 calls per second at 0.7 microseconds each. Not worth a patch; left out.
//
// COOP_PERF_STATS=1 adds counters to the log every 10 s. In that mode every 256th remembered
// answer is recomputed, compared with the remembered one and timed.
public static class PerfPatches
{
    public const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string NativePin="A37869A3C91443B50322B2CDF29595C9644AB8FC1280342F1F17C6F616A15534";
    public const string Owner="CoopPerfFix.BossQuery.20261110";
    const BindingFlags S=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
    static readonly object Gate=new();
    static volatile bool Published;
    static bool Stats;
    static Func<Mob,bool> RuntimeType,ComponentType,ProxyType;
    static readonly ConditionalWeakTable<Mob,object> Verdicts=new();
    static readonly object Yes=true,No=false;
    static long BossCalls,BossHits,BossMisses,BossLive,BossShadow,BossShadowTicks,BossShadowBytes,BossMismatch,Faults;
    static long NextReport,Started;
    public static bool Ready=>Published;
    public static bool Disabled{get;private set;}
    public static string InitializationError{get;private set;}
    public static long Mismatches=>Interlocked.Read(ref BossMismatch);
    public static long Errors=>Interlocked.Read(ref Faults);

    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);
    static Func<Mob,bool> Helper(string name)
    {
        var method=typeof(BossSyncHelpers).GetMethod(name,S|BindingFlags.DeclaredOnly,null,new[]{typeof(Mob)},null);
        if(method?.ReturnType!=typeof(bool))throw new InvalidOperationException("Pinned boss helper changed: "+name);
        return (Func<Mob,bool>)Delegate.CreateDelegate(typeof(Func<Mob,bool>),method);
    }
    public static void Install()
    {
        lock(Gate)
        {
            if(Published)return;
            if(InitializationError!=null)throw new InvalidOperationException("Earlier perf backport initialization failed: "+InitializationError);
            Harmony harmony=null;MethodInfo boss=null,twin=null;
            try
            {
                var original=typeof(ModEntry).Assembly;
                if(original.GetName().Version!=new Version(0,5,1,0)||Hash(original.Location)!=BasePin||
                    Hash(Path.Combine(Path.GetDirectoryName(typeof(Hero).Assembly.Location),"hlboot.dat"))!=NativePin)
                    throw new InvalidOperationException("CoopPerfFix requires exact community0.5.1/native35.13.5");
                boss=typeof(BossSyncHelpers).GetMethod("IsBossMob",S|BindingFlags.DeclaredOnly,null,new[]{typeof(Mob)},null);
                twin=typeof(BossSyncHelpers).GetMethod("IsBossEncounterCombatant",S|BindingFlags.DeclaredOnly,null,new[]{typeof(Mob)},null);
                if(boss?.ReturnType!=typeof(bool)||twin?.ReturnType!=typeof(bool))throw new InvalidOperationException("Pinned boss query signature changed");
                // The twin may share the remembered answers only while both bodies are byte-identical.
                byte[] a=boss.GetMethodBody()?.GetILAsByteArray(),b=twin.GetMethodBody()?.GetILAsByteArray();
                if(a==null||b==null||a.Length!=88||!a.AsSpan().SequenceEqual(b))throw new InvalidOperationException("Pinned boss query bodies differ");
                RuntimeType=Helper("IsKnownBossRuntimeType");ComponentType=Helper("IsKnownBossComponentType");ProxyType=Helper("IsKnownBossProxyType");
                Disabled=Environment.GetEnvironmentVariable("COOP_PERF_BOSS_DISABLE")=="1";
                Stats=Environment.GetEnvironmentVariable("COOP_PERF_STATS")=="1";
                Started=Stopwatch.GetTimestamp();NextReport=Started+Stopwatch.Frequency*10;
                harmony=new Harmony(Owner);
                foreach(var target in new[]{boss,twin})
                {
                    harmony.Patch(target,prefix:new HarmonyMethod(typeof(PerfPatches).GetMethod(nameof(BeforeIsBoss),S)));
                    var installed=Harmony.GetPatchInfo(target);
                    if(installed==null||installed.Prefixes.Count(p=>p.owner==Owner)!=1)throw new InvalidOperationException("Perf prefix was not installed exactly once");
                }
                Published=true;
            }
            catch(Exception error)
            {
                Published=false;InitializationError=error.Message;
                if(harmony!=null)foreach(var target in new[]{boss,twin})if(target!=null)try{harmony.Unpatch(target,HarmonyPatchType.All,Owner);}
                    catch(Exception rollback){InitializationError+="; rollback: "+rollback.Message;}
                throw;
            }
        }
    }

    // 0.5.1 body (IL checked against the pinned DLL): mob==null -> false; level.boss identity ->
    // true; otherwise RuntimeType ? true : ComponentType ? false : ProxyType ? true : (mob is
    // Boss); any exception -> false. Only the type part is remembered, per mob object, and only
    // once mob.type is readable. Null, the live boss and any failure go to the original.
    static bool TypeVerdict(Mob mob)=>RuntimeType(mob)||(!ComponentType(mob)&&(ProxyType(mob)||mob is dc.en.mob.Boss));
    static bool BeforeIsBoss(Mob mob,ref bool __result)
    {
        if(!Published||Disabled||mob==null)return true;
        try
        {
            if(Stats)Interlocked.Increment(ref BossCalls);
            var level=mob._level;
            if(level!=null&&ReferenceEquals(level.boss,mob)){if(Stats)Interlocked.Increment(ref BossLive);return true;}
            if(Verdicts.TryGetValue(mob,out var cached))
            {
                __result=ReferenceEquals(cached,Yes);
                if(Stats)Shadow(mob,__result);
                return false;
            }
            if(mob.type==null)return true;      // not fully built yet: let the original answer, remember nothing
            bool verdict=TypeVerdict(mob);
            if(Stats)Interlocked.Increment(ref BossMisses);
            Verdicts.AddOrUpdate(mob,verdict?Yes:No);
            __result=verdict;
            return false;
        }
        catch(Exception error)
        {
            if(Interlocked.Increment(ref Faults)==1)
                try{Serilog.Log.Warning("[CoopPerfFix] Boss query fell back to the original: {Error}",error.GetBaseException().Message);}catch{}
            return true;
        }
    }
    // Stats only: every 256th remembered answer is recomputed, compared and timed. The timing is
    // what one un-remembered query costs on this PC in steady state.
    static void Shadow(Mob mob,bool remembered)
    {
        if((Interlocked.Increment(ref BossHits)&255)==0)
        {
            long b0=GC.GetAllocatedBytesForCurrentThread(),t0=Stopwatch.GetTimestamp();
            bool fresh=TypeVerdict(mob);
            Interlocked.Add(ref BossShadowTicks,Stopwatch.GetTimestamp()-t0);Interlocked.Add(ref BossShadowBytes,GC.GetAllocatedBytesForCurrentThread()-b0);
            Interlocked.Increment(ref BossShadow);
            if(fresh!=remembered&&Interlocked.Increment(ref BossMismatch)==1)
                try{Serilog.Log.Error("[CoopPerfFix] Remembered boss answer differs from a fresh one for {Type}",mob.GetType().Name);}catch{}
        }
        long now=Stopwatch.GetTimestamp(),next=Interlocked.Read(ref NextReport);
        if(now<next||Interlocked.CompareExchange(ref NextReport,now+Stopwatch.Frequency*10,next)!=next)return;
        try
        {
            long n=Interlocked.Read(ref BossShadow);double us=1e6/Stopwatch.Frequency;
            Serilog.Log.Information("[CoopPerfFix] stats seconds={Seconds:0.0} bossQueries={BossQueries} bossHits={BossHits} bossMisses={BossMisses} bossLive={BossLive} bossShadow={BossShadow} bossFreshUs={FreshUs:0.00} bossFreshBytes={FreshBytes:0} bossMismatch={BossMismatch} errors={Errors}",
                (now-Started)/(double)Stopwatch.Frequency,Interlocked.Read(ref BossCalls),Interlocked.Read(ref BossHits),Interlocked.Read(ref BossMisses),Interlocked.Read(ref BossLive),
                n,n>0?Interlocked.Read(ref BossShadowTicks)*us/n:0.0,n>0?(double)Interlocked.Read(ref BossShadowBytes)/n:0.0,Interlocked.Read(ref BossMismatch),Interlocked.Read(ref Faults));
        }
        catch{}
    }
}
