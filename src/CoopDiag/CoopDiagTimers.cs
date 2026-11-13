using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using R=CoopDiagRules;

// Issue 16 (host HostFlush spikes) and issue 15 (the ~1 s hitch at the first partner avatar) stopwatches. Harmony
// prefix/finalizer pairs on the pinned base-mod methods; the bodies only take timestamps and add numbers, never call the
// game, and run in try/catch behind the "timing" fault gate. Everything here runs on the game thread
// (MobsSynchronization.OnFrameUpdateCore and the avatar creation path are game-thread only); calls from other threads
// are ignored.
public static class CoopDiagTimers
{
    static readonly double TickMs=1000.0/Stopwatch.Frequency;
    static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
    public static readonly CoopDiagFaultGate Gate=new("timing");
    public static volatile bool Enabled;
    static int GameThread;
    public static void SetGameThread(int id)=>Volatile.Write(ref GameThread,id);
    static bool OnGameThread=>Environment.CurrentManagedThreadId==Volatile.Read(ref GameThread);
    public static Func<int> TrackedCount=()=>-1;
    public static Func<int> ScratchCount=()=>-1;
    public static Func<double> RegistrySendFrame=()=>double.NaN;

    // ---------------- issue 16: one OnFrameUpdateCore = one frame of mob sync ----------------
    static bool inFrame;static long frameStart;static readonly double[] frameStep=new double[R.StepCount];static bool frameAnyStep,frameRegistrySent;
    static int frameCatchups,frameCatchupCandidates,frameBossPasses,frameActivePasses;static int fGc0,fGc1,fGc2;static long fAlloc;static TimeSpan fPause;
    // window (taken by the heartbeat)
    static readonly CoopDiagStat[] winStep=NewStats(R.StepCount);static readonly CoopDiagStat winTotal=new(),winCatchupPass=new();
    static long winFrames,winSpikes,winRegistrySends,winCatchups,winAlloc;static int winGc0,winGc1,winGc2;static double winPauseMs;
    static readonly CoopDiagLimiter spikeLimiter=new(R.SpikeCooldownSeconds);
    public static long FlushFrames,FlushSpikes,FlushWindows;   // totals since start (probe)
    static CoopDiagStat[] NewStats(int n){var a=new CoopDiagStat[n];for(int i=0;i<n;i++)a[i]=new CoopDiagStat();return a;}
    static double Now=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;

