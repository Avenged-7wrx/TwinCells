using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using static OptClientRules;

// Pure rules AND the control flow of part OptClient (CoopOpt): the CLIENT per-enemy steady state of
// MobsSynchronization.Hook_Mob_preUpdate, design-client-per-enemy.json D1 with every correction of
// design\round2\client-per-enemy\skeptic.md (F1-F8). No game or mod types here: OptClient.cs instantiates the generic code
// with dc.en.Mob / dc.pr.Level / its Glue and forwards its Harmony prefix and finalizer to OptClientPath; OptClientChecks.cs
// runs the same code against a managed model of the release client path under an emulation of HarmonyX's
// prefix / body / finalizer order (Test-Managed.ps1 -Only OptClient), and notes\client\bind-check.ps1 runs it under the real
// HarmonyX 2.16.1 on a stand-in method with the real orig_preUpdate delegate type.
//
// Release 0.5.1 client body (IL 133468-133541): IsSyncMob(self); if sync: EnsureMobTracked(self),
// UpdateClientMobAiAuthority(self); orig(self). In state S below every one of those calls only reads, so the body is
// observationally orig(self) (skeptic section 1). S, evaluated in this order (skeptic F4):
//   1 role: !IsHost(net) && IsClient(net)                       (the mod's own IsHost/IsClient, IL 132459-132482)
//   2 EnableMobsSync (same lock(SyncRoot) and value as get_EnableMobsSync, IL 62077; the normalisation write-back is skipped)
//   3 self != null, !self.destroyed, level = self._level != null
//   4 IsSyncMobByType(self) (the mod's method; with 2-3 it makes IsSyncMob true, IL 142564-142599)
//   8 !(IsLocalBossIntroCineActive() && IsBossMob(self))         (the mod's methods, IsBossMob only when the cine is active)
//   9 !HasLocalQueuedOrChargingSkill(self)                       (the mod's method, called exactly once as in the original)
//   then ONE lock(Sync) section, the last thing before orig, never held across 8-9:
//   5 s_levelIdentityReady && token > 0 && currentLevel != null && ReferenceEquals(currentLevel, level)
//     (DoesLevelMatchCurrentIdentityLocked returns true at IL_0023 before its slow path; CSF's MatchTrackedNativeLevel
//     prefix returns pointer equality = true for the same object)
//   6 trackedMobs.Count > 0 && trackedMobIndices hit && index in range && ReferenceEquals(trackedMobs[index], self)
//     (FindTrackedMobIndexLocked first branch, IL 141482-141508, no write)
//   7 clientAiLockedMobs.Contains(self) && !clientActiveNetworkAttackMobs.Contains(self)
//     (RefreshClientNetworkAttackState returns at IL_000B; TryLockClientMobAiAuthority's Add returns false, no lockAiS)
//
// Validation (CoopLoadFix ChainCache model, skeptic F1): the first 4096 S-calls, the first 64 after every level-identity
// change and then every 256th are NOT served: the literal original body runs with orig replaced by a one-shot probe that
// compares, when the body calls orig, a snapshot of the mod's tracking state with the one taken in the prefix; the
// finalizer judges it (orig exactly once, with self, nothing changed). One mismatch switches the fast path off for the
// session. A hit is served only after the warm-up was actually judged: 4096 samples compared equal (WarmupGate).
public static class OptClientRules
{
    public enum Reason { Hit, NotClient, Settings, NoMob, Destroyed, NoLevel, Type, Cine, Skill, Identity, Tracked, NotLocked, Attack, Count }
    public static readonly string[] ReasonNames={"hit","notClient","settings","noMob","destroyed","noLevel","type","cine","skill","identity","tracked","notLocked","attack"};

    // What the decision reads. The real implementation (OptClient.Glue) is a struct, so every call is resolved at JIT time.
    public interface IView<TMob,TLevel> where TMob:class where TLevel:class
    {
        void Role(out bool host,out bool client);   // item 1: IsHost(net), then IsClient(net), of one LobbySession.NetRef read
        bool MobsSyncEnabled();                      // item 2
        bool Destroyed(TMob mob);                    // item 3
        TLevel Level(TMob mob);                      // item 3
        bool SyncByType(TMob mob);                   // item 4
        bool BossIntroCineActive();                  // item 8
        bool IsBoss(TMob mob);                       // item 8
        bool QueuedOrChargingSkill(TMob mob);        // item 9
        bool IdentityReady();                        // item 5, read under Sync
        int IdentityToken();                         // item 5, read under Sync
        TLevel CurrentLevel();                       // item 5, read under Sync
        bool Pending();                              // s_trackedMobValidationPending, under Sync (shadow snapshot only)
    }
    // What the patch needs besides the reads.
    public interface IEnv<TMob,TLevel>:IView<TMob,TLevel> where TMob:class where TLevel:class
    {
        object Target(TMob mob);                     // mob.aTarget (shadow snapshot, read outside Sync)
        OptClientState<TMob> Capture();              // first call: Sync and the static readonly collections
        string GuardViolation();                     // null = only allowed patches on the hook and on the skipped methods
        string AllowedForeign();                     // for the log
        bool Fault(Exception e);                     // FaultGate.Fail: true = switch the part off
        void Ok();                                   // FaultGate.Ok
        void Info(string line);
        void Warn(string line);
    }

