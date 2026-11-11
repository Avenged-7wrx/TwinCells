using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;

// Pure decisions of CoopOpt part OptMinimap (no game, mod, framework or Harmony types). Compiled into the add-on and,
// with OptMinimapChecks.cs only, by Test-Managed.ps1 -Only OptMinimap. The checks compare every decision with a literal
// transcription of the release body it replaces: Minimapreveal.Hook_MiniMap_postUpdate, release 0.5.1 IL 88207-88352
// (SHA 378BE6B0..., 420 IL bytes), on hlboot A37869A3....
// The whole loop frame after orig (decision, plain fast path, shadow frame, fallback after a fault, the bypass flag) is
// MinimapState below, generic over thin adapters; OptMinimap.cs only reads the release's values and supplies the
// adapters (the release's own calls), so the checks and the mutants exercise the sequencing that ships.
public static class OptMinimapRules
{
    // ---- shadow sampling (CoopLoadFix ChainCache model): the first 4096 loop frames, then every 256th; every one in
    // QA mode. A loop frame is a call that reaches the tracker loop (kingcircle != null).
    public const long WarmShadows=4096;
    public const long ShadowPeriod=256;
    public static bool ShouldShadow(long n,bool all)=>n>=1&&(all||n<=WarmShadows||n%ShadowPeriod==0);
    // The negative memo is the only route that rests on a premise (every push goes through the mod's track handler), so
    // it has warm-ups of its own that frames of other routes cannot use up: every negative-memo frame is compared until
    // RouteWarm of them were, and the first EpochWarm uses of every newly stored negative memo (a new key: array id,
    // length or track version) and of the memo after every king null -> non-null transition are compared as well.
    public const long RouteWarm=4096,EpochWarm=64;
    public sealed class ShadowSampler
    {
        public long NegShadows;   // negative-memo frames compared so far (session)
        public long EpochLeft;    // uses of the current negative memo still to be compared
        public bool KingBefore;   // the previous loop frame had a king client
        // Called once per loop frame, after the decision. memoStored: this frame stored a new negative memo.
        public bool Next(long frame,bool all,bool king,Route route,bool memoStored)
        {
            if(king&&!KingBefore)EpochLeft=EpochWarm;
            KingBefore=king;
            if(memoStored)EpochLeft=EpochWarm;
            bool shadow=ShouldShadow(frame,all);
            if(route==Route.NegativeMemo)
            {
                if(NegShadows<RouteWarm||EpochLeft>0)shadow=true;
                if(EpochLeft>0)EpochLeft--;
                if(shadow)NegShadows++;
            }
            return shadow;
        }
    }
    public static bool ShadowAllMode(string value)=>value=="all";
    public static bool SwitchSet(string value)=>value=="1";
    // Sub-switch of this part: no negative memo (every frame without a verified king tracker scans the whole array).
    public const string MemoSwitch="COOP_OPT_MINIMAP_MEMO_DISABLE";

    // ---- the decision: "does at least one tracker hold a KingSkin entity" ----
    // The release evaluates, for every tracker i < length: e = getDyn(i); skip null; mm = (MMTracker)e;
    // if king != null && Std.is(mm.e, KingSkin.Class) then write every marker. The writes are the same for every
    // matching tracker, so the frame's result depends only on (king != null) && (exists such a tracker).
    // IsKingAt(i) is that per-tracker test (getDyn + null test + cast + Std.is); it may throw, like the release's.
    public interface IKingTrackers{bool IsKingAt(int index);}

    // Index: where a king tracker was last seen (-1: none). It is only a hint: a positive answer is accepted only after
    // IsKingAt(Index) said so in this very frame.
    // Negative memo: a full scan found no king tracker in the array with this id and length while the track counter
    // had this version. Trackers are pushed only by MiniMap.track (every call bumps the version first) and a tracker's
    // entity never changes, so with the same (id, length, version) the array still holds no king tracker. Used only
    // when the caller allows it (track handler checked, memo switch not set).
    public struct Memo
    {
        public int Index;public bool NegValid;public long NegId;public int NegLength;public long NegVersion;
        public static Memo Empty=>new Memo{Index=-1};
    }
    public enum Route{KingNull,IndexHit,NegativeMemo,ScanFound,ScanNone}
    public enum Rescan{None,IndexMiss,NoMemo,MemoOff,IdChanged,LengthChanged,VersionChanged}

