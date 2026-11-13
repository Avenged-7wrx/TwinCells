using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

// Pure rules of CoopDiag (Alpha2 unit U13; PLAN-ALPHA2.md issues 19, 15, 16 and 13). No game, base-mod or DCCM types:
// the same file is compiled into the managed checks (checks\CoopDiagChecks.cs).
//
// CoopDiag only observes. It writes no game object, no base-mod field, no setting (MultiplayerSettingsStorage is only
// read through its getters) and nothing on the wire. Every line it writes starts with "[CoopDiag] ".
public static class CoopDiagRules
{
    public const string Prefix="[CoopDiag] ";
    public const string HarmonyId="CoopDiag.U13";
    public const string Version="0.2.0-alpha";

    // Kill switches (UNIT-GUIDE.md section 1: COOP_<PART>_DISABLE; the pack row switch COOP_PACK_DIAG_DISABLE is added by
    // integration). Sub-switches turn off one group only.
    public const string DisableSwitch="COOP_DIAG_DISABLE";
    public const string TimingSwitch="COOP_DIAG_TIMING_DISABLE";      // issue 15/16 stopwatches
    public const string WatchdogSwitch="COOP_DIAG_WATCHDOG_DISABLE";  // timer-thread frame-loop watchdog
    public const string BossSwitch="COOP_DIAG_BOSS_DISABLE";          // boss trace while a player is downed
    public const string PeriodSetting="COOP_DIAG_HEARTBEAT_SECONDS";  // heartbeat period (default 5, 1..60)

    // Heartbeat: every 5 s while a session or a run is active, else once a minute (title screen, menus).
    public const int DefaultHeartbeatSeconds=5,MinHeartbeatSeconds=1,MaxHeartbeatSeconds=60,IdleHeartbeatSeconds=60;
    // Watchdog: one line when the game thread has not finished a frame for this long, one line when it is back.
    public const double StallSeconds=3.0,WatchdogPollMs=500;
    // HostFlush spike threshold: the base mod's own (OnFrameUpdateCore IL_00D7 ldc.r8 6, "MobsSynchronization.HostFlush").
    public const double FlushSpikeMs=6.0;
    // Spike lines: at most one per 2 s per key (RuntimeHitchWatch's per-key cooldown), with the suppressed count.
    public const double SpikeCooldownSeconds=2.0;
    // Avatar creation without CreateGhostKing is logged only when it is this slow.
    public const double AvatarSlowMs=20.0;
    // Boss trace (host): one line per second while a player is downed on a boss level, at most this many per episode.
    public const double BossTraceSeconds=1.0;
    public const int BossTraceMaxPerEpisode=120;
    // Fault gate per group: off after 3 faults in a row or 20 in total (the lines say "switched off").
    public const int MaxConsecutiveFaults=3,MaxTotalFaults=20;

    // tool.DLCId in index order (bytecode type t@1261 "tool.DLCId"; GameProxy dc.tool.DLCId+Indexes). The title screen
    // build text "<date> - <hash> - <n> - Steam" (pr.TitleScreen.setMiscTexts f@6469, TitleScreen.hx:302-307) prints
    // tool.$DLC.installMask: bit i is set when steam.Api.isDlcInstalled(DLC.getSteamId(DLCId[i])) (f@6430 -> native
    // steam.is_dlc_installed). getSteamId f@6322 (tool/DLC.hx:29-35) maps the indices to these Steam app ids.
    public static readonly string[] DlcNames={"RiseOfTheGiant","BadSeed","FatalFalls","TheQueenAndTheSea","Purple"};
    public static readonly int[] DlcSteamAppIds={1046440,1204130,1451460,1580050,2101430};

    static readonly CultureInfo Inv=CultureInfo.InvariantCulture;

    public static int ParseHeartbeatSeconds(string value)
    {
        if(string.IsNullOrWhiteSpace(value)||!int.TryParse(value.Trim(),NumberStyles.Integer,Inv,out int s))return DefaultHeartbeatSeconds;
        return s<MinHeartbeatSeconds?MinHeartbeatSeconds:s>MaxHeartbeatSeconds?MaxHeartbeatSeconds:s;
    }
    public static bool IsOn(string value)=>value=="1";
    // Session or run active: the configured period; otherwise once a minute (never more often than configured).
    public static double PeriodSeconds(bool sessionActive,int configured)=>sessionActive?configured:Math.Max(configured,IdleHeartbeatSeconds);

