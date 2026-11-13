using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using R=CoopDiagRules;

// CoopDiag (Alpha2 unit U13). Observation only:
//   - heartbeat: a postfix on the base mod's ungated per-frame callback ModEntry.OnFrameUpdate (Priority.Last; the same
//     anchor CoopDialogPresenceFix uses) writes one "[CoopDiag] HB" line every 5 s while a session or run is active
//     (once a minute otherwise): role, frames and hero-tick-ready frames, the longest frame gap, level / sub-level /
//     level identity token / ROOM marker, Game.curCine type, uniqId and age, downed state, age and count of received
//     lines, the DLC install mask (issue 13), the gamepad name and, on boss levels, boss life and targets;
//   - received lines: a prefix on NetNode.TryHandleFastPathLine (every received line of both transports passes it
//     first, on the network thread) stores only a timestamp and a count;
//   - hero tick: a postfix on ModEntry.IsHeroRuntimeReadyForCoop counts the frames in which it answered true;
//   - watchdog: CoopDiagWatchdog (timer thread);
//   - boss trace (host): one "[CoopDiag] BOSS" line per second while a player is downed on a boss level (issue 9);
//   - issue 16 / 15 stopwatches: CoopDiagTimers.
// It writes nothing into the game, the base mod, the settings or the wire.
public static class CoopDiagHooks
{
    static readonly object InstallGate=new();
    static readonly double TickMs=1000.0/Stopwatch.Frequency;
    static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
    public static readonly CoopDiagFaultGate FrameGate=new("heartbeat"),ReadyGate=new("ready"),BossGate=new("boss");
    static volatile bool Published;
    public static bool Ready=>Published;
    public static bool Disabled{get;private set;}
    public static string InitializationError{get;private set;}
    public static string ProxySymbolic{get;private set;}
    public static string ProxyRaw{get;private set;}
    public static int Period{get;private set;}=R.DefaultHeartbeatSeconds;
    public static bool TimingOn{get;private set;}
    public static bool WatchdogOn{get;private set;}
    public static bool BossOn{get;private set;}
    public static readonly List<string> PatchedKeys=new();