    // A throw from IsKingAt propagates; the memo is then empty (no index, no negative answer), so the next frame
    // scans the whole array again.
    public static Route Decide<T>(ref Memo m,bool kingPresent,int length,long id,long version,bool memoAllowed,ref T trackers,
        out bool any,out int scanned,out Rescan why) where T:struct,IKingTrackers
    {
        scanned=0;why=Rescan.None;any=false;
        if(!kingPresent)return Route.KingNull;                 // the release writes nothing when king == null
        int index=m.Index;m.Index=-1;
        if(index>=0&&index<length)
        {
            scanned++;
            if(trackers.IsKingAt(index)){m.Index=index;any=true;return Route.IndexHit;}
        }
        if(index>=0)why=Rescan.IndexMiss;
        else if(!memoAllowed)why=Rescan.MemoOff;
        else if(!m.NegValid)why=Rescan.NoMemo;
        else if(m.NegId!=id)why=Rescan.IdChanged;
        else if(m.NegLength!=length)why=Rescan.LengthChanged;
        else if(m.NegVersion!=version)why=Rescan.VersionChanged;
        else return Route.NegativeMemo;
        m.NegValid=false;
        for(int i=0;i<length;i++)
        {
            scanned++;
            if(trackers.IsKingAt(i)){m.Index=i;any=true;return Route.ScanFound;}
        }
        if(memoAllowed){m.NegValid=true;m.NegId=id;m.NegLength=length;m.NegVersion=version;}
        return Route.ScanNone;
    }
    // The shadow's independent answer: a fresh scan of every tracker, no memo, no hint.
    public static bool FullScan<T>(int length,ref T trackers,out int index) where T:struct,IKingTrackers
    {
        for(int i=0;i<length;i++)if(trackers.IsKingAt(i)){index=i;return true;}
        index=-1;return false;
    }
    // A shadow frame's decision agrees with the fresh scan; with no king the decision must be "no writes".
    public static bool FlagAgrees(bool kingPresent,bool decided,bool fullScan)=>kingPresent?decided==fullScan:!decided;

    // ---- the writes: once per frame instead of once per king tracker ----
    // Release IL_0140-IL_0192, run for each king tracker: clients = ModEntry.clients; for k < clients.Length:
    // c = clients[k]; if c != null && _kingMarkers.TryGetValue(k, out m) { m.x = (double)c.cx; m.y = (double)c.cy; }
    // Nothing in between writes clients, cx / cy or the markers, and x / y are raw field writes, so a second pass
    // writes the same values again. The plan is built first (reads only) and applied afterwards in the same order.
    // Open() reads clients / _kingMarkers (release IL_0140 / IL_0158) when a plan is built; Markers() is _kingMarkers
    // for a shadow frame's snapshot (null allowed).
    public interface IPlanSource<TM>{void Open(); int ClientCount{get;} bool HasClient(int k); bool TryMarker(int k,out TM marker); double X(int k); double Y(int k); Dictionary<int,TM> Markers();}
    public interface IMarkerAccess<TM>{double X(TM m); double Y(TM m); void SetX(TM m,double v); void SetY(TM m,double v);}
    public sealed class PlanBuffer<TM> where TM:class
    {
        public TM[] M=new TM[4];public double[] X=new double[4],Y=new double[4];public int Count;
        public int Build<TS>(ref TS s) where TS:struct,IPlanSource<TM>
        {
            Count=0;
            s.Open();
            int n=s.ClientCount;
            for(int k=0;k<n;k++)
            {
                if(!s.HasClient(k))continue;
                if(!s.TryMarker(k,out var m))continue;
                if(Count==M.Length){Array.Resize(ref M,Count*2);Array.Resize(ref X,Count*2);Array.Resize(ref Y,Count*2);}
                M[Count]=m;X[Count]=s.X(k);Y[Count]=s.Y(k);Count++;
            }
            return Count;
        }
        public void Apply<TA>(ref TA a) where TA:struct,IMarkerAccess<TM>
        {
            for(int i=0;i<Count;i++){a.SetX(M[i],X[i]);a.SetY(M[i],Y[i]);}
        }
        // No marker proxy is kept alive between frames.
        public void Clear(){Array.Clear(M,0,Count);Count=0;}
    }