    // "3:RiseOfTheGiant+BadSeed", "0:none", unknown high bits as "+bit5".
    public static string DescribeDlcMask(int mask)
    {
        if(mask==0)return "0:none";
        var parts=new List<string>();
        for(int i=0;i<31;i++)
        {
            if((mask&(1<<i))==0)continue;
            parts.Add(i<DlcNames.Length?DlcNames[i]:"bit"+i.ToString(Inv));
        }
        if(mask<0)parts.Add("bit31");
        return mask.ToString(Inv)+":"+string.Join("+",parts);
    }
    // Bits set in one mask and not in the other, named ("" when equal).
    public static string DlcDifference(int a,int b)=>a==b?"":DescribeDlcMask(a^b);

    // Log-safe token: letters, digits and . _ : # / + - only; anything else becomes '_'; capped; null/empty -> "-".
    public static string Clean(string value,int max=64)
    {
        if(string.IsNullOrEmpty(value))return "-";
        if(max<1)max=1;
        var sb=new StringBuilder(Math.Min(value.Length,max));
        foreach(char c in value)
        {
            if(sb.Length>=max)break;
            bool ok=c>='a'&&c<='z'||c>='A'&&c<='Z'||c>='0'&&c<='9'||c=='.'||c=='_'||c==':'||c=='#'||c=='/'||c=='+'||c=='-';
            sb.Append(ok?c:'_');
        }
        return sb.ToString();
    }
    public static string Ms(double ms)=>double.IsNaN(ms)||double.IsInfinity(ms)?"-":ms.ToString("0.00",Inv);
    public static string Sec(double s)=>double.IsNaN(s)||double.IsInfinity(s)||s<0?"-":s.ToString("0.0",Inv);
    public static string Bool(bool b)=>b?"1":"0";

    // Who a boss aims at, from the host's view: the host hero, the client's avatar (GhostKing), nobody, or another entity.
    public static string TargetOwner(bool isNull,bool isLocalHero,bool isRemoteAvatar,string otherType)
        =>isNull?"none":isLocalHero?"host":isRemoteAvatar?"client":"other:"+Clean(ShortType(otherType),40);
    public static string ShortType(string fullName)
    {
        if(string.IsNullOrEmpty(fullName))return "-";
        int i=fullName.LastIndexOf('.');
        return i>=0&&i<fullName.Length-1?fullName.Substring(i+1):fullName;
    }
    // Host only, a player downed, on a boss level, at most once per BossTraceSeconds and BossTraceMaxPerEpisode lines.
    public static bool ShouldTraceBoss(bool isHost,bool anyDowned,bool onBossLevel,double secondsSinceLast,int linesThisEpisode)
        =>isHost&&anyDowned&&onBossLevel&&secondsSinceLast>=BossTraceSeconds&&linesThisEpisode<BossTraceMaxPerEpisode;

    public static string FormatHeartbeat(CoopDiagBeat b)
    {
        var s=new StringBuilder(320);
        s.Append(Prefix).Append("HB #").Append(b.Seq.ToString(Inv));
        Kv(s,"role",b.Role);Kv(s,"up",Sec(b.UpSeconds));Kv(s,"win",Sec(b.WindowSeconds));
        Kv(s,"fr",b.Frames.ToString(Inv));Kv(s,"ready",b.ReadyFrames.ToString(Inv));Kv(s,"gap",Ms(b.MaxGapMs));
        Kv(s,"lvl",Clean(b.Level,48));Kv(s,"sub",b.SubLevel<0?"-":b.SubLevel.ToString(Inv));
        Kv(s,"tok",b.LevelToken);Kv(s,"room",Clean(b.RoomLevel,48)+":"+b.RoomToken.ToString(Inv));Kv(s,"ep",b.LevelEpoch.ToString(Inv));
        Kv(s,"cine",Clean(b.Cine,64));Kv(s,"cuid",b.CineUid==0?"-":b.CineUid.ToString(Inv));Kv(s,"cage",b.Cine==null?"-":Sec(b.CineAgeSeconds));
        Kv(s,"down",b.LocalDowned<0?"-":b.LocalDowned.ToString(Inv));Kv(s,"rdown",b.RemoteDowned<0?"-":b.RemoteDowned.ToString(Inv));
        Kv(s,"rx",b.RxAgeSeconds<0?"-":b.RxAgeSeconds.ToString("0.00",Inv));Kv(s,"rxn",b.RxCount.ToString(Inv));
        Kv(s,"dlc",b.DlcMask<0?"-":b.DlcMask.ToString(Inv)+(b.DlcDirty?"d":""));
        Kv(s,"pad",Clean(b.Pad,48));Kv(s,"boss",string.IsNullOrEmpty(b.Boss)?"-":b.Boss);Kv(s,"f",b.Faults.ToString(Inv));
        return s.ToString();
    }
    static void Kv(StringBuilder s,string k,string v)=>s.Append(' ').Append(k).Append('=').Append(string.IsNullOrEmpty(v)?"-":v);