    // Filled by Decide for a hit, under the same lock(Sync) as items 5-7.
    public struct HitInfo{public bool Sample,IdentityChanged;public OptClientSnapshot Before;}

    // The decision. Every read is side-effect free, so a decline leaves no trace and the original body then reproduces
    // everything (including exception behaviour). Exceptions propagate to the caller (counted as a fault there, and the
    // original runs). For a hit it also advances the sampler and, for a sampled call, takes the prefix snapshot.
    public static Reason Decide<TMob,TLevel,TView>(TMob self,ref TView v,OptClientState<TMob> state,Sampler sampler,ref HitInfo hit) where TMob:class where TLevel:class where TView:struct,IView<TMob,TLevel>
    {
        v.Role(out bool host,out bool client);
        if(!client||host)return Reason.NotClient;
        if(!v.MobsSyncEnabled())return Reason.Settings;          // read first even for a null mob, as IsSyncMob does
        if(self==null)return Reason.NoMob;
        if(v.Destroyed(self))return Reason.Destroyed;
        var level=v.Level(self);
        if(level==null)return Reason.NoLevel;
        if(!v.SyncByType(self))return Reason.Type;
        if(v.BossIntroCineActive()&&v.IsBoss(self))return Reason.Cine;
        if(v.QueuedOrChargingSkill(self))return Reason.Skill;
        bool taken=false;
        try
        {
            Monitor.Enter(state.Sync,ref taken);
            bool ready=v.IdentityReady();int token=v.IdentityToken();var current=v.CurrentLevel();
            var r=Locked(self,level,ready,token,current,state);
            if(r==Reason.Hit){hit.Sample=sampler.Next(RuntimeHelpers.GetHashCode(level),token,out hit.IdentityChanged);if(hit.Sample)state.TakeLocked(self,v.Pending(),ready,token,current,ref hit.Before);}
            return r;
        }
        finally{if(taken)Monitor.Exit(state.Sync);}
    }

    // Items 5-7, called with Sync held.
    public static Reason Locked<TMob,TLevel>(TMob self,TLevel level,bool ready,int token,TLevel current,OptClientState<TMob> s) where TMob:class where TLevel:class
    {
        if(!IdentityMatches(ready,token,current,level))return Reason.Identity;
        if(!TrackedAt(self,s.Tracked,s.Indices))return Reason.Tracked;
        if(!s.Locked.Contains(self))return Reason.NotLocked;
        if(s.Active.Contains(self))return Reason.Attack;
        return Reason.Hit;
    }
    // DoesLevelMatchCurrentIdentityLocked IL_0000-0023 (ready, level, token > 0, currentLevel, reference compare).
    public static bool IdentityMatches<TLevel>(bool ready,int token,TLevel current,TLevel level) where TLevel:class
        =>ready&&level!=null&&token>0&&current!=null&&ReferenceEquals(current,level);
    // FindTrackedMobIndexLocked IL_0000-0040 (the branch that returns without writing).
    public static bool TrackedAt<TMob>(TMob self,List<TMob> tracked,Dictionary<TMob,int> indices) where TMob:class
    {
        if(self==null||tracked.Count==0)return false;
        if(!indices.TryGetValue(self,out int i))return false;
        return i>=0&&i<tracked.Count&&ReferenceEquals(tracked[i],self);
    }

    // ---- shadow sampling (task: first 4096 S-calls, then every 256th; skeptic F7: plus the first 64 after each
    // level-identity change; COOP_OPT_SHADOW=all: every S-call). call and sinceIdentity are 1-based.
    public const long WarmSamples=4096,SamplePeriod=256,IdentityWarmSamples=64;
    public static bool ShouldSample(long call,long sinceIdentity,bool all)=>ShouldSample(call,sinceIdentity,all,WarmSamples,SamplePeriod,IdentityWarmSamples);
    public static bool ShouldSample(long call,long sinceIdentity,bool all,long warm,long period,long identityWarm)
        =>call>=1&&(all||call<=warm||(sinceIdentity>=1&&sinceIdentity<=identityWarm)||call%period==0);
    // Which prefix calls are timed (stats only): every 256th, every call in QA mode.
    public const long TimePeriod=256;
    public static bool ShouldTime(long call,bool all)=>call>=1&&(all||call%TimePeriod==0);