    public static void Install()
    {
        lock(InstallGate)
        {
            if(Published)return;
            if(InitializationError!=null)throw new InvalidOperationException("Earlier CoopDiag initialization failed: "+InitializationError);
            if(R.IsOn(Environment.GetEnvironmentVariable(R.DisableSwitch))){Disabled=true;return;}
            Harmony harmony=null;
            try
            {
                CoopDiagPins.Verify(typeof(ModEntry).Assembly,typeof(HaxeProxy.Runtime.HaxeProxyBase).Assembly,
                    Path.Combine(Path.GetDirectoryName(typeof(dc.en.Hero).Assembly.Location),"hlboot.dat"));
                ProxySymbolic=CoopDiagProxyCheck.Validate(typeof(dc.pr.Game).Assembly,out var raw);ProxyRaw=raw;
                CoopDiagSnapshot.Bind();
                Period=R.ParseHeartbeatSeconds(Environment.GetEnvironmentVariable(R.PeriodSetting));
                TimingOn=!R.IsOn(Environment.GetEnvironmentVariable(R.TimingSwitch));
                WatchdogOn=!R.IsOn(Environment.GetEnvironmentVariable(R.WatchdogSwitch));
                BossOn=!R.IsOn(Environment.GetEnvironmentVariable(R.BossSwitch));
                harmony=new Harmony(R.HarmonyId);
                PatchedKeys.Clear();
                Patch(harmony,"frame",post:nameof(AfterFrame),postPriority:Priority.Last);
                Patch(harmony,"rx",pre:nameof(BeforeRx),prePriority:Priority.First);
                Patch(harmony,"ready",post:nameof(AfterReady));
                if(TimingOn)
                {
                    var t=typeof(CoopDiagTimers);
                    Patch(harmony,"frameCore",t,pre:nameof(CoopDiagTimers.FrameCorePre),fin:nameof(CoopDiagTimers.FrameCoreFinal));
                    Patch(harmony,"dirty",t,pre:nameof(CoopDiagTimers.TimePre),fin:nameof(CoopDiagTimers.DirtyFinal));
                    Patch(harmony,"bossScan",t,pre:nameof(CoopDiagTimers.TimePre),fin:nameof(CoopDiagTimers.BossScanFinal));
                    Patch(harmony,"registry",t,pre:nameof(CoopDiagTimers.RegistryPre),fin:nameof(CoopDiagTimers.RegistryFinal));
                    Patch(harmony,"priority",t,pre:nameof(CoopDiagTimers.TimePre),fin:nameof(CoopDiagTimers.PriorityFinal));
                    Patch(harmony,"pass",t,pre:nameof(CoopDiagTimers.PassPre),fin:nameof(CoopDiagTimers.PassFinal));
                    Patch(harmony,"tombstone",t,pre:nameof(CoopDiagTimers.TimePre),fin:nameof(CoopDiagTimers.TombstoneFinal));
                    Patch(harmony,"activation",t,pre:nameof(CoopDiagTimers.TimePre),fin:nameof(CoopDiagTimers.ActivationFinal));
                    Patch(harmony,"slot",t,pre:nameof(CoopDiagTimers.SlotPre),fin:nameof(CoopDiagTimers.SlotFinal));
                    Patch(harmony,"create",t,pre:nameof(CoopDiagTimers.TimePre),fin:nameof(CoopDiagTimers.CreateFinal));
                    Patch(harmony,"check",t,pre:nameof(CoopDiagTimers.TimePre),fin:nameof(CoopDiagTimers.CheckFinal));
                    Patch(harmony,"init",t,pre:nameof(CoopDiagTimers.TimePre),fin:nameof(CoopDiagTimers.InitFinal));
                    Patch(harmony,"initGfx",t,pre:nameof(CoopDiagTimers.TimePre),fin:nameof(CoopDiagTimers.GfxFinal));
                    Patch(harmony,"deps",t,pre:nameof(CoopDiagTimers.TimePre),fin:nameof(CoopDiagTimers.DepsFinal));
                    Patch(harmony,"weapons",t,pre:nameof(CoopDiagTimers.TimePre),fin:nameof(CoopDiagTimers.WeaponsFinal));
                    Patch(harmony,"skin",t,pre:nameof(CoopDiagTimers.TimePre),fin:nameof(CoopDiagTimers.SkinFinal));
                    Patch(harmony,"head",t,pre:nameof(CoopDiagTimers.TimePre),fin:nameof(CoopDiagTimers.HeadFinal));
                    Patch(harmony,"label",t,pre:nameof(CoopDiagTimers.TimePre),fin:nameof(CoopDiagTimers.LabelFinal));
                    Patch(harmony,"dive",t,pre:nameof(CoopDiagTimers.TimePre),fin:nameof(CoopDiagTimers.DiveFinal));
                    Patch(harmony,"notify",t,pre:nameof(CoopDiagTimers.TimePre),fin:nameof(CoopDiagTimers.NotifyFinal));
                    CoopDiagTimers.Enabled=true;
                }
                if(WatchdogOn)CoopDiagWatchdog.Start();
                StartTicks=Stopwatch.GetTimestamp();
                Published=true;
            }
            catch(Exception error)
            {
                Published=false;InitializationError=error.Message;CoopDiagTimers.Enabled=false;CoopDiagWatchdog.Stop();
                if(harmony!=null)try{harmony.UnpatchSelf();}catch(Exception rollback){InitializationError+="; rollback: "+rollback.Message;}
                throw;
            }
        }
    }
    static void Patch(Harmony harmony,string key,Type owner=null,string pre=null,string post=null,string fin=null,int prePriority=Priority.Normal,int postPriority=Priority.Normal)
    {
        owner??=typeof(CoopDiagHooks);
        var original=CoopDiagPins.Methods[key];
        HarmonyMethod M(string name,int priority)
        {
            if(name==null)return null;
            var m=owner.GetMethod(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static)??throw new InvalidOperationException("Missing patch body "+name);
            return new HarmonyMethod(m){priority=priority};
        }
        harmony.Patch(original,prefix:M(pre,prePriority),postfix:M(post,postPriority),finalizer:M(fin,Priority.Normal));
        var info=Harmony.GetPatchInfo(original);
        int Mine(IEnumerable<HarmonyLib.Patch> list)=>list?.Count(p=>p.owner==R.HarmonyId)??0;
        if(info==null||Mine(info.Prefixes)!=(pre==null?0:1)||Mine(info.Postfixes)!=(post==null?0:1)||Mine(info.Finalizers)!=(fin==null?0:1))
            throw new InvalidOperationException("CoopDiag patch not installed exactly once: "+key);
        PatchedKeys.Add(key);
    }