    // ---- shadow comparison of the observable output (the markers' x / y) ----
    // Before the release body runs every marker is set to Sentinel; afterwards a coordinate still holding it was not
    // written by the release and gets its value from before back (so the final state is exactly the release's).
    // The release writes (double)Entity.cx / cy, integral and inside the int range, so it never writes the sentinel.
    public const long SentinelBits=unchecked((long)0xFE3C_0FFE_E0DD_BA11UL);
    public static double Sentinel=>BitConverter.Int64BitsToDouble(SentinelBits);
    public static bool IsSentinel(double v)=>BitConverter.DoubleToInt64Bits(v)==SentinelBits;
    public static bool TheirsWritten(double x,double y)=>!IsSentinel(x)||!IsSentinel(y);
    public static bool SameBits(double a,double b)=>BitConverter.DoubleToInt64Bits(a)==BitConverter.DoubleToInt64Bits(b);
    // One marker: written by both with bit-identical coordinates, or written by neither.
    public static bool MarkerAgrees(bool mineWritten,double mineX,double mineY,bool theirsWritten,double theirX,double theirY)
        =>mineWritten==theirsWritten&&(!mineWritten||SameBits(mineX,theirX)&&SameBits(mineY,theirY));
    // A shadow frame passes when the flag agrees, every marker agrees, every planned marker was in the snapshot and the
    // release body did not throw (an exception the fast path would have hidden is a difference too).
    public static bool ShadowAgrees(bool flagOk,bool markersOk,bool planCovered,bool releaseThrew)=>flagOk&&markersOk&&planCovered&&!releaseThrew;

    // One shadow frame: snapshot (every marker of _kingMarkers, once per object), what the fast path would leave
    // (its plan applied in order onto the snapshot), sentinels, then -- after the release body -- compare and restore.
    public sealed class ShadowFrame<TM> where TM:class
    {
        public TM[] Snap=new TM[4];public double[] PreX=new double[4],PreY=new double[4],MineX=new double[4],MineY=new double[4];public bool[] MineW=new bool[4];
        public int Count;public bool Armed;
        public int Find(TM m){for(int i=0;i<Count;i++)if(ReferenceEquals(Snap[i],m))return i;return -1;}
        public void Begin(){Clear();}
        public void Add(TM m,double x,double y)
        {
            if(m==null||Find(m)>=0)return;
            if(Count==Snap.Length)
            {
                int n=Count*2;
                Array.Resize(ref Snap,n);Array.Resize(ref PreX,n);Array.Resize(ref PreY,n);Array.Resize(ref MineX,n);Array.Resize(ref MineY,n);Array.Resize(ref MineW,n);
            }
            Snap[Count]=m;PreX[Count]=x;PreY[Count]=y;MineW[Count]=false;MineX[Count]=x;MineY[Count]=y;Count++;
        }
        // False when the plan names a marker that is not in the snapshot (cannot happen: both read _kingMarkers).
        public bool Mine(PlanBuffer<TM> plan)
        {
            bool covered=true;
            for(int i=0;i<plan.Count;i++)
            {
                int j=Find(plan.M[i]);
                if(j<0){covered=false;continue;}
                MineW[j]=true;MineX[j]=plan.X[i];MineY[j]=plan.Y[i];
            }
            return covered;
        }
        public void Arm<TA>(ref TA a) where TA:struct,IMarkerAccess<TM>
        {
            Armed=true;
            for(int i=0;i<Count;i++){a.SetX(Snap[i],Sentinel);a.SetY(Snap[i],Sentinel);}
        }
        // After the release body: compare every marker and give back the value from before to every coordinate the
        // release did not write. Returns whether all markers agree; theirs = markers the release wrote.
        public bool Settle<TA>(ref TA a,out int theirs) where TA:struct,IMarkerAccess<TM>
        {
            bool ok=true;theirs=0;
            for(int i=0;i<Count;i++)
            {
                double tx=a.X(Snap[i]),ty=a.Y(Snap[i]);
                bool written=TheirsWritten(tx,ty);
                if(written)theirs++;
                if(IsSentinel(tx))a.SetX(Snap[i],PreX[i]);
                if(IsSentinel(ty))a.SetY(Snap[i],PreY[i]);
                if(!MarkerAgrees(MineW[i],MineX[i],MineY[i],written,tx,ty))ok=false;
            }
            Armed=false;
            return ok;
        }
        // Safety net (finally): while armed, every coordinate still holding the sentinel gets its value back.
        public void Restore<TA>(ref TA a) where TA:struct,IMarkerAccess<TM>
        {
            if(!Armed)return;
            Armed=false;
            for(int i=0;i<Count;i++)
            {
                if(IsSentinel(a.X(Snap[i])))a.SetX(Snap[i],PreX[i]);
                if(IsSentinel(a.Y(Snap[i])))a.SetY(Snap[i],PreY[i]);
            }
        }
        public void Clear(){Array.Clear(Snap,0,Count);Count=0;Armed=false;}
    }