    // Counts S-calls and notices level-identity changes. The identity key is (identity hash of the level wrapper, token):
    // no reference to a level is kept. A hash collision only costs extra samples / a skipped guard re-check.
    public sealed class Sampler
    {
        public readonly bool All;public readonly long Warm,Period,IdentityWarm;
        long _calls,_since;bool _seen;int _hash,_token;
        public Sampler(bool all):this(all,WarmSamples,SamplePeriod,IdentityWarmSamples){}
        // Other thresholds: managed checks only (the game always uses the constants above).
        public Sampler(bool all,long warm,long period,long identityWarm)
        {
            if(warm<0||period<1||identityWarm<0)throw new ArgumentOutOfRangeException(nameof(period));
            All=all;Warm=warm;Period=period;IdentityWarm=identityWarm;
        }
        public long Calls=>_calls;
        public bool Next(int levelHash,int token,out bool identityChanged)
        {
            identityChanged=!_seen||levelHash!=_hash||token!=_token;
            if(identityChanged){_seen=true;_hash=levelHash;_token=token;_since=0;}
            _calls++;_since++;
            return ShouldSample(_calls,_since,All,Warm,Period,IdentityWarm);
        }
    }

    // Past the warm-up a hit is served only when the warm-up was judged: at least `warm` sampled calls compared equal and
    // none differed (review finding 1: creating 4096 probes is not comparing 4096 calls). Unjudged samples are either in
    // flight (orig re-entered the hook while a sampled call was running: this call waits, i.e. runs the original) or lost
    // (the probe never reached the body or the finalizer: switch off). Inconclusive samples do not count as equal.
    public enum WarmGate { Serve, Wait, Off }
    public const long MaxProbesInFlight=8;
    public static WarmGate WarmupGate(long samples,long ok,long mismatches,long inconclusive,long warm)
    {
        if(mismatches!=0)return WarmGate.Off;
        if(ok>=warm)return WarmGate.Serve;
        long unjudged=samples-ok-inconclusive;
        return unjudged>0&&unjudged<=MaxProbesInFlight?WarmGate.Wait:WarmGate.Off;
    }

    // ---- verdict of one shadow sample (skeptic F1: the snapshot is compared at the moment the original body calls orig)
    public enum Verdict { Ok, Mismatch, Inconclusive }
    public static Verdict Judge(int origCalls,bool sameSelf,bool faulted,string diff,out string why)
    {
        if(origCalls!=1){why="the original body called orig "+origCalls+" times";return Verdict.Mismatch;}
        if(faulted){why="the snapshot at orig could not be read";return Verdict.Inconclusive;}
        if(!sameSelf){why="the original body called orig with another mob";return Verdict.Mismatch;}
        if(diff!=null){why="changed before orig: "+diff;return Verdict.Mismatch;}
        why=null;return Verdict.Ok;
    }

    public static string Diff(in OptClientSnapshot a,in OptClientSnapshot b)
    {
        StringBuilder sb=null;
        Note(ref sb,a.TrackedCount!=b.TrackedCount||a.TrackedVersion!=b.TrackedVersion,"trackedMobs");
        Note(ref sb,a.IndicesCount!=b.IndicesCount||a.IndicesVersion!=b.IndicesVersion,"trackedMobIndices");
        Note(ref sb,a.LockedCount!=b.LockedCount||a.LockedVersion!=b.LockedVersion,"clientAiLockedMobs");
        Note(ref sb,a.ActiveCount!=b.ActiveCount||a.ActiveVersion!=b.ActiveVersion,"clientActiveNetworkAttackMobs");
        Note(ref sb,a.StartCount!=b.StartCount||a.StartVersion!=b.StartVersion,"clientNetworkAttackStartFrame");
        Note(ref sb,a.LeaseCount!=b.LeaseCount||a.LeaseVersion!=b.LeaseVersion,"clientBossSkillCallbackLeaseMobs");
        Note(ref sb,a.MobToIdCount!=b.MobToIdCount||a.MobToIdVersion!=b.MobToIdVersion,"MobToId");
        Note(ref sb,a.Pending!=b.Pending,"s_trackedMobValidationPending");
        Note(ref sb,a.Ready!=b.Ready||a.Token!=b.Token,"levelIdentity");
        Note(ref sb,!ReferenceEquals(a.Current,b.Current),"currentLevel");
        Note(ref sb,a.SelfIndex!=b.SelfIndex||!ReferenceEquals(a.SelfAt,b.SelfAt),"selfIndex");
        Note(ref sb,a.SelfLocked!=b.SelfLocked,"selfLocked");
        Note(ref sb,a.SelfActive!=b.SelfActive,"selfActive");
        Note(ref sb,a.TargetRead!=b.TargetRead||!ReferenceEquals(a.Target,b.Target),"aTarget");
        return sb?.ToString();
    }
    static void Note(ref StringBuilder sb,bool differs,string name)
    {
        if(!differs)return;
        if(sb==null)sb=new StringBuilder();else sb.Append(',');
        sb.Append(name);
    }