    // HostFlush steps in the order OnFrameUpdateCore calls them (IL_00AF..IL_00C7), then the activation pass (IL_00A2).
    public static readonly string[] StepNames={"dirty","bossScan","registry","priority","tombstone","activation"};
    public const int StepDirty=0,StepBossScan=1,StepRegistry=2,StepPriority=3,StepTombstone=4,StepActivation=5,StepCount=6;
    // The five steps the base mod's "MobsSynchronization.HostFlush" stopwatch covers.
    public static double FlushTotal(double[] stepMs){double t=0;for(int i=StepDirty;i<=StepTombstone;i++)t+=stepMs[i];return t;}
    public static bool IsFlushSpike(double totalMs)=>totalMs>=FlushSpikeMs;
}

// One heartbeat's values (filled on the game thread, formatted by CoopDiagRules.FormatHeartbeat).
public sealed class CoopDiagBeat
{
    public long Seq,Frames,ReadyFrames,RxCount,Faults;
    public double UpSeconds,WindowSeconds,MaxGapMs,CineAgeSeconds,RxAgeSeconds=-1;
    public string Role="-",Level,LevelToken="-",RoomLevel,Cine,Pad,Boss;
    public int SubLevel=-1,RoomToken,LevelEpoch,CineUid,LocalDowned=-1,RemoteDowned=-1,DlcMask=-1;
    public bool DlcDirty;
}

// count / avg / max of millisecond samples. Not thread-safe: one writer (the game thread).
public sealed class CoopDiagStat
{
    public long Count;public double Sum,Max;
    public void Add(double ms){Count++;Sum+=ms;if(ms>Max)Max=ms;}
    public void Reset(){Count=0;Sum=0;Max=0;}
    public double Avg=>Count==0?0:Sum/Count;
    public override string ToString()=>Count.ToString(CultureInfo.InvariantCulture)+"/"+CoopDiagRules.Ms(Avg)+"/"+CoopDiagRules.Ms(Max);
}

// At most one line per cooldown; counts what it held back.
public sealed class CoopDiagLimiter
{
    readonly double cooldown;double last=double.NegativeInfinity;int held;
    public CoopDiagLimiter(double cooldownSeconds){cooldown=cooldownSeconds;}
    public bool Allow(double nowSeconds,out int suppressed)
    {
        if(nowSeconds-last>=cooldown){last=nowSeconds;suppressed=held;held=0;return true;}
        held++;suppressed=0;return false;
    }
}

// Per-group fault gate: Fault() returns true exactly once, when the group trips (3 in a row or 20 in total).
public sealed class CoopDiagFaultGate
{
    int consecutive,total,tripped;
    public readonly string Name;
    public CoopDiagFaultGate(string name){Name=name;}
    public bool Tripped=>Volatile.Read(ref tripped)!=0;
    public int Total=>Volatile.Read(ref total);
    public void Ok(){Volatile.Write(ref consecutive,0);}
    public bool Fault()
    {
        int c=Interlocked.Increment(ref consecutive),t=Interlocked.Increment(ref total);
        if(c<CoopDiagRules.MaxConsecutiveFaults&&t<CoopDiagRules.MaxTotalFaults)return false;
        return Interlocked.Exchange(ref tripped,1)==0;
    }
}

// Frame-loop watchdog state. Frame() runs on the game thread once per frame; Poll() on the timer thread. The poll
// reports a stall once (when no frame finished for the threshold); the next frame reports its end and the gap.
public sealed class CoopDiagStallTracker
{
    long lastFrame;int reported;
    public readonly long ThresholdTicks;
    public CoopDiagStallTracker(double seconds,long frequency){ThresholdTicks=(long)(seconds*frequency);}
    public long LastFrame=>Volatile.Read(ref lastFrame);
    // gapTicks: time since the previous frame (0 for the first). ended: a stall had been reported and is now over.
    public void Frame(long now,out long gapTicks,out bool ended)
    {
        long prev=Interlocked.Exchange(ref lastFrame,now);
        gapTicks=prev==0?0:now-prev;
        // A claim that raced with this frame (gap below the threshold) is cleared without an "ended" line.
        ended=Interlocked.Exchange(ref reported,0)!=0&&gapTicks>=ThresholdTicks;
    }
    // True once per stall. silentTicks: time since the last frame.
    public bool Poll(long now,out long silentTicks)
    {
        long last=Volatile.Read(ref lastFrame);
        silentTicks=last==0?0:now-last;
        if(last==0||silentTicks<ThresholdTicks)return false;
        if(Interlocked.CompareExchange(ref reported,1,0)!=0)return false;
        // A frame finished between the read and the claim. If that frame has not seen the claim, undo it (no stall);
        // if it has (it already wrote its end line), the stall was real: report it, so an end line never lacks a start.
        if(Volatile.Read(ref lastFrame)!=last)return Interlocked.CompareExchange(ref reported,0,1)!=1;
        return true;
    }
}