    // ---- a fault of the fast path after orig ran: the release body (with a no-op orig) runs instead ----
    // The release body ran clean: the fast path failed where the original does not -> switched off for the session.
    // The release body threw too: the original throws here as well; its exception goes on to the game unchanged
    // (counted; repeated ones trip the fault gate).
    public enum FaultAction{SwitchOff,Rethrow}
    public static FaultAction AfterFault(bool releaseThrew)=>releaseThrew?FaultAction.Rethrow:FaultAction.SwitchOff;

    // ---- the release body run by this part itself (shadow frames, fallback): the handler method entered with a no-op
    // orig while InRelease is set, so the part's prefix lets the call through. ----
    [ThreadStatic] static bool t_inRelease;
    public static bool InRelease=>t_inRelease;
    public interface IReleaseBody{void Run();}
    // Runs the release body once; returns what it threw (null: nothing). InRelease is cleared whatever happens.
    public static Exception RunRelease<TR>(ref TR body) where TR:struct,IReleaseBody
    {
        t_inRelease=true;
        try{body.Run();return null;}
        catch(Exception e){return e;}
        finally{t_inRelease=false;}
    }

    // ---- one loop frame (kingcircle != null) after orig: what OptMinimap.Body runs ----
    public struct LoopResult
    {
        public Route Route;public Rescan Why;public int Tested;public bool Any;public int Writes;
        public bool Shadowed,ShadowOk,FlagOk,MarkersOk,Covered,Full;public int FullIndex,Theirs;public Exception ReleaseError;
        public long MineTicks,MineBytes,ReleaseTicks,ReleaseBytes;   // shadow frames, when timed
    }
    public sealed class MinimapState<TM> where TM:class
    {
        public Memo Memo=Memo.Empty;
        public readonly PlanBuffer<TM> Plan=new();
        public readonly ShadowFrame<TM> Frame=new();
        public readonly ShadowSampler Sampler=new();
        public long LoopFrames,LastId,LastVersion;public int LastLength;

        // First step of every loop frame: counts it, and checks the memo's premise against the previous loop frame.
        // Returns true when the same tracker array grew while the track counter stood still (a push the mod's track
        // handler did not see).
        public bool Observe(long id,int length,long version,out long frame)
        {
            frame=++LoopFrames;
            bool unseen=PushWithoutBump(LastId,LastLength,LastVersion,id,length,version);
            LastId=id;LastLength=length;LastVersion=version;
            return unseen;
        }