    // Reads the private _version field of a BCL collection through an emitted getter (no MethodInfo.Invoke per call).
    // Throws when the runtime has no such field: the part then does not install (fail closed).
    public static Func<TC,int> VersionGetter<TC>() where TC:class
    {
        var f=typeof(TC).GetField("_version",BindingFlags.Instance|BindingFlags.NonPublic);
        if(f==null||f.FieldType!=typeof(int))throw new InvalidOperationException("No int _version field on "+typeof(TC).Name+" in this runtime");
        var dm=new DynamicMethod("OptClient_version_"+typeof(TC).Name,typeof(int),new[]{typeof(TC)},typeof(OptClientRules).Module,true);
        var il=dm.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldfld,f);il.Emit(OpCodes.Ret);
        return (Func<TC,int>)dm.CreateDelegate(typeof(Func<TC,int>));
    }

    // ---- orig delegates. The game's dc.en.Hook_Mob.orig_preUpdate cannot be named at compile time (it derives from
    // System.Private.CoreLib's MulticastDelegate, which the reference-assembly build cannot reference: CS0012), so it is
    // invoked and created through emitted stubs. The checks run the same stubs on a model delegate of the same shape.
    static MethodInfo OrigInvoke<TMob>(Type origType)
    {
        var invoke=origType!=null&&typeof(MulticastDelegate).IsAssignableFrom(origType)?origType.GetMethod("Invoke",BindingFlags.Instance|BindingFlags.Public|BindingFlags.DeclaredOnly):null;
        var ps=invoke?.GetParameters();
        if(invoke==null||invoke.ReturnType!=typeof(void)||ps.Length!=1||ps[0].ParameterType!=typeof(TMob)||origType.GetConstructor(new[]{typeof(object),typeof(IntPtr)})==null)
            throw new InvalidOperationException("orig delegate signature changed: "+origType);
        return invoke;
    }
    // orig.Invoke(mob) on an orig delegate held as object (castclass + callvirt Invoke).
    public static Action<object,TMob> Invoker<TMob>(Type origType)
    {
        var invoke=OrigInvoke<TMob>(origType);
        var dm=new DynamicMethod("OptClient_invokeOrig",typeof(void),new[]{typeof(object),typeof(TMob)},typeof(OptClientRules).Module,true);
        var g=dm.GetILGenerator();g.Emit(OpCodes.Ldarg_0);g.Emit(OpCodes.Castclass,origType);g.Emit(OpCodes.Ldarg_1);g.Emit(OpCodes.Callvirt,invoke);g.Emit(OpCodes.Ret);
        return (Action<object,TMob>)dm.CreateDelegate(typeof(Action<object,TMob>));
    }
    // A new orig delegate bound to target.run (ldftn + newobj orig(object, IntPtr)): what the probe hands to the body.
    public static Func<TTarget,object> Binder<TTarget,TMob>(Type origType,MethodInfo run) where TTarget:class
    {
        OrigInvoke<TMob>(origType);
        var ps=run?.GetParameters();
        if(run==null||run.IsStatic||run.DeclaringType!=typeof(TTarget)||run.ReturnType!=typeof(void)||ps.Length!=1||ps[0].ParameterType!=typeof(TMob))
            throw new InvalidOperationException("probe method signature changed: "+run);
        var ctor=origType.GetConstructor(new[]{typeof(object),typeof(IntPtr)});
        var dm=new DynamicMethod("OptClient_bindOrig",typeof(object),new[]{typeof(TTarget)},typeof(OptClientRules).Module,true);
        var g=dm.GetILGenerator();g.Emit(OpCodes.Ldarg_0);g.Emit(OpCodes.Ldftn,run);g.Emit(OpCodes.Newobj,ctor);g.Emit(OpCodes.Ret);
        return (Func<TTarget,object>)dm.CreateDelegate(typeof(Func<TTarget,object>));
    }

    // ---- guard against foreign patches (skeptic F3) ----
    // Hook = Hook_Mob_preUpdate itself; Mirrored = a method whose work the fast path skips (its patches would be skipped
    // too), including everything those methods call on the S path (settings normalisation helpers, Config<T>, the by-type
    // lambda, the proxy getters: review finding 2); LevelMatch = DoesLevelMatchCurrentIdentityLocked, where CoopSyncFixes'
    // MatchTrackedNativeLevel prefix is the one allowed foreign patch (it returns true for exactly the reference-equal
    // levels the fast path accepts).
    public enum GuardTarget { Hook, Mirrored, LevelMatch }
    public enum PatchKind { Prefix, Postfix, Transpiler, Finalizer, ILManipulator }
    public readonly struct PatchFact
    {
        public readonly PatchKind Kind;public readonly string Owner,Method,DeclaringType;public readonly bool OwnAssembly;public readonly int Priority;
        public PatchFact(PatchKind kind,string owner,string method,string declaringType,bool ownAssembly,int priority)
        {Kind=kind;Owner=owner;Method=method;DeclaringType=declaringType;OwnAssembly=ownAssembly;Priority=priority;}
        public override string ToString()=>Kind+" "+Owner+"/"+DeclaringType+"."+Method+"/p"+Priority;
    }
    public const string ClientOwner="CoopOpt.Client.20261111",ClientType="OptClient",HostOwner="CoopOpt.Host.20261111",HostType="OptHost";
    public const string CsfOwner="local.deadcells.syncfixes.1",CsfLevelPrefix="MatchTrackedNativeLevel";
    public static bool IsOwnClient(in PatchFact p)=>p.OwnAssembly&&p.DeclaringType==ClientType&&p.Owner==ClientOwner;
    // The host part's patches on a skipped method that are known to be exact, as (target, patch method), prefixes only:
    // OptHost A1 returns the method's own boolean and writes nothing but its own counters. Every other patch of the host
    // part (another kind, another method, another target, the hook itself) counts as foreign (review finding 3).
    public static readonly (string Target,string Patch)[] HostAllowed={("MobsSynchronization.IsSyncMob","BeforeSyncMob"),("MobsSynchronization.IsMobHostileToPlayers","BeforeHostile")};
    public static bool IsAllowedHost(string method,in PatchFact p)
    {
        if(!p.OwnAssembly||p.DeclaringType!=HostType||p.Owner!=HostOwner||p.Kind!=PatchKind.Prefix)return false;
        foreach(var (t,m) in HostAllowed)if(t==method&&m==p.Method)return true;
        return false;
    }
    // Returns null when the patches on one target allow the fast path, else the reason. expectOwn: our prefix and
    // finalizer must be on the hook exactly once (false only for the check before our own Patch call).
    public static string GuardViolation(GuardTarget target,string method,IReadOnlyList<PatchFact> patches,bool expectOwn)
    {
        int ownPrefix=0,ownFinalizer=0,csf=0,host=0;
        foreach(var p in patches)if(IsOwnClient(p)){if(p.Kind==PatchKind.Prefix)ownPrefix++;else if(p.Kind==PatchKind.Finalizer)ownFinalizer++;else return method+": unexpected own "+p.Kind;}
        if(target==GuardTarget.Hook){if(expectOwn?(ownPrefix!=1||ownFinalizer!=1):(ownPrefix!=0||ownFinalizer!=0))return method+": own patches "+ownPrefix+" prefix / "+ownFinalizer+" finalizer";}
        else if(ownPrefix+ownFinalizer!=0)return method+": own patch on a mirrored method";
        foreach(var p in patches)
        {
            if(IsOwnClient(p))continue;
            if(target==GuardTarget.Mirrored&&IsAllowedHost(method,p)&&++host==1)continue;
            if(target==GuardTarget.LevelMatch&&p.Kind==PatchKind.Prefix&&p.Owner==CsfOwner&&p.Method==CsfLevelPrefix&&++csf==1)continue;
            return method+": "+p;
        }
        return null;
    }

    // ---- stats ----
    public enum Counter { Calls, Hits, Samples, SampleOk, Mismatches, Inconclusive, Faults, OtherPrefixSkipped, WarmWait, FastTimed, FastTicks, FastBytes, OrigTimed, OrigTicks, OrigBytes, DeclineTimed, DeclineTicks, DeclineBytes, Count }
    // One line per window. Counts are for the window; "total" values are cumulative.
    public static string StatsLine(double seconds,long[] c,long[] cPrev,long[] r,long[] rPrev,long ticksPerSecond,bool fastPathOn,string offReason)
    {
        var inv=CultureInfo.InvariantCulture;
        long W(Counter k)=>c[(int)k]-(cPrev==null?0:cPrev[(int)k]);
        long T(Counter k)=>c[(int)k];
        string Us(Counter n,Counter t){long k=W(n);return k<=0?"-":(W(t)*1e6/ticksPerSecond/k).ToString("0.00",inv);}
        string B(Counter n,Counter b){long k=W(n);return k<=0?"-":((double)W(b)/k).ToString("0",inv);}
        var sb=new StringBuilder(340);
        sb.Append("client window=").Append(seconds.ToString("0.0",inv)).Append("s calls=").Append(W(Counter.Calls)).Append(" hits=").Append(W(Counter.Hits))
          .Append(" samples=").Append(W(Counter.Samples)).Append(" declines=");
        for(int i=1;i<(int)Reason.Count;i++){if(i>1)sb.Append(',');sb.Append(ReasonNames[i]).Append(':').Append(r[i]-(rPrev==null?0:rPrev[i]));}
        sb.Append(" fastUs=").Append(Us(Counter.FastTimed,Counter.FastTicks)).Append(" fastB=").Append(B(Counter.FastTimed,Counter.FastBytes))
          .Append(" origModUs=").Append(Us(Counter.OrigTimed,Counter.OrigTicks)).Append(" origModB=").Append(B(Counter.OrigTimed,Counter.OrigBytes))
          .Append(" declineUs=").Append(Us(Counter.DeclineTimed,Counter.DeclineTicks)).Append(" declineB=").Append(B(Counter.DeclineTimed,Counter.DeclineBytes))
          .Append(" | total calls=").Append(T(Counter.Calls)).Append(" hits=").Append(T(Counter.Hits)).Append(" samples=").Append(T(Counter.Samples))
          .Append(" ok=").Append(T(Counter.SampleOk)).Append(" mismatches=").Append(T(Counter.Mismatches)).Append(" inconclusive=").Append(T(Counter.Inconclusive))
          .Append(" faults=").Append(T(Counter.Faults)).Append(" otherPrefixSkipped=").Append(T(Counter.OtherPrefixSkipped)).Append(" warmWait=").Append(T(Counter.WarmWait))
          .Append(" fastPath=").Append(fastPathOn?"on":"off("+(offReason??"?")+")");
        return sb.ToString();
    }
}

