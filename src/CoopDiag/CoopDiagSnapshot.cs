using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using DeadCellsMultiplayerMod;
using R=CoopDiagRules;

// Read-only views of the game and the base mod for the heartbeat, the boss trace and the content line. Game thread only.
// Every native read is guarded (null / zero pointer / destroyed) and every caller runs it inside try/catch.
public static class CoopDiagSnapshot
{
    static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
    static Func<bool> isDowned,inRun,showPerf,bossTraceSetting;static Func<dc.en.Mob,bool> isBossMob;
    static FieldInfo doorToken,doorLevel,remoteDowned,bossLevels;
    static Func<int> levelToken;static Func<bool> levelReady;static Func<List<dc.en.Mob>> tracked,scratch;static Func<object> sync;static Func<double> registrySendFrame;
    static HashSet<string> bossLevelSet;

    // A compiled static-field read (no boxing on the per-frame paths); reflection as the fallback.
    static Func<T> Getter<T>(FieldInfo f)
    {
        try{return Expression.Lambda<Func<T>>(Expression.Field(null,f)).Compile();}
        catch{return ()=>(T)f.GetValue(null);}
    }
    // Compiling the getters does not run the base mod's static constructors; the first call does (after the base mod
    // has initialised: the first heartbeat or host frame).
    public static void Bind()
    {
        isDowned=CoopDiagPins.Call<Func<bool>>("isDowned");inRun=CoopDiagPins.Call<Func<bool>>("inRun");
        showPerf=CoopDiagPins.Call<Func<bool>>("showPerf");bossTraceSetting=CoopDiagPins.Call<Func<bool>>("bossTraceSetting");
        isBossMob=CoopDiagPins.Call<Func<dc.en.Mob,bool>>("isBossMob");
        var f=CoopDiagPins.Fields;
        doorToken=f["doorToken"];doorLevel=f["doorLevel"];remoteDowned=f["remoteDowned"];bossLevels=f["bossLevels"];
        levelToken=Getter<int>(f["levelToken"]);levelReady=Getter<bool>(f["levelReady"]);
        tracked=Getter<List<dc.en.Mob>>(f["tracked"]);scratch=Getter<List<dc.en.Mob>>(f["scratch"]);sync=Getter<object>(f["sync"]);
        registrySendFrame=Getter<double>(f["registrySendFrame"]);
        CoopDiagTimers.TrackedCount=()=>tracked()?.Count??-1;
        CoopDiagTimers.ScratchCount=()=>scratch()?.Count??-1;
        CoopDiagTimers.RegistrySendFrame=()=>registrySendFrame();
        CoopDiagTimers.LevelName=()=>Level;
    }

    // ---- sampled state (about 10 times a second, from the frame postfix) ----
    public static string Role="-",Level="-",CineType,LastCineType="-";
    public static int SubLevel=-1,Epoch,CineUid,LocalDowned=-1,RemoteDowned=-1;
    public static bool IsHost,SessionActive,OnBossLevel,AnyDowned;
    static IntPtr levelPtr,cinePtr;static long cineSince;
    public static double CineAgeSeconds=>CineType==null?-1:(Stopwatch.GetTimestamp()-cineSince)/(double)Stopwatch.Frequency;

    public static void Sample()
    {
        var net=ModEntry._net;
        bool alive=net!=null&&net.IsAlive;
        IsHost=alive&&net.IsHost;
        Role=!alive?"solo":IsHost?"host":"client";
        bool run=false;try{run=inRun();}catch{}
        SessionActive=alive||run;
        var game=dc.pr.Game.Class.ME;
        if(game==null||game.HashlinkPointer==IntPtr.Zero){Forget();return;}
        var hero=game.hero;
        var level=hero!=null&&hero.HashlinkPointer!=IntPtr.Zero&&!hero.destroyed?hero._level:null;
        if(level!=null&&level.HashlinkPointer!=IntPtr.Zero&&!level.destroyed&&level.map!=null)
        {
            if(level.HashlinkPointer!=levelPtr){levelPtr=level.HashlinkPointer;Epoch++;Level=level.map.id?.ToString()??"-";SubLevel=level.isSubLevel?1:0;
                bossLevelSet??=bossLevels.GetValue(null) as HashSet<string>;OnBossLevel=bossLevelSet!=null&&bossLevelSet.Contains(Level);}
        }
        else{levelPtr=IntPtr.Zero;Level="-";SubLevel=-1;OnBossLevel=false;}
        var cine=game.curCine;
        if(cine==null||cine.HashlinkPointer==IntPtr.Zero){CineType=null;CineUid=0;cinePtr=IntPtr.Zero;}
        else
        {
            int uid=cine.uniqId;
            if(cine.HashlinkPointer!=cinePtr||uid!=CineUid){cinePtr=cine.HashlinkPointer;CineUid=uid;CineType=cine.GetType().FullName??"?";cineSince=Stopwatch.GetTimestamp();LastCineType=CineType;}
        }
        var mod=ModEntry.Instance;
        LocalDowned=mod==null?-1:isDowned()?1:0;
        RemoteDowned=mod==null?-1:(remoteDowned.GetValue(mod) as ICollection)?.Count??-1;
        AnyDowned=LocalDowned==1||RemoteDowned>0;
    }
    static void Forget(){levelPtr=IntPtr.Zero;cinePtr=IntPtr.Zero;Level="-";SubLevel=-1;CineType=null;CineUid=0;OnBossLevel=false;LocalDowned=-1;RemoteDowned=-1;AnyDowned=false;}