        // The decision, then either the plain fast path (the planned marker writes, once) or a shadow frame: the fast
        // path plans its writes without applying them; every marker is snapshotted, set to the sentinel; the release
        // body runs; every marker is compared and every coordinate the release did not write gets its value back, so
        // the frame ends in exactly the release's state. A throw from the decision or the plan propagates (the caller
        // then runs Fault); the release body's own exception is returned in ReleaseError.
        public LoopResult Run<TT,TS,TA,TR>(bool king,int length,long id,long version,bool memoUsable,bool shadowAll,bool timed,
            ref TT trackers,ref TS source,ref TA access,ref TR release)
            where TT:struct,IKingTrackers where TS:struct,IPlanSource<TM> where TA:struct,IMarkerAccess<TM> where TR:struct,IReleaseBody
        {
            var r=new LoopResult{FullIndex=-1};
            long t0=timed?Stopwatch.GetTimestamp():0,b0=timed?GC.GetAllocatedBytesForCurrentThread():0;
            r.Route=Decide(ref Memo,king,length,id,version,memoUsable,ref trackers,out r.Any,out r.Tested,out r.Why);
            r.Shadowed=Sampler.Next(LoopFrames,shadowAll,king,r.Route,r.Route==Route.ScanNone&&Memo.NegValid);
            if(!r.Shadowed)
            {
                if(r.Any){Plan.Build(ref source);Plan.Apply(ref access);r.Writes=Plan.Count;Plan.Clear();}
                return r;
            }
            if(r.Any)Plan.Build(ref source);
            if(timed){r.MineTicks=Stopwatch.GetTimestamp()-t0;r.MineBytes=GC.GetAllocatedBytesForCurrentThread()-b0;}
            Frame.Begin();
            var markers=source.Markers();
            if(markers!=null)foreach(var kv in markers){var m=kv.Value;if(m!=null)Frame.Add(m,access.X(m),access.Y(m));}
            r.Covered=Frame.Mine(Plan);
            if(king)r.Full=FullScan(length,ref trackers,out r.FullIndex);
            r.FlagOk=FlagAgrees(king,r.Any,r.Full);
            try
            {
                Frame.Arm(ref access);
                long t1=timed?Stopwatch.GetTimestamp():0,b1=timed?GC.GetAllocatedBytesForCurrentThread():0;
                r.ReleaseError=RunRelease(ref release);
                if(timed){r.ReleaseTicks=Stopwatch.GetTimestamp()-t1;r.ReleaseBytes=GC.GetAllocatedBytesForCurrentThread()-b1;}
                r.MarkersOk=Frame.Settle(ref access,out r.Theirs);
            }
            finally{Frame.Restore(ref access);Frame.Clear();Plan.Clear();}
            r.ShadowOk=ShadowAgrees(r.FlagOk,r.MarkersOk,r.Covered,r.ReleaseError!=null);
            return r;
        }

        // A fault of the fast path after orig ran: nothing of the fast path's state is kept and the release body runs
        // instead, so the frame has the release's result. Returns the release body's exception (null: none).
        public Exception Fault<TR>(ref TR release,out FaultAction action) where TR:struct,IReleaseBody
        {
            Memo=Memo.Empty;Plan.Clear();Frame.Clear();
            var error=RunRelease(ref release);
            action=AfterFault(error!=null);
            return error;
        }
    }

    // ---- the negative memo's precondition: MiniMap.track (HL f@3307) has exactly one handler, the mod's ----
    public const int TrackFunction=3307,PostUpdateFunction=3327;
    public static bool TrackHandlerSole(int handlers,bool modHandler,int findex)=>handlers==1&&modHandler&&findex==TrackFunction;
    // The handler list is checked on the first loop frame and then every GuardPeriod-th one; the cached list
    // reference is checked on every use of the negative memo.
    public const long GuardPeriod=1024;
    public static bool GuardDue(long loopFrame)=>loopFrame==1||(loopFrame>1&&loopFrame%GuardPeriod==0);

    // Runtime check of the memo's premise on every loop frame: the same array grew while the track counter stood still
    // = a push the mod's track handler did not see. The negative memo is then switched off for the session.
    public static bool PushWithoutBump(long lastId,int lastLength,long lastVersion,long id,int length,long version)
        =>lastId!=0&&lastId==id&&length>lastLength&&version==lastVersion;