// ---- the patch: prefix and finalizer on Hook_Mob_preUpdate, and the probe of a sampled call ----
// Not generic in the environment, so OptClient holds it in one field (its prefix and finalizer are one-line forwarders)
// and notes\client\bind-check.ps1 can put a path over a test environment there.
public abstract class OptClientPathBase<TMob> where TMob:class
{
    public readonly long[] C=new long[(int)Counter.Count];
    public readonly long[] Rs=new long[(int)Reason.Count];
    volatile bool _off;string _offReason;
    public bool Off=>_off;
    public string OffReason=>_offReason;
    // Prefix. true: the original body runs (orig possibly replaced by a probe); false: this call is done (orig ran here,
    // or another prefix already skipped the original). Throws only what orig throws on the fast path.
    public abstract bool Before(ref object orig,TMob self,bool runOriginal);
    // Finalizer: judges the probe of a sampled call. Never throws.
    public abstract void After(object orig);
    // For the rest of the session the original body serves every call. The first reason is kept.
    public abstract void SwitchOff(string reason);
    protected bool MarkOff(string reason){if(_off)return false;_offReason=reason;_off=true;return true;}
}

public sealed class OptClientPath<TMob,TLevel,TEnv>:OptClientPathBase<TMob> where TMob:class where TLevel:class where TEnv:struct,IEnv<TMob,TLevel>
{
    public TEnv Env;
    public readonly Sampler Sampler;
    readonly bool _all;readonly long _started;
    readonly Action<object,TMob> _invoke;readonly Func<Probe,object> _makeProbe;
    OptClientState<TMob> _state;
    bool _guardOk,_served,_inconclusiveLogged;
    static Tuple<Type,Action<object,TMob>,Func<Probe,object>> s_stubs;   // per orig type (one in the game)