    // ---------------- per-frame state (game thread) ----------------
    static int gameThread;static long StartTicks,frameNo,lastBeatTicks,nextBeatTicks,winFrames,winReady,lastReadyFrame=-1,rxAtLastBeat;
    static double winMaxGap;static bool contentLogged,configLogged;static int lastMask=int.MinValue;static bool lastDirty;
    public static long Heartbeats,Frames,ReadyFramesTotal,StallEnds,BossLines,BossEpisodes;public static double MaxGapMsEver,LastStallGapMs=-1;
    public static CoopDiagBeat LastBeat;
    static long lastRxTicks,rxTotal;
    static bool bossEpisode;static long bossEpisodeStart,bossLast;static int bossEpisodeLines;

    static void AfterFrame(ModEntry __instance)
    {
        if(!Published||FrameGate.Tripped)return;
        try
        {
            int thread=Environment.CurrentManagedThreadId;
            if(gameThread==0){gameThread=thread;CoopDiagTimers.SetGameThread(thread);lastBeatTicks=Stopwatch.GetTimestamp();nextBeatTicks=lastBeatTicks+(long)(Period*(double)Stopwatch.Frequency);}
            else if(thread!=gameThread)return;
            long now=Stopwatch.GetTimestamp();
            CoopDiagWatchdog.Tracker.Frame(now,out long gap,out bool ended);
            double gapMs=gap*TickMs;
            frameNo++;Frames++;winFrames++;Interlocked.Exchange(ref CoopDiagWatchdog.LastFrameNumber,frameNo);
            if(gapMs>winMaxGap)winMaxGap=gapMs;if(gapMs>MaxGapMsEver)MaxGapMsEver=gapMs;
            if(ended)
            {
                StallEnds++;LastStallGapMs=gapMs;
                CoopDiagLog.Warn(R.Prefix+"STALL ended: frame gap "+R.Sec(gapMs/1000.0)+" s role="+CoopDiagSnapshot.Role+" lvl="+R.Clean(CoopDiagSnapshot.Level,48)+" cine="+R.Clean(CoopDiagSnapshot.CineType,64));
            }
            if(frameNo%6==1)
            {
                CoopDiagSnapshot.Sample();
                CoopDiagWatchdog.LastLevel=CoopDiagSnapshot.Level;CoopDiagWatchdog.LastCine=CoopDiagSnapshot.CineType??"-";CoopDiagWatchdog.LastRole=CoopDiagSnapshot.Role;
                // A session or run just started: do not wait out the idle (one-minute) period.
                long soon=now+(long)(Period*(double)Stopwatch.Frequency);
                if(CoopDiagSnapshot.SessionActive&&nextBeatTicks>soon)nextBeatTicks=soon;
                if(BossOn&&!BossGate.Tripped)BossTick(now);
            }
            if(now>=nextBeatTicks)Beat(now);
            FrameGate.Ok();
        }
        catch(Exception error)
        {
            if(FrameGate.Fault())try{CoopDiagLog.Warn(R.Prefix+"heartbeat switched off after "+FrameGate.Total.ToString(Inv)+" faults: "+error.GetBaseException().Message);}catch{}
        }
    }
    static void Beat(long now)
    {
        double window=(now-lastBeatTicks)/(double)Stopwatch.Frequency;
        long rx=Interlocked.Read(ref rxTotal),rxLast=Volatile.Read(ref lastRxTicks);
        var b=new CoopDiagBeat{Seq=++Heartbeats,UpSeconds=(now-StartTicks)/(double)Stopwatch.Frequency,WindowSeconds=window,Frames=winFrames,ReadyFrames=winReady,
            MaxGapMs=winMaxGap,RxCount=rx-rxAtLastBeat,RxAgeSeconds=rxLast==0?-1:(now-rxLast)/(double)Stopwatch.Frequency,
            Faults=FrameGate.Total+ReadyGate.Total+BossGate.Total+CoopDiagTimers.Gate.Total+CoopDiagWatchdog.Gate.Total};
        CoopDiagSnapshot.Fill(b);
        LastBeat=b;
        CoopDiagLog.Info(R.FormatHeartbeat(b));
        if(TimingOn){var flush=CoopDiagTimers.TakeFlushWindow(window);if(flush!=null)CoopDiagLog.Info(flush);}
        if(!contentLogged||b.DlcMask!=lastMask||b.DlcDirty!=lastDirty)
        {
            contentLogged=true;lastMask=b.DlcMask;lastDirty=b.DlcDirty;
            CoopDiagLog.Info(CoopDiagSnapshot.ContentLine());
        }
        if(!configLogged&&CoopDiagSnapshot.SessionActive){configLogged=true;CoopDiagLog.Info(CoopDiagSnapshot.ConfigLine(Period,TimingOn,WatchdogOn,BossOn));}
        rxAtLastBeat=rx;winFrames=0;winReady=0;winMaxGap=0;lastBeatTicks=now;
        nextBeatTicks=now+(long)(R.PeriodSeconds(CoopDiagSnapshot.SessionActive,Period)*Stopwatch.Frequency);
    }
    // Issue 9 / 19: while a player is downed on a boss level, the host writes the boss's life, aTarget and nemesis owner
    // and aiLocked once per second (bounded per episode), plus one line when the episode starts and ends.
    static void BossTick(long now)
    {
        try
        {
            bool active=CoopDiagSnapshot.IsHost&&CoopDiagSnapshot.AnyDowned&&CoopDiagSnapshot.OnBossLevel;
            if(active&&!bossEpisode)
            {
                bossEpisode=true;bossEpisodeStart=now;bossLast=0;bossEpisodeLines=0;BossEpisodes++;
                CoopDiagLog.Info(R.Prefix+"BOSS episode start lvl="+R.Clean(CoopDiagSnapshot.Level,48)+" down="+CoopDiagSnapshot.LocalDowned.ToString(Inv)+" rdown="+CoopDiagSnapshot.RemoteDowned.ToString(Inv));
            }
            else if(!active&&bossEpisode)
            {
                bossEpisode=false;
                CoopDiagLog.Info(R.Prefix+"BOSS episode end after "+R.Sec((now-bossEpisodeStart)/(double)Stopwatch.Frequency)+" s lines="+bossEpisodeLines.ToString(Inv)+" boss="+CoopDiagSnapshot.BossSummary(true));
            }
            if(!active)return;
            double since=bossLast==0?double.MaxValue:(now-bossLast)/(double)Stopwatch.Frequency;
            if(!R.ShouldTraceBoss(CoopDiagSnapshot.IsHost,CoopDiagSnapshot.AnyDowned,CoopDiagSnapshot.OnBossLevel,since,bossEpisodeLines))return;
            bossLast=now;bossEpisodeLines++;BossLines++;
            CoopDiagLog.Info(R.Prefix+"BOSS t="+R.Sec((now-bossEpisodeStart)/(double)Stopwatch.Frequency)+" down="+CoopDiagSnapshot.LocalDowned.ToString(Inv)+" rdown="+CoopDiagSnapshot.RemoteDowned.ToString(Inv)
                +" cine="+R.Clean(CoopDiagSnapshot.CineType,64)+" boss="+CoopDiagSnapshot.BossSummary(true));
            BossGate.Ok();
        }
        catch(Exception error)
        {
            if(BossGate.Fault())try{CoopDiagLog.Warn(R.Prefix+"boss trace switched off after "+BossGate.Total.ToString(Inv)+" faults: "+error.GetBaseException().Message);}catch{}
        }
    }
    // Network thread: a timestamp and a count, nothing else (the line itself is not read).
    static void BeforeRx(){Volatile.Write(ref lastRxTicks,Stopwatch.GetTimestamp());Interlocked.Increment(ref rxTotal);}
    // Frames in which the base mod's hero tick readiness answered true (deduplicated per frame; game thread only).
    static void AfterReady(bool __result)
    {
        if(!__result||!Published||ReadyGate.Tripped)return;
        try
        {
            if(Environment.CurrentManagedThreadId!=gameThread||lastReadyFrame==frameNo)return;
            lastReadyFrame=frameNo;winReady++;ReadyFramesTotal++;
        }
        catch{if(ReadyGate.Fault())try{CoopDiagLog.Warn(R.Prefix+"ready counter switched off");}catch{}}
    }