    public static void FrameCorePre()
    {
        if(!Enabled||Gate.Tripped)return;
        try
        {
            if(!OnGameThread)return;
            inFrame=true;frameStart=Stopwatch.GetTimestamp();Array.Clear(frameStep);frameAnyStep=frameRegistrySent=false;
            frameCatchups=frameCatchupCandidates=frameBossPasses=frameActivePasses=0;
            fGc0=GC.CollectionCount(0);fGc1=GC.CollectionCount(1);fGc2=GC.CollectionCount(2);fAlloc=GC.GetAllocatedBytesForCurrentThread();fPause=GC.GetTotalPauseDuration();
        }
        catch(Exception e){Fault(e);}
    }
    public static void FrameCoreFinal()
    {
        if(!inFrame)return;
        inFrame=false;
        if(!Enabled||Gate.Tripped||!frameAnyStep)return;   // client frames run none of the host steps
        try
        {
            int g0=GC.CollectionCount(0)-fGc0,g1=GC.CollectionCount(1)-fGc1,g2=GC.CollectionCount(2)-fGc2;
            long alloc=GC.GetAllocatedBytesForCurrentThread()-fAlloc;double pause=(GC.GetTotalPauseDuration()-fPause).TotalMilliseconds;
            double total=R.FlushTotal(frameStep);
            winFrames++;FlushFrames++;winTotal.Add(total);for(int i=0;i<R.StepCount;i++)if(frameStep[i]>0)winStep[i].Add(frameStep[i]);
            if(frameRegistrySent)winRegistrySends++;winCatchups+=frameCatchups;winGc0+=g0;winGc1+=g1;winGc2+=g2;winAlloc+=alloc;winPauseMs+=pause;
            if(R.IsFlushSpike(total))
            {
                winSpikes++;FlushSpikes++;
                if(spikeLimiter.Allow(Now,out int held))
                {
                    var s=new StringBuilder(260);
                    s.Append(R.Prefix).Append("FLUSH-SPIKE ms=").Append(R.Ms(total));
                    for(int i=0;i<R.StepCount;i++)s.Append(' ').Append(R.StepNames[i]).Append('=').Append(R.Ms(frameStep[i]));
                    s.Append(" regSent=").Append(R.Bool(frameRegistrySent)).Append(" catchup=").Append(frameCatchups.ToString(Inv)).Append('/').Append(frameCatchupCandidates.ToString(Inv))
                     .Append(" bossPass=").Append(frameBossPasses.ToString(Inv)).Append(" activePass=").Append(frameActivePasses.ToString(Inv))
                     .Append(" gc=").Append(g0.ToString(Inv)).Append('/').Append(g1.ToString(Inv)).Append('/').Append(g2.ToString(Inv))
                     .Append(" pause=").Append(R.Ms(pause)).Append(" alloc=").Append((alloc/1024.0).ToString("0.0",Inv)).Append("KB")
                     .Append(" tracked=").Append(TrackedCount().ToString(Inv)).Append(" held=").Append(held.ToString(Inv));
                    CoopDiagLog.Info(s.ToString());
                }
            }
            Gate.Ok();
        }
        catch(Exception e){Fault(e);}
    }
    public static void TimePre(out long __state){__state=Stopwatch.GetTimestamp();}
    static void Step(int index,long start)
    {
        if(!inFrame||!Enabled)return;
        try{if(!OnGameThread)return;frameStep[index]+=(Stopwatch.GetTimestamp()-start)*TickMs;frameAnyStep=true;}
        catch(Exception e){Fault(e);}
    }
    public static void DirtyFinal(long __state)=>Step(R.StepDirty,__state);
    public static void BossScanFinal(long __state)=>Step(R.StepBossScan,__state);
    public static void PriorityFinal(long __state)=>Step(R.StepPriority,__state);
    public static void TombstoneFinal(long __state)=>Step(R.StepTombstone,__state);
    public static void ActivationFinal(long __state)=>Step(R.StepActivation,__state);
    static double registryBefore;
    public static void RegistryPre(out long __state)
    {
        __state=Stopwatch.GetTimestamp();
        try{if(inFrame&&Enabled)registryBefore=RegistrySendFrame();}catch(Exception e){Fault(e);}
    }
    public static void RegistryFinal(long __state)
    {
        Step(R.StepRegistry,__state);
        try{if(inFrame&&Enabled){double after=RegistrySendFrame();if(!double.IsNaN(after)&&after!=registryBefore)frameRegistrySent=true;}}catch(Exception e){Fault(e);}
    }
    // FlushHostPriorityResyncPass(net,preferBoss,preferActive,includeAll): includeAll is the full catch-up pass (every 45
    // frames, 5 during the bootstrap; FlushHostPriorityResync IL_00E1/IL_01A6) that CoopSyncFixes Catchup.cs windows
    // to 48 mobs and uses to re-arm a full MOBREG. Its time is inside the "priority" step; here only kinds and counts.
    public static void PassPre(bool preferBoss,bool preferActive,bool includeAll,out long __state)
    {
        __state=Stopwatch.GetTimestamp();
        if(!inFrame||!Enabled)return;
        try{if(includeAll){frameCatchups++;frameCatchupCandidates=Math.Max(frameCatchupCandidates,ScratchCount());}else if(preferBoss)frameBossPasses++;else if(preferActive)frameActivePasses++;}
        catch(Exception e){Fault(e);}
    }
    public static void PassFinal(long __state,bool includeAll)
    {
        if(!inFrame||!Enabled||!includeAll)return;
        try{winCatchupPass.Add((Stopwatch.GetTimestamp()-__state)*TickMs);}catch(Exception e){Fault(e);}
    }
    // The heartbeat takes the window: one FLUSH line per period while the host ran mob sync frames, else null.
    public static string TakeFlushWindow(double windowSeconds)
    {
        if(winFrames==0){winSpikes=0;return null;}
        var s=new StringBuilder(320);
        s.Append(R.Prefix).Append("FLUSH win=").Append(R.Sec(windowSeconds)).Append(" frames=").Append(winFrames.ToString(Inv))
         .Append(" total=").Append(winTotal);
        for(int i=0;i<R.StepCount;i++)s.Append(' ').Append(R.StepNames[i]).Append('=').Append(winStep[i]);
        s.Append(" catchupPass=").Append(winCatchupPass).Append(" catchups=").Append(winCatchups.ToString(Inv)).Append(" regSends=").Append(winRegistrySends.ToString(Inv))
         .Append(" spikes=").Append(winSpikes.ToString(Inv)).Append(" gc=").Append(winGc0.ToString(Inv)).Append('/').Append(winGc1.ToString(Inv)).Append('/').Append(winGc2.ToString(Inv))
         .Append(" pause=").Append(R.Ms(winPauseMs)).Append(" alloc=").Append((winAlloc/1048576.0).ToString("0.00",Inv)).Append("MB tracked=").Append(TrackedCount().ToString(Inv));
        winFrames=winSpikes=winRegistrySends=winCatchups=winAlloc=0;winGc0=winGc1=winGc2=0;winPauseMs=0;winTotal.Reset();winCatchupPass.Reset();foreach(var st in winStep)st.Reset();
        FlushWindows++;
        return s.ToString();
    }