    public OptClientPath(TEnv env,Type origType,Sampler sampler)
    {
        Sampler=sampler??throw new ArgumentNullException(nameof(sampler));
        var s=s_stubs;
        if(s==null||s.Item1!=origType)s_stubs=s=Tuple.Create(origType,Invoker<TMob>(origType),Binder<Probe,TMob>(origType,typeof(Probe).GetMethod(nameof(Probe.Run))));
        _invoke=s.Item2;_makeProbe=s.Item3;
        Env=env;_all=sampler.All;_started=Stopwatch.GetTimestamp();
    }

    // Positional __0/__1 in OptClient (parameter TYPES are checked at install, F7). Priority.Last: every other allowed
    // prefix has run, so calling orig here keeps the order prefixes -> body -> postfixes.
    public override bool Before(ref object orig,TMob self,bool runOriginal)
    {
        if(!runOriginal){C[(int)Counter.OtherPrefixSkipped]++;return false;}   // another prefix replaced this call: never a second orig
        if(Off)return true;
        long call=++C[(int)Counter.Calls];
        bool timed=ShouldTime(call,_all);
        long t0=0,b0=0;
        if(timed){b0=GC.GetAllocatedBytesForCurrentThread();t0=Stopwatch.GetTimestamp();}
        try
        {
            var state=_state??=Env.Capture();
            var hit=new HitInfo();
            var r=Decide<TMob,TLevel,TEnv>(self,ref Env,state,Sampler,ref hit);
            Rs[(int)r]++;
            if(r!=Reason.Hit){if(timed)Time(Counter.DeclineTimed,Counter.DeclineTicks,Counter.DeclineBytes,t0,b0);Env.Ok();return true;}
            if(hit.IdentityChanged||!_guardOk)
            {
                var bad=Env.GuardViolation();
                if(bad!=null){SwitchOff("patched by another module: "+bad);return true;}
                if(!_guardOk){_guardOk=true;Log(false,"OptClient: first client steady-state call; guard OK (other patches on the re-tested methods: "+Env.AllowedForeign()+")");}
            }
            if(hit.Sample)
            {
                hit.Before.Target=Env.Target(self);hit.Before.TargetRead=true;
                var probe=new Probe(this,orig,self,hit.Before,Sampler.Calls);
                orig=_makeProbe(probe);
                C[(int)Counter.Samples]++;
                Env.Ok();
                probe.ExitBytes=GC.GetAllocatedBytesForCurrentThread();probe.ExitTicks=Stopwatch.GetTimestamp();
                return true;      // the literal original body runs; the probe compares when the body calls orig
            }
            if(!_served)
            {
                var gate=WarmupGate(C[(int)Counter.Samples],C[(int)Counter.SampleOk],C[(int)Counter.Mismatches],C[(int)Counter.Inconclusive],Sampler.Warm);
                if(gate==WarmGate.Wait){C[(int)Counter.WarmWait]++;Env.Ok();return true;}
                if(gate==WarmGate.Off){SwitchOff("warm-up not judged: "+C[(int)Counter.Samples]+" samples, "+C[(int)Counter.SampleOk]+" compared equal, "+C[(int)Counter.Inconclusive]+" inconclusive (needs "+Sampler.Warm+" equal)");return true;}
                _served=true;
                Log(false,"OptClient: first fast-path call served after "+C[(int)Counter.Samples]+" samples ("+C[(int)Counter.SampleOk]+" compared equal, "+C[(int)Counter.Inconclusive]+" inconclusive)");
            }
            C[(int)Counter.Hits]++;
            if(timed)Time(Counter.FastTimed,Counter.FastTicks,Counter.FastBytes,t0,b0);
            Env.Ok();
        }
        catch(Exception e)
        {
            // nothing of the game has run yet in this call: the original body serves it
            C[(int)Counter.Faults]++;
            if(Trip(e))SwitchOff("faults: "+e.GetBaseException().Message);
            return true;   // fault: the original body serves this call
        }
        _invoke(orig,self);   // the fast path: what the original body does in S; exceptions of orig propagate unchanged (F7)
        return false;         // nothing after orig
    }