    // For the test-only probe (CoopDiagProbe reads it by reflection). Values only, no game objects.
    public static Dictionary<string,object> Counters()
    {
        var b=LastBeat;long now=Stopwatch.GetTimestamp();long rxLast=Volatile.Read(ref lastRxTicks);
        return new Dictionary<string,object>(StringComparer.Ordinal)
        {
            ["installed"]=Published,["disabled"]=Disabled,["error"]=InitializationError,["harmonyId"]=R.HarmonyId,["version"]=R.Version,
            ["patched"]=PatchedKeys.ToArray(),["period"]=Period,["timing"]=TimingOn,["watchdog"]=WatchdogOn,["boss"]=BossOn,
            ["proxySymbolic"]=ProxySymbolic,["proxyRaw"]=ProxyRaw,
            ["heartbeats"]=Heartbeats,["frames"]=Frames,["readyFrames"]=ReadyFramesTotal,["maxGapMs"]=MaxGapMsEver,
            ["rxTotal"]=Interlocked.Read(ref rxTotal),["rxAgeSeconds"]=rxLast==0?-1.0:(now-rxLast)/(double)Stopwatch.Frequency,
            ["stallReports"]=Interlocked.Read(ref CoopDiagWatchdog.Reports),["stallEnds"]=StallEnds,["lastStallGapMs"]=LastStallGapMs,["watchdogPolls"]=Interlocked.Read(ref CoopDiagWatchdog.Polls),
            ["faults"]=new Dictionary<string,int>{["heartbeat"]=FrameGate.Total,["ready"]=ReadyGate.Total,["boss"]=BossGate.Total,["timing"]=CoopDiagTimers.Gate.Total,["watchdog"]=CoopDiagWatchdog.Gate.Total},
            ["tripped"]=new[]{FrameGate,ReadyGate,BossGate,CoopDiagTimers.Gate,CoopDiagWatchdog.Gate}.Where(g=>g.Tripped).Select(g=>g.Name).ToArray(),
            ["avatars"]=Interlocked.Read(ref CoopDiagTimers.Avatars),["lastAvatarTotalMs"]=CoopDiagTimers.LastAvatarTotalMs,["lastAvatarCreateMs"]=CoopDiagTimers.LastAvatarCreateMs,["maxAvatarTotalMs"]=CoopDiagTimers.MaxAvatarTotalMs,
            ["avatarParts"]=CoopDiagTimers.AvatarParts,["lastAvatarCounts"]=CoopDiagTimers.LastAvatarCounts,
            ["flushFrames"]=CoopDiagTimers.FlushFrames,["flushWindows"]=CoopDiagTimers.FlushWindows,["flushSpikes"]=CoopDiagTimers.FlushSpikes,
            ["bossLines"]=BossLines,["bossEpisodes"]=BossEpisodes,
            ["lastRole"]=b?.Role,["lastLevel"]=b?.Level,["lastSub"]=b?.SubLevel??-1,["lastCine"]=b?.Cine,["lastDlcMask"]=b?.DlcMask??-1,["lastDlcDirty"]=b?.DlcDirty??false,
            ["lastPad"]=b?.Pad,["lastReady"]=b?.ReadyFrames??-1,["lastFrames"]=b?.Frames??-1,["lastRxCount"]=b?.RxCount??-1,
        };
    }
}

// Serilog wrapper: pre-formatted text (braces escaped so Serilog renders it literally); never throws.
public static class CoopDiagLog
{
    static string Esc(string s)=>s==null?"":s.Replace("{","{{").Replace("}","}}");
    public static void Info(string line){try{Serilog.Log.Information(Esc(line));}catch{}}
    public static void Warn(string line){try{Serilog.Log.Warning(Esc(line));}catch{}}
    public static void Error(string line){try{Serilog.Log.Error(Esc(line));}catch{}}
}