    // ---- the generated proxy members the fast path calls, checked by shape (not by file hash: GameProxy.dll is
    // generated on each machine). Same members as the release body calls. ----
    public const byte OpLdArg0=0x02,OpLdArg1=0x03,OpLdStr=0x72,OpLdSFldA=0x7F,OpCall=0x28,OpRet=0x2A,OpLdcI4=0x20;
    // ldarg.0; ldstr name; ldsflda cache; call HaxeProxyHelper.Get(Value)FieldById<T>; ret
    public static bool FieldGetterShape(byte[] il)=>il!=null&&il.Length==17&&il[0]==OpLdArg0&&il[1]==OpLdStr&&il[6]==OpLdSFldA&&il[11]==OpCall&&il[16]==OpRet;
    public const int GetterStringAt=2,GetterHelperAt=12;
    // ldarg.0; ldarg.1; ldstr name; ldsflda cache; call HaxeProxyHelper.SetValueFieldById<T>; ret
    public static bool FieldSetterShape(byte[] il)=>il!=null&&il.Length==18&&il[0]==OpLdArg0&&il[1]==OpLdArg1&&il[2]==OpLdStr&&il[7]==OpLdSFldA&&il[12]==OpCall&&il[17]==OpRet;
    public const int SetterStringAt=3,SetterHelperAt=13;
    // ldc.i4 global; ldsflda cache; call HaxeProxyHelper.GetGlobal; ret
    public static bool GlobalGetterShape(byte[] il,int global)=>il!=null&&il.Length==16&&il[0]==OpLdcI4&&Int32At(il,1)==global&&il[5]==OpLdSFldA&&il[10]==OpCall&&il[15]==OpRet;
    // A proxy method / hook accessor that starts with ldc.i4 <findex> names that HL function.
    public static bool StartsWithFunction(byte[] il,int findex)=>il!=null&&il.Length>=5&&il[0]==OpLdcI4&&Int32At(il,1)==findex;
    public static int Int32At(byte[] il,int at)=>il[at]|(il[at+1]<<8)|(il[at+2]<<16)|(il[at+3]<<24);
    public const int StdGlobal=1008,KingSkinGlobal=2876,ArrayObjGetDyn=71;

    // ---- emitted stubs for the generated orig delegate type (dc.ui.hud.Hook_MiniMap.orig_postUpdate cannot be named
    // at compile time: it derives from System.Private.CoreLib's MulticastDelegate, CS0012). The checks run the same
    // stubs on a model delegate of the same shape: ctor(object, IntPtr), void Invoke(self). ----
    public static MethodInfo OrigInvoke<TSelf>(Type origType)
    {
        var invoke=origType!=null&&typeof(MulticastDelegate).IsAssignableFrom(origType)?origType.GetMethod("Invoke",BindingFlags.Instance|BindingFlags.Public|BindingFlags.DeclaredOnly):null;
        var ps=invoke?.GetParameters();
        if(invoke==null||invoke.ReturnType!=typeof(void)||ps.Length!=1||ps[0].ParameterType!=typeof(TSelf)||origType.GetConstructor(new[]{typeof(object),typeof(IntPtr)})==null)
            throw new InvalidOperationException("orig delegate signature changed: "+origType);
        return invoke;
    }
    // orig.Invoke(self) on an orig delegate held as object: castclass + callvirt Invoke (the release's IL_0002).
    public static Action<object,TSelf> Invoker<TSelf>(Type origType)
    {
        var invoke=OrigInvoke<TSelf>(origType);
        var dm=new DynamicMethod("OptMinimap_invokeOrig",typeof(void),new[]{typeof(object),typeof(TSelf)},typeof(OptMinimapRules).Module,true);
        var g=dm.GetILGenerator();g.Emit(OpCodes.Ldarg_0);g.Emit(OpCodes.Castclass,origType);g.Emit(OpCodes.Ldarg_1);g.Emit(OpCodes.Callvirt,invoke);g.Emit(OpCodes.Ret);
        return (Action<object,TSelf>)dm.CreateDelegate(typeof(Action<object,TSelf>));
    }
    // An orig delegate that does nothing (static void noop(TSelf)), typed as the generated orig delegate.
    public static object Noop<TSelf>(Type origType,MethodInfo noop)
    {
        OrigInvoke<TSelf>(origType);
        var ps=noop?.GetParameters();
        if(noop==null||!noop.IsStatic||noop.ReturnType!=typeof(void)||ps.Length!=1||ps[0].ParameterType!=typeof(TSelf))throw new InvalidOperationException("no-op orig signature: "+noop);
        return Delegate.CreateDelegate(origType,noop);
    }
    // inst.body(orig, self) with orig held as object: the handler method itself (so its Harmony patches run).
    public static Action<TInst,object,TSelf> BodyCaller<TInst,TSelf>(MethodInfo body,Type origType)
    {
        OrigInvoke<TSelf>(origType);
        var ps=body?.GetParameters();
        if(body==null||body.IsStatic||body.DeclaringType!=typeof(TInst)||body.ReturnType!=typeof(void)||ps.Length!=2||ps[0].ParameterType!=origType||ps[1].ParameterType!=typeof(TSelf))
            throw new InvalidOperationException("handler signature changed: "+body);
        var dm=new DynamicMethod("OptMinimap_callBody",typeof(void),new[]{typeof(TInst),typeof(object),typeof(TSelf)},typeof(OptMinimapRules).Module,true);
        var g=dm.GetILGenerator();
        g.Emit(OpCodes.Ldarg_0);g.Emit(OpCodes.Ldarg_1);g.Emit(OpCodes.Castclass,origType);g.Emit(OpCodes.Ldarg_2);g.Emit(OpCodes.Callvirt,body);g.Emit(OpCodes.Ret);
        return (Action<TInst,object,TSelf>)dm.CreateDelegate(typeof(Action<TInst,object,TSelf>));
    }