    // ---------------- issue 15: one EnsureClientKingSlot call = one avatar context ----------------
    public static readonly string[] AvatarParts={"create","check","init","gfx","deps","weapons","skin","head","label","dive","notify"};
    public const int ACreate=0,ACheck=1,AInit=2,AGfx=3,ADeps=4,AWeapons=5,ASkin=6,AHead=7,ALabel=8,ADive=9,ANotify=10;
    static int avDepth,avSlot;static long avStart,avAlloc,avJitMethods;static TimeSpan avJit,avPause;static int avGc0,avGc1,avGc2;
    static readonly double[] avMs=new double[AvatarParts.Length];static readonly int[] avN=new int[AvatarParts.Length];
    public static long Avatars;public static double LastAvatarTotalMs=-1,LastAvatarCreateMs=-1,MaxAvatarTotalMs=-1;public static int[] LastAvatarCounts=new int[0];
    public static Func<string> LevelName=()=>"-";

    public static void SlotPre(int slot,out long __state)
    {
        __state=Stopwatch.GetTimestamp();
        if(!Enabled||Gate.Tripped)return;
        try
        {
            if(!OnGameThread)return;
            if(avDepth++==0)
            {
                avSlot=slot;avStart=__state;Array.Clear(avMs);Array.Clear(avN);
                avGc0=GC.CollectionCount(0);avGc1=GC.CollectionCount(1);avGc2=GC.CollectionCount(2);avAlloc=GC.GetAllocatedBytesForCurrentThread();avPause=GC.GetTotalPauseDuration();
                avJit=System.Runtime.JitInfo.GetCompilationTime(true);avJitMethods=System.Runtime.JitInfo.GetCompiledMethodCount(true);
            }
        }
        catch(Exception e){Fault(e);}
    }
    public static void SlotFinal(long __state)
    {
        if(avDepth==0)return;
        try
        {
            if(!OnGameThread)return;
            if(--avDepth!=0)return;
            double total=(Stopwatch.GetTimestamp()-avStart)*TickMs;
            bool created=avN[ACreate]>0;
            if(!created&&total<R.AvatarSlowMs)return;
            double jit=(System.Runtime.JitInfo.GetCompilationTime(true)-avJit).TotalMilliseconds;long jitMethods=System.Runtime.JitInfo.GetCompiledMethodCount(true)-avJitMethods;
            int g0=GC.CollectionCount(0)-avGc0,g1=GC.CollectionCount(1)-avGc1,g2=GC.CollectionCount(2)-avGc2;
            long alloc=GC.GetAllocatedBytesForCurrentThread()-avAlloc;double pause=(GC.GetTotalPauseDuration()-avPause).TotalMilliseconds;
            long n=Interlocked.Increment(ref Avatars);
            LastAvatarTotalMs=total;if(total>MaxAvatarTotalMs)MaxAvatarTotalMs=total;
            if(created){LastAvatarCreateMs=avMs[ACreate];LastAvatarCounts=(int[])avN.Clone();}   // the last real creation (probe)
            var s=new StringBuilder(300);
            s.Append(R.Prefix).Append("AVATAR #").Append(n.ToString(Inv)).Append(" slot=").Append(avSlot.ToString(Inv)).Append(" created=").Append(R.Bool(created))
             .Append(" total=").Append(R.Ms(total));
            for(int i=0;i<AvatarParts.Length;i++)s.Append(' ').Append(AvatarParts[i]).Append('=').Append(R.Ms(avMs[i])).Append('/').Append(avN[i].ToString(Inv));
            s.Append(" jit=").Append(R.Ms(jit)).Append('/').Append(jitMethods.ToString(Inv)).Append(" gc=").Append(g0.ToString(Inv)).Append('/').Append(g1.ToString(Inv)).Append('/').Append(g2.ToString(Inv))
             .Append(" pause=").Append(R.Ms(pause)).Append(" alloc=").Append((alloc/1048576.0).ToString("0.00",Inv)).Append("MB lvl=").Append(R.Clean(LevelName(),48));
            CoopDiagLog.Info(s.ToString());
            Gate.Ok();
        }
        catch(Exception e){Fault(e);}
    }
    static void Part(int index,long start)
    {
        if(avDepth==0||!Enabled)return;
        try{if(!OnGameThread)return;avMs[index]+=(Stopwatch.GetTimestamp()-start)*TickMs;avN[index]++;}
        catch(Exception e){Fault(e);}
    }
    public static void CreateFinal(long __state)=>Part(ACreate,__state);
    public static void CheckFinal(long __state)=>Part(ACheck,__state);
    public static void InitFinal(long __state)=>Part(AInit,__state);
    public static void GfxFinal(long __state)=>Part(AGfx,__state);
    public static void DepsFinal(long __state)=>Part(ADeps,__state);
    public static void WeaponsFinal(long __state)=>Part(AWeapons,__state);
    public static void SkinFinal(long __state)=>Part(ASkin,__state);
    public static void HeadFinal(long __state)=>Part(AHead,__state);
    public static void LabelFinal(long __state)=>Part(ALabel,__state);
    public static void DiveFinal(long __state)=>Part(ADive,__state);
    public static void NotifyFinal(long __state)=>Part(ANotify,__state);

    static void Fault(Exception e)
    {
        if(Gate.Fault()){Enabled=false;CoopDiagLog.Warn(R.Prefix+"timing switched off after "+Gate.Total.ToString(Inv)+" faults: "+e.GetBaseException().Message);}
    }
}