    public static void Fill(CoopDiagBeat b)
    {
        b.Role=Role;b.Level=Level;b.SubLevel=SubLevel;b.LevelEpoch=Epoch;b.Cine=CineType;b.CineUid=CineUid;b.CineAgeSeconds=CineAgeSeconds;
        b.LocalDowned=LocalDowned;b.RemoteDowned=RemoteDowned;
        // Each optional part fails alone ("?"), so one unreadable value never costs the whole heartbeat.
        try{b.LevelToken=levelReady()?levelToken().ToString(Inv):"-";}catch{b.LevelToken="?";}
        try{var mod=ModEntry.Instance;if(mod!=null){b.RoomToken=(int)doorToken.GetValue(mod);b.RoomLevel=doorLevel.GetValue(mod) as string;}}catch{b.RoomLevel="?";}
        try{var dlc=Dlc(out bool dirty);b.DlcMask=dlc;b.DlcDirty=dirty;}catch{b.DlcMask=-1;}
        try{b.Pad=Pad();}catch{b.Pad="?";}
        try{b.Boss=OnBossLevel?BossSummary(false):null;}catch{b.Boss="?";}
    }
    public static int Dlc(out bool dirty)
    {
        var d=dc.tool.DLC.Class;
        if(d==null){dirty=false;return -1;}
        dirty=d.installMaskCacheDirty;return d.installMask;
    }
    // Names of the active gamepads (game.controller.parent.activeGamepads[i].device.get_name()), "none" without one.
    public static string Pad()
    {
        var game=dc.pr.Game.Class.ME;
        var parent=game?.controller?.parent;
        var pads=parent?.activeGamepads;
        if(pads==null)return "-";
        int n=pads.length;if(n<=0)return "none";
        var names=new List<string>();
        for(int i=0;i<n&&i<4;i++)
        {
            var pad=pads.getDyn(i) as dc.libs.heaps.GamePad;
            var device=pad?.device;
            names.Add(device==null?"?":device.get_name()?.ToString()??"?");
        }
        return string.Join("|",names);
    }
    // Boss state on a boss level: "Behemoth:1234/4000:aim.client:nem.host:lock.1" (up to two bosses; no '=' inside, so
    // the heartbeat stays one key=value token per field).
    public static string BossSummary(bool detail)
    {
        var bosses=Bosses();
        if(bosses.Count==0)return "none";
        var me=ModEntry.me;IntPtr hero=me!=null?me.HashlinkPointer:IntPtr.Zero;
        var s=new StringBuilder();
        foreach(var b in bosses)
        {
            if(s.Length>0)s.Append(',');
            s.Append(R.Clean(R.ShortType(b.GetType().FullName),32)).Append(':').Append(b.life.ToString(Inv)).Append('/').Append(b.maxLife.ToString(Inv))
             .Append(":aim.").Append(Owner(b.aTarget,hero)).Append(":nem.").Append(Owner(b.nemesisTarget,hero));
            if(detail)s.Append(":lock.").Append(R.Bool(b.aiLocked()));
        }
        return s.ToString();
    }
    static string Owner(dc.Entity e,IntPtr hero)
    {
        bool none=e==null||e.HashlinkPointer==IntPtr.Zero;
        string type=none?null:e.GetType().FullName??"";
        return R.TargetOwner(none,!none&&hero!=IntPtr.Zero&&e.HashlinkPointer==hero,!none&&(type.EndsWith("GhostKing",StringComparison.Ordinal)||type.EndsWith("KingSkin",StringComparison.Ordinal)),type);
    }
    static List<dc.en.Mob> Bosses()
    {
        var list=tracked();var gate=sync();var found=new List<dc.en.Mob>(2);
        if(list==null||gate==null)return found;
        dc.en.Mob[] copy;
        lock(gate){copy=list.ToArray();}
        foreach(var m in copy)
        {
            if(m==null||m.HashlinkPointer==IntPtr.Zero||m.destroyed)continue;
            if(!isBossMob(m))continue;
            found.Add(m);if(found.Count==2)break;
        }
        return found;
    }
    public static string ContentLine()
    {
        var mask=Dlc(out bool dirty);
        string hash="-";int version=-1;bool steam=false;
        try{hash=dc.libs.GitVersion.Class?.SHORT_HASH?.ToString()??"-";}catch{}
        try{version=dc.Main.Class?.GAME_VERSION??-1;}catch{}
        try{steam=dc.steam.Api.Class?.active??false;}catch{}
        // installMask is the third field of the title-screen build text "<date> - <hash> - <mask> - Steam".
        return R.Prefix+"CONTENT dlc="+(mask<0?"-":R.DescribeDlcMask(mask))+" dirty="+R.Bool(dirty)+" steamActive="+R.Bool(steam)+" hash="+R.Clean(hash,16)+" v="+version.ToString(Inv)+" role="+Role;
    }
    public static string ConfigLine(int period,bool timing,bool watchdog,bool boss)
    {
        string perf="-",trace="-";
        try{perf=R.Bool(showPerf());}catch{}
        try{trace=R.Bool(bossTraceSetting());}catch{}
        return R.Prefix+"CONFIG showPerfLogs="+perf+" bossSyncTrace="+trace+" envBossTrace="+R.Clean(Environment.GetEnvironmentVariable("DCCM_BOSS_SYNC_TRACE"),8)
            +" heartbeat="+period.ToString(Inv)+" timing="+R.Bool(timing)+" watchdog="+R.Bool(watchdog)+" boss="+R.Bool(boss);
    }
}