    // ---- patches on the two targets: every patch must be ours ----
    public static bool OnlyOwner(IEnumerable<string> owners,string own)
    {
        if(owners==null)return true;
        foreach(var o in owners)if(o!=own)return false;
        return true;
    }
    public static bool ExactlyOnce(int ownPrefixes,int ownPostfixes,int ownTranspilers,int ownFinalizers)=>ownPrefixes==1&&ownPostfixes==0&&ownTranspilers==0&&ownFinalizers==0;

    // ---- pins ----
    public static string Sha256(byte[] data)=>Convert.ToHexString(SHA256.HashData(data));
    public static bool PinMatches(byte[] il,int length,string sha)=>il!=null&&il.Length==length&&string.Equals(Sha256(il),sha,StringComparison.Ordinal);
    public static bool IsPinText(string sha)
    {
        if(sha==null||sha.Length!=64)return false;
        foreach(char c in sha)if(!(c>='0'&&c<='9'||c>='A'&&c<='F'))return false;
        return true;
    }
    // Release 0.5.1 IL bodies the part rests on (name, IL length, SHA-256 of the IL bytes).
    public static readonly (string Name,int Length,string Sha)[] ModPins=
    {
        ("Hook_MiniMap_postUpdate",420,"ACFF9C279B115A27AB6B6AC7D8C4A8A9C252B9338E44D256831CD74FB71C3BD5"),
        ("Hook_MiniMap_track",29,"543C9611BF536880FFB96DD5DA98E2F47628FD5C6AA3110FFAABEAA5A38E06C9"),
        ("GetPrimaryClient",59,"E0C58113E56C36F5F2C6B8290786F6499543BDE38F51545E44EC20ECAC4E4871"),
        ("Hook_MiniMap_initContainers",200,"F2B243A3DCF8C3A61A6E624B035DAEDFCBF6B884DA3265B3D53B87BC89E0D27A"),
    };
    public static bool TryPin(string name,out int length,out string sha)
    {
        foreach(var p in ModPins)if(p.Name==name){length=p.Length;sha=p.Sha;return true;}
        length=0;sha=null;return false;
    }

    // ---- stats ----
    public sealed class CounterSet
    {
        public readonly string[] Names;public readonly long[] V;readonly long[] _last;
        public CounterSet(params string[] names){Names=names;V=new long[names.Length];_last=new long[names.Length];}
        public long Total(int i)=>System.Threading.Volatile.Read(ref V[i]);
        public long Delta(int i){long now=System.Threading.Volatile.Read(ref V[i]);long d=now-_last[i];_last[i]=now;return d;}
        public string Deltas()
        {
            var sb=new StringBuilder();
            for(int i=0;i<Names.Length;i++)
            {
                long now=System.Threading.Volatile.Read(ref V[i]);
                if(sb.Length>0)sb.Append(' ');
                sb.Append(Names[i]).Append('=').Append((now-_last[i]).ToString(CultureInfo.InvariantCulture));
                _last[i]=now;
            }
            return sb.ToString();
        }
    }
    public static double PerCall(long total,long calls,double scale)=>calls>0?total*scale/calls:0;
    public const int StatsPeriodSeconds=5,VerifyPeriodSeconds=2,QuietHintSeconds=180;
    public static bool Due(long tick,int period)=>tick>0&&tick%period==0;
    public static string RouteName(Route r)=>r switch{Route.KingNull=>"kingNull",Route.IndexHit=>"indexHit",Route.NegativeMemo=>"negMemo",Route.ScanFound=>"scanFound",_=>"scanNone"};
}
