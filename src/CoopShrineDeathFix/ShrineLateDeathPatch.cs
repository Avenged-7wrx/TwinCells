using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using HaxeProxy.Runtime;
using dc.en;
using dc.en.inter;
using dc.pr;

// Conditional follow-up to the independently verified alive handoff. No live
// registry ID is assigned to the late actor; the old authoritative ID stays dead.
public static partial class ShrineLateDeathPatch
{
    public const string Owner="CoopShrineDeathFix.ExactNativeRetirement.20261108";
    public const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string NativePin="A37869A3C91443B50322B2CDF29595C9644AB8FC1280342F1F17C6F616A15534";
    public const string FixesPin="FFF00CA3F9297B00EBD7688A9E4D223FD2A14F2D0EC8DFE34801F8490C7342E9";
    public const string SourcePin="EA0702E0E1C1634BE4543864DA53AD6200ACEDB2FD77EEBA37DA54076CF68E45";
    public const string RegistrationPin="857C4714B2D9218BD2FF2DB2BD8E9DB6F41B075D54596BB6646F6B523A7791A7";
    const BindingFlags S=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
    const BindingFlags I=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    static readonly object InstallGate=new();
    static readonly List<Receipt> Receipts=new(); // Only under original Sync, max16.
    static Type M,F,Source,PairType;
    static FieldInfo SyncField,PairsField,ForwardField,ReverseField,TrackedField,PendingField,FirstFrameField,LedgerField;
    static readonly Dictionary<string,FieldInfo> PairFields=new(StringComparer.Ordinal);
    static MethodInfo PairContext,PairValid,PairActor,HasLadder,TryGetId,Dead,OriginalIsDead,AcceptedIntent,Frame,Quiesced,NativeFault,Reset,Flush,Run,Remember,DeathHook,FFFNativeBridge;
    static volatile bool Published;
    static long Captured,CompletedPresentation,Enrolled,Completed,Expired,Rejected,Errors,QueueErrors,LateCalls,LateReturns,LateFaults,PlainCompletions,OriginalPlainBodies,RetirementFaults;
    public static bool Ready=>Published;
    public static bool Disabled {get;private set;}
    public static string InitializationError {get;private set;}
    public static long CapturedPairs=>Interlocked.Read(ref Captured);
    public static long CompletedPresentations=>Interlocked.Read(ref CompletedPresentation);
    public static long EnrolledDeaths=>Interlocked.Read(ref Enrolled);
    public static long CompletedDeaths=>Interlocked.Read(ref Completed);
    public static long ExpiredReceipts=>Interlocked.Read(ref Expired);
    public static long RejectedReceipts=>Interlocked.Read(ref Rejected);
    public static long InspectionErrors=>Interlocked.Read(ref Errors);
    public static long EnrollmentErrors=>Interlocked.Read(ref QueueErrors);
    public static long NativeDeathCalls=>Interlocked.Read(ref LateCalls);
    public static long NativeDeathReturns=>Interlocked.Read(ref LateReturns);
    public static long NativeDeathFaults=>Interlocked.Read(ref LateFaults);
    public static long RetiredPlainActors=>Interlocked.Read(ref PlainCompletions);
    public static long OriginalPlainDeathCalls=>Interlocked.Read(ref OriginalPlainBodies);
    public static long PlainRetirementFaults=>Interlocked.Read(ref RetirementFaults);
    sealed class Receipt
    {
        public object Pair;public NetNode Net;public Level Level;public EliteShrine Shrine;
        public IntPtr LevelPointer,MapPointer,ShrinePointer,SourcePointer,ReplicaPointer,NativePointer;
        public int Id,Generation,Build,SourceUid;public Mob Native;
        public readonly ShrineLateDeathGuard.Evidence Proof=new();
        public bool CompletionCounted;public int LateNativeCalls,LateNativeReturns,LateNativeFaults;
    }
    static object Sync=>SyncField.GetValue(null);
    static IList Pairs=>(IList)PairsField.GetValue(null);
    static Dictionary<int,Mob> Forward=>(Dictionary<int,Mob>)ForwardField.GetValue(null);
    static Dictionary<Mob,int> Reverse=>(Dictionary<Mob,int>)ReverseField.GetValue(null);
    static IList Tracked=>(IList)TrackedField.GetValue(null);
    static HashSet<Mob> Pending=>(HashSet<Mob>)PendingField.GetValue(null);
    static IDictionary FirstFrames=>(IDictionary)FirstFrameField.GetValue(null);
    static IntPtr Pointer(HaxeProxyBase value)=>value?.HashlinkPointer??IntPtr.Zero;
    static T Read<T>(object pair,string name)=>(T)PairFields[name].GetValue(pair);
    static object Field(string name)=>(M.GetField(name,S)??throw new InvalidOperationException("Missing pinned field: "+name)).GetValue(null);
    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);
    static MethodInfo Method(Type type,string name,Type result,params Type[] args)
    {
        var method=type.GetMethod(name,S,null,args,null);
        if(method?.ReturnType!=result)throw new InvalidOperationException("Pinned method changed: "+name);
        return method;
    }
    static FieldInfo FieldInfo(string name,Type expected)
    {
        var field=M.GetField(name,S);
        if(field?.FieldType!=expected)throw new InvalidOperationException("Pinned field changed: "+name);
        return field;
    }
    static Type Companion(string type,string part,bool requireEnabled)
    {
        // CoopPack: the companion is a part of this assembly; it counts only while that part is present (not failed, not switched off).
        var target=global::CoopPackRuntime.PackParts.FindType(part,type)??throw new InvalidOperationException("Exact companion required: "+type);
        if(requireEnabled&&(!(bool)target.GetProperty("Ready",S).GetValue(null)||(bool)target.GetProperty("Disabled",S).GetValue(null)))
            throw new InvalidOperationException("Enabled companion required: "+type);
        return target;
    }
    public static void Install()
    {
        lock(InstallGate)
        {
            if(Published)return;
            if(InitializationError!=null)throw new InvalidOperationException(InitializationError);
            var harmony=new Harmony(Owner);var patched=new List<MethodInfo>();
            try
            {
                if(Hash(typeof(NetNode).Assembly.Location)!=BasePin||Hash(Path.Combine(Path.GetDirectoryName(typeof(Mob).Assembly.Location),"hlboot.dat"))!=NativePin)
                    throw new InvalidOperationException("Exact community0.5.1/native35.13.5 required");
                Source=Companion("ShrineReplicaPatch","CoopShrineFix",true);F=Companion("CoopSyncFixes","CoopSyncFixes",false);
                Companion("MobRegistrationPatch","CoopMobRegistrationFix",true);
                if(F.GetField("EliteDeathCompletionReady",S)?.GetValue(null) is not true)
                    throw new InvalidOperationException("FFF guarded native death completion required");
                LateShrineProxyCompatibility.ValidateLoaded(typeof(Mob).Assembly);
                M=typeof(NetNode).Assembly.GetType("DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization",true);
                SyncField=FieldInfo("Sync",typeof(object));ForwardField=FieldInfo("IdToMob",typeof(Dictionary<int,Mob>));
                ReverseField=FieldInfo("MobToId",typeof(Dictionary<Mob,int>));TrackedField=M.GetField("trackedMobs",S);
                PendingField=FieldInfo("s_pendingCulledMobDeaths",typeof(HashSet<Mob>));FirstFrameField=FieldInfo("s_pendingCulledMobDeathFirstFrame",typeof(Dictionary<Mob,double>));
                LedgerField=M.GetField("s_clientMobLifecycle",S)??throw new InvalidOperationException("Original death ledger missing");
                OriginalIsDead=LedgerField.FieldType.GetMethod("IsDead",I,null,new[]{typeof(int),typeof(int)},null);
                if(OriginalIsDead?.ReturnType!=typeof(bool))throw new InvalidOperationException("Original death ledger ABI changed");
                PairType=Source.GetNestedType("Pair",BindingFlags.NonPublic)??throw new InvalidOperationException("EA07 Pair missing");
                PairsField=Source.GetField("Pending",S)??throw new InvalidOperationException("EA07 Pair list missing");
                foreach(var name in new[]{"Net","Level","LevelPointer","MapPointer","Shrine","ShrinePointer","SourcePointer","SourceUid","ReplicaPointer","Native","NativePointer","Generation","Build","Id"})
                    PairFields.Add(name,PairType.GetField(name,I)??throw new InvalidOperationException("EA07 Pair field changed: "+name));
                PairContext=Method(Source,"Context",typeof(bool),PairType);PairValid=Method(Source,"ValidSource",typeof(bool),PairType,typeof(bool));
                PairActor=Method(Source,"NativeActor",typeof(bool),typeof(Mob),PairType,typeof(bool),typeof(bool));
                HasLadder=Method(Source,"HasLadder",typeof(bool),typeof(dc.hl.types.ArrayObj));
                Remember=Method(Source,"RememberReplica",typeof(void),typeof(Mob),typeof(NetNode.MobRegistryEntry));
                TryGetId=Method(M,"TryGetMobSyncId",typeof(bool),typeof(Mob),typeof(int).MakeByRefType());
                Dead=Method(F,"DeadOnClient",typeof(bool),typeof(int),typeof(int));
                AcceptedIntent=Method(F,"HasCurrentBuildDeathIntent",typeof(bool),typeof(int),typeof(int));
                NativeFault=Method(F,"EliteHasNativeFault",typeof(bool),typeof(Mob));
                Frame=Method(M,"GetCurrentFrame",typeof(double),typeof(Mob));Quiesced=Method(M,"IsSyncQuiescedForTransition",typeof(bool));
                Reset=Method(M,"ResetMobTrackingStateLocked",typeof(void));Flush=Method(M,"FlushPendingClientAuthoritativeDeaths",typeof(void));
                Run=Method(M,"RunAuthoritativeClientDeathNow",typeof(bool),typeof(Mob));
                DeathHook=M.GetMethod("Hook_Mob_onDie",S)??throw new InvalidOperationException("Original death hook missing");
                FFFNativeBridge=Method(F,"EliteInvokeOriginalDeath",typeof(void),typeof(object),typeof(Mob));
                Disabled=Environment.GetEnvironmentVariable("COOP_SHRINE_EARLY_DEATH_DISABLE")=="1";
                void Patch(MethodInfo target,string prefix=null,string postfix=null)
                {
                    harmony.Patch(target,prefix:prefix==null?null:new HarmonyMethod(typeof(ShrineLateDeathPatch).GetMethod(prefix,S)){priority=Priority.First+100},
                        postfix:postfix==null?null:new HarmonyMethod(typeof(ShrineLateDeathPatch).GetMethod(postfix,S)));
                    patched.Add(target);
                }
                Patch(Remember,postfix:nameof(CapturePairs));Patch(Flush,prefix:nameof(EnrollmentTick));
                Patch(Run,prefix:nameof(BeforePendingDeath));Patch(Reset,postfix:nameof(AfterReset));
                var existing=Harmony.GetPatchInfo(DeathHook)?.Transpilers.Where(p=>p.PatchMethod==F.GetMethod("EliteObserveOriginalDeathCallsite",S)).ToArray();
                if(existing==null||existing.Length!=1)throw new InvalidOperationException("Exact FFF native callsite guard required");
                BuildNativeBridges();
                harmony.Patch(DeathHook,transpiler:new HarmonyMethod(typeof(ShrineLateDeathPatch).GetMethod(nameof(ReplaceNativeCall),S)){after=new[]{existing[0].owner}});patched.Add(DeathHook);
                SubscribeHooks();
                if(patched.Any(p=>!Harmony.GetPatchInfo(p).Owners.Contains(Owner)))throw new InvalidOperationException("Late death hooks incomplete");
                Published=true;
            }
            catch(Exception error)
            {
                Published=false;InitializationError=error.Message;
                try{UnsubscribeHooks();}catch(Exception rollback){InitializationError+="; native rollback: "+rollback.Message;}
                foreach(var method in patched)try{harmony.Unpatch(method,HarmonyPatchType.All,Owner);}catch(Exception rollback){InitializationError+="; rollback: "+rollback.Message;}
                throw;
            }
        }
    }
    static bool Current(Receipt r)=>r!=null&&Pairs.Contains(r.Pair)&&ReferenceEquals(r.Net,ModEntry._net)&&r.Net.IsAlive&&!r.Net.IsHost&&
        (bool)PairContext.Invoke(null,new[]{r.Pair})&&Read<int>(r.Pair,"Id")==r.Id&&Read<int>(r.Pair,"Generation")==r.Generation&&
        Read<int>(r.Pair,"Build")==r.Build&&Read<IntPtr>(r.Pair,"SourcePointer")==r.SourcePointer&&Read<int>(r.Pair,"SourceUid")==r.SourceUid;
    static bool SourceCurrent(Receipt r)=>(bool)PairValid.Invoke(null,new[]{r.Pair,(object)false});
    static void CapturePairs()
    {
        if(!Published||Disabled)return;
        try
        {
            lock(Sync)
            {
                Prune();if(Pairs.Count>16)return;
                foreach(var pair in Pairs)
                {
                    if(Receipts.Any(r=>ReferenceEquals(r.Pair,pair)))continue;
                    if(Receipts.Count>=16)break;
                    var r=new Receipt {Pair=pair,Net=Read<NetNode>(pair,"Net"),Level=Read<Level>(pair,"Level"),Shrine=Read<EliteShrine>(pair,"Shrine"),
                        LevelPointer=Read<IntPtr>(pair,"LevelPointer"),MapPointer=Read<IntPtr>(pair,"MapPointer"),ShrinePointer=Read<IntPtr>(pair,"ShrinePointer"),
                        SourcePointer=Read<IntPtr>(pair,"SourcePointer"),SourceUid=Read<int>(pair,"SourceUid"),ReplicaPointer=Read<IntPtr>(pair,"ReplicaPointer"),
                        Id=Read<int>(pair,"Id"),Generation=Read<int>(pair,"Generation"),Build=Read<int>(pair,"Build")};
                    if(r.Id<=0||!Current(r)||!SourceCurrent(r)||r.Shrine.used)continue;
                    Receipts.Add(r);Interlocked.Increment(ref Captured);
                }
            }
        }
        catch(Exception error){Report(error,false);}
    }
    static void Prune()
    {
        for(int i=Receipts.Count-1;i>=0;i--)
        {
            var r=Receipts[i];
            // Once enrolled, retain the rejection identity until original cleanup
            // removes it. Expiration must not turn an owned stale actor into an
            // unrestricted original death invocation.
            if(r.Proof.Enrolled&&r.Native!=null&&Pending.Contains(r.Native))continue;
            if(!Current(r)){Receipts.RemoveAt(i);Interlocked.Increment(ref Expired);}
        }
    }
    static void AfterReset()
    {
        if(!Published)return;
        lock(Sync)Receipts.RemoveAll(r=>r.Native==null||!Pending.Contains(r.Native));
    }
    static bool OriginalDead(Receipt r)=>(bool)OriginalIsDead.Invoke(LedgerField.GetValue(null),new object[]{r.Generation,r.Id})&&
        (bool)Dead.Invoke(null,new object[]{r.Generation,r.Id}); // FFF intent alone is insufficient.
    static bool NoMap(IntPtr pointer)=>pointer!=IntPtr.Zero&&!Forward.Any(p=>Pointer(p.Value)==pointer)&&!Reverse.Any(p=>Pointer(p.Key)==pointer);
    static bool OldUntracked(Receipt r)=>NoMap(r.ReplicaPointer)&&!Tracked.Cast<Mob>().Any(m=>Pointer(m)==r.ReplicaPointer)&&
        !Pending.Any(m=>Pointer(m)==r.ReplicaPointer)&&!FirstFrames.Keys.Cast<Mob>().Any(m=>Pointer(m)==r.ReplicaPointer);
    static bool Unbound(Receipt r)
    {
        if(r.Native==null||Pointer(r.Native)!=r.NativePointer||r.NativePointer==r.ReplicaPointer||!NoMap(r.NativePointer))return false;
        if(Tracked.Cast<Mob>().Count(m=>Pointer(m)==r.NativePointer)!=1)return false;
        object[] args={r.Native,0};
        // On the pinned client this lookup cannot allocate a host ID. A true
        // result (including any original alias) rejects the late-death action.
        return !(bool)TryGetId.Invoke(null,args)&&NoMap(r.NativePointer);
    }
    static bool NativeReady(Receipt r)
    {
        var mob=r.Native;var game=Game.Class.ME;var level=r.Level;
        if(mob==null||!(bool)PairActor.Invoke(null,new object[]{mob,r.Pair,true,true})||mob.destroyed||mob.onDieDone||mob.life<=0||!mob.initDone||!mob.elite||mob.type?.ToString()!="Archer"||
            Pointer(mob._level)!=r.LevelPointer||Pointer(level.map)!=r.MapPointer||game==null||Pointer(level.game)!=Pointer(game)||
            Pointer(game.hero?._level)!=r.LevelPointer||game.hero.life<=0||game.curCine!=null||game.hasCinematic()||game.paused||
            (bool)Quiesced.Invoke(null,null)||mob.spr==null||!mob.isOnScreen||mob.isOutOfGame||!mob.canUpdate())return false;
        double a=mob.getHighestAffectDurationF(5),b=mob.getHighestAffectDurationF(8);
        return double.IsFinite(a)&&double.IsFinite(b)&&a<=0&&b<=0&&!(bool)NativeFault.Invoke(null,new object[]{mob});
    }
    static bool PendingReady(Receipt r)
    {
        // An original pending death may already have zero life. canUpdate and
        // aiLocked also depend on life; they are living-enrollment predicates,
        // not reasons to discard an accepted authoritative death.
        var mob=r.Native;var game=Game.Class.ME;var level=r.Level;
        if(mob==null||Pointer(mob)!=r.NativePointer||mob.destroyed||mob.onDieDone||!mob.initDone||!mob.elite||
            mob.type?.ToString()!="Archer"||!(bool)HasLadder.Invoke(null,new object[]{mob.loots})||
            Pointer(mob._level)!=r.LevelPointer||Pointer(level.map)!=r.MapPointer||game==null||Pointer(level.game)!=Pointer(game)||
            Pointer(game.hero?._level)!=r.LevelPointer||game.hero.life<=0||game.curCine!=null||game.hasCinematic()||game.paused||
            (bool)Quiesced.Invoke(null,null)||mob.spr==null||!mob.isOnScreen||mob.isOutOfGame)return false;
        double a=mob.getHighestAffectDurationF(5),b=mob.getHighestAffectDurationF(8);
        return double.IsFinite(a)&&double.IsFinite(b)&&a<=0&&b<=0&&!(bool)NativeFault.Invoke(null,new object[]{mob});
    }
    static bool PendingFree(Receipt r)=>!Pending.Any(m=>Pointer(m)==r.NativePointer)&&!FirstFrames.Keys.Cast<Mob>().Any(m=>Pointer(m)==r.NativePointer);
    static void EnrollmentTick()
    {
        if(!Published||Disabled)return;
        try
        {
            lock(Sync)
            {
                Prune();
                foreach(var r in Receipts)
                {
                    if(r.Proof.Enrolled)
                    {
                        if(!r.CompletionCounted&&r.LateNativeCalls==1&&r.LateNativeReturns==1&&r.LateNativeFaults==0&&
                            Current(r)&&r.Native!=null&&(r.Native.destroyed||r.Native.onDieDone)&&
                            !(bool)NativeFault.Invoke(null,new object[]{r.Native})&&OriginalDead(r))
                        {r.CompletionCounted=true;Interlocked.Increment(ref Completed);}
                        continue;
                    }
                    if(r.Proof.Terminal||!ShrineLateDeathGuard.PlainRetiredWithoutNativeDeath(r.Proof)||r.Proof.PresentationReturns!=1)continue;
                    if(!ShrineLateDeathGuard.Eligible(r.Proof,Current(r),SourceCurrent(r),OriginalDead(r),NativeReady(r),Unbound(r),OldUntracked(r),PendingFree(r)))continue;
                    double frame=(double)Frame.Invoke(null,new object[]{r.Native});
                    try
                    {
                        if(ShrineLateDeathGuard.Commit(Pending,FirstFrames,r.Native,frame))
                        {r.Proof.Enrolled=true;Interlocked.Increment(ref Enrolled);}
                    }
                    catch(Exception error){r.Proof.Terminal=true;Report(error,true);}
                }
            }
        }
        catch(Exception error){Report(error,false);}
    }
    static bool BeforePendingDeath(Mob mob,ref bool __result)
    {
        if(!Published||Disabled||mob==null)return true;
        try
        {
            lock(Sync)
            {
                var matches=Receipts.Where(r=>r.Proof.Enrolled&&Pointer(mob)==r.NativePointer).ToArray();
                if(matches.Length==0)return true;
                if(matches.Length==1)
                {
                    var r=matches[0];
                    if(Current(r)&&ReferenceEquals(mob,r.Native)&&OriginalDead(r)&&!r.Proof.Terminal&&Pending.Contains(mob))
                    {
                        if((mob.destroyed||mob.onDieDone)&&!(bool)NativeFault.Invoke(null,new object[]{mob}))return true;
                        // If a native body returned incomplete, do not duplicate
                        // loot by attempting another body. FFF's fault fence
                        // remains active as well; pending intent is retained.
                        if(r.LateNativeCalls==0&&SourceCurrent(r)&&Unbound(r)&&OldUntracked(r)&&PendingReady(r))return true;
                    }
                }
                __result=false;return false;
            }
        }
        catch(Exception error){Report(error,false);__result=false;return false;}
    }
    static void Report(Exception error,bool mutation)
    {
        long count=mutation?Interlocked.Increment(ref QueueErrors):Interlocked.Increment(ref Errors);
        if(count<=8)Serilog.Log.Warning("[CoopShrineDeathFix] {Stage} rejected: {Error}",mutation?"enrollment":"inspection",error.GetBaseException().Message);
    }
}
