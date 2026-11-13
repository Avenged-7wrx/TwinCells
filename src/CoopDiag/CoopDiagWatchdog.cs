using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using R=CoopDiagRules;

// Timer-thread watchdog (PLAN-ALPHA2.md issue 19): one WRN line when the game thread has not finished a frame for
// StallSeconds, read from CoopDiagRules.CoopDiagStallTracker. It never touches a game object: the line carries the
// strings the game thread last published (level, cinematic type, role, frame number).
public static class CoopDiagWatchdog
{
    public static readonly CoopDiagStallTracker Tracker=new(R.StallSeconds,Stopwatch.Frequency);
    public static readonly CoopDiagFaultGate Gate=new("watchdog");
    static Timer timer;
    public static long Polls,Reports;
    // Published by the game thread (reference writes are atomic; volatile for visibility).
    public static volatile string LastLevel="-",LastCine="-",LastRole="-";
    public static long LastFrameNumber;

    public static void Start()
    {
        if(timer!=null)return;
        timer=new Timer(Poll,null,(int)R.WatchdogPollMs,(int)R.WatchdogPollMs);
        AppDomain.CurrentDomain.ProcessExit+=(_,_)=>Stop();
    }
    public static void Stop(){var t=Interlocked.Exchange(ref timer,null);try{t?.Dispose();}catch{}}
    static void Poll(object _)
    {
        if(Gate.Tripped)return;
        try
        {
            Interlocked.Increment(ref Polls);
            if(Tracker.Poll(Stopwatch.GetTimestamp(),out long silent))
            {
                Interlocked.Increment(ref Reports);
                CoopDiagLog.Warn(R.Prefix+"STALL frame loop silent for "+R.Sec(silent/(double)Stopwatch.Frequency)+" s after frame #"
                    +Interlocked.Read(ref LastFrameNumber).ToString(CultureInfo.InvariantCulture)+" role="+LastRole+" lvl="+R.Clean(LastLevel,48)+" cine="+R.Clean(LastCine,64));
            }
            Gate.Ok();
        }
        catch(Exception e)
        {
            if(Gate.Fault()){Stop();try{CoopDiagLog.Warn(R.Prefix+"watchdog switched off after "+Gate.Total+" faults: "+e.GetBaseException().Message);}catch{}}
        }
    }
}