    // Runs after every call, also when it threw (OptClient's finalizer returns void: HarmonyX rethrows unchanged).
    // Only a sampled call carries a probe in the orig slot; a probe always reports to the path that made it.
    public override void After(object orig)
    {
        if(orig is not Delegate d||d.Target is not Probe p)return;
        try{p.Finish();}
        catch(Exception e){C[(int)Counter.Faults]++;if(Trip(e))SwitchOff("faults (finalizer): "+e.GetBaseException().Message);}
    }

    public override void SwitchOff(string reason)
    {
        if(MarkOff(reason))Log(true,"OptClient fast path switched off for this session, the original Hook_Mob_preUpdate body serves every call: "+reason);
    }

    bool Trip(Exception e){try{return Env.Fault(e);}catch{return true;}}
    void Log(bool warn,string line){try{if(warn)Env.Warn(line);else Env.Info(line);}catch{}}
    void Time(Counter n,Counter ticks,Counter bytes,long t0,long b0){C[(int)n]++;C[(int)ticks]+=Stopwatch.GetTimestamp()-t0;C[(int)bytes]+=GC.GetAllocatedBytesForCurrentThread()-b0;}

    // The probe's snapshot when the original body calls orig: aTarget outside the lock, the rest under Sync.
    void SnapshotNow(TMob self,ref OptClientSnapshot s)
    {
        s.Target=Env.Target(self);s.TargetRead=true;
        var st=_state;bool locked=false;
        try{Monitor.Enter(st.Sync,ref locked);st.TakeLocked(self,Env.Pending(),Env.IdentityReady(),Env.IdentityToken(),Env.CurrentLevel(),ref s);}
        finally{if(locked)Monitor.Exit(st.Sync);}
    }

    // Replaces orig for one sampled call of the literal original body. Per call, no static scratch (F7): orig's callbacks
    // may re-enter the mod and the hook.
    public sealed class Probe
    {
        readonly OptClientPath<TMob,TLevel,TEnv> _path;readonly object _real;readonly TMob _self;readonly OptClientSnapshot _before;readonly long _call;
        OptClientSnapshot _at;int _count;bool _sameSelf,_faulted,_finished;string _fault;long _origTicks,_origBytes;
        internal long ExitTicks,ExitBytes;
        internal Probe(OptClientPath<TMob,TLevel,TEnv> path,object real,TMob self,in OptClientSnapshot before,long call){_path=path;_real=real;_self=self;_before=before;_call=call;}
        // The original body's orig(self).
        public void Run(TMob self)
        {
            if(++_count==1)
            {
                try{_origTicks=Stopwatch.GetTimestamp();_origBytes=GC.GetAllocatedBytesForCurrentThread();_sameSelf=ReferenceEquals(self,_self);_path.SnapshotNow(_self,ref _at);}
                catch(Exception e){_faulted=true;_fault=e.GetBaseException().Message;}
            }
            _path._invoke(_real,self);   // every orig call of the body reaches the real orig, exactly as without the probe
        }
        internal void Finish()
        {
            if(_finished)return;
            _finished=true;
            var c=_path.C;
            string diff=_count==1&&!_faulted&&_sameSelf?Diff(_before,_at):null;
            switch(Judge(_count,_sameSelf,_faulted,diff,out string why))
            {
                case Verdict.Ok:
                    c[(int)Counter.SampleOk]++;
                    c[(int)Counter.OrigTimed]++;c[(int)Counter.OrigTicks]+=_origTicks-ExitTicks;c[(int)Counter.OrigBytes]+=_origBytes-ExitBytes;
                    break;
                case Verdict.Inconclusive:
                    c[(int)Counter.Inconclusive]++;
                    if(!_path._inconclusiveLogged){_path._inconclusiveLogged=true;_path.Log(true,"OptClient shadow sample inconclusive (not counted as a mismatch, nor as equal): "+(_fault??why));}
                    break;
                default:
                    c[(int)Counter.Mismatches]++;
                    _path.SwitchOff("shadow mismatch: "+why+"; mob "+_self?.GetType().Name+"; S-call "+_call+"; "+((Stopwatch.GetTimestamp()-_path._started)/(double)Stopwatch.Frequency).ToString("0.0",CultureInfo.InvariantCulture)+" s after install");
                    break;
            }
        }
    }
}

// Sync and the static readonly collections of MobsSynchronization (never reassigned after its type initializer:
// stsfld only in .cctor, skeptic section 1), with emitted _version getters for the shadow snapshot.
public sealed class OptClientState<TMob> where TMob:class
{
    public readonly object Sync;
    public readonly List<TMob> Tracked;
    public readonly Dictionary<TMob,int> Indices,MobToId;
    public readonly HashSet<TMob> Locked,Active,Lease;
    public readonly Dictionary<TMob,double> StartFrame;
    // one set of emitted getters per collection type (built by the first constructor; a race only builds them twice)
    static Func<List<TMob>,int> _vList;static Func<Dictionary<TMob,int>,int> _vIndex;static Func<HashSet<TMob>,int> _vSet;static Func<Dictionary<TMob,double>,int> _vStart;
    public OptClientState(object sync,List<TMob> tracked,Dictionary<TMob,int> indices,HashSet<TMob> locked,HashSet<TMob> active,
        Dictionary<TMob,double> startFrame,HashSet<TMob> lease,Dictionary<TMob,int> mobToId)
    {
        Sync=sync??throw new ArgumentNullException(nameof(sync));Tracked=tracked??throw new ArgumentNullException(nameof(tracked));
        Indices=indices??throw new ArgumentNullException(nameof(indices));Locked=locked??throw new ArgumentNullException(nameof(locked));
        Active=active??throw new ArgumentNullException(nameof(active));StartFrame=startFrame??throw new ArgumentNullException(nameof(startFrame));
        Lease=lease??throw new ArgumentNullException(nameof(lease));MobToId=mobToId??throw new ArgumentNullException(nameof(mobToId));
        _vList??=VersionGetter<List<TMob>>();_vIndex??=VersionGetter<Dictionary<TMob,int>>();
        _vSet??=VersionGetter<HashSet<TMob>>();_vStart??=VersionGetter<Dictionary<TMob,double>>();
    }
    // Everything of the mod's tracking state that the client body before orig could write. Caller holds Sync.
    // aTarget is read by the caller outside the lock.
    public void TakeLocked(TMob self,bool pending,bool ready,int token,object current,ref OptClientSnapshot s)
    {
        s.TrackedCount=Tracked.Count;s.TrackedVersion=_vList(Tracked);
        s.IndicesCount=Indices.Count;s.IndicesVersion=_vIndex(Indices);
        s.LockedCount=Locked.Count;s.LockedVersion=_vSet(Locked);
        s.ActiveCount=Active.Count;s.ActiveVersion=_vSet(Active);
        s.StartCount=StartFrame.Count;s.StartVersion=_vStart(StartFrame);
        s.LeaseCount=Lease.Count;s.LeaseVersion=_vSet(Lease);
        s.MobToIdCount=MobToId.Count;s.MobToIdVersion=_vIndex(MobToId);
        s.Pending=pending;s.Ready=ready;s.Token=token;s.Current=current;
        if(self!=null&&Indices.TryGetValue(self,out int i)){s.SelfIndex=i;s.SelfAt=i>=0&&i<Tracked.Count?Tracked[i]:null;}else{s.SelfIndex=int.MinValue;s.SelfAt=null;}
        s.SelfLocked=self!=null&&Locked.Contains(self);s.SelfActive=self!=null&&Active.Contains(self);
    }
}

public struct OptClientSnapshot
{
    public int TrackedCount,TrackedVersion,IndicesCount,IndicesVersion,LockedCount,LockedVersion,ActiveCount,ActiveVersion,
        StartCount,StartVersion,LeaseCount,LeaseVersion,MobToIdCount,MobToIdVersion,Token,SelfIndex;
    public bool Pending,Ready,SelfLocked,SelfActive,TargetRead;
    public object Current,SelfAt,Target;
}
