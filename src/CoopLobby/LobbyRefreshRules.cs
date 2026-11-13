using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

// Pure rules of the REFRESH part of CoopLobby (LobbyRefresh.cs; Alpha2 unit U12, PLAN-ALPHA2.md issue 17). No game
// types: the managed suite (experiments\alpha2-perf-lobby\checks) runs them, and alpha2-lobby\Test-Managed.ps1 compiles
// this file with the reviewed G9 checks (it only adds a type there).
public static class LobbyRefreshRules
{
    // The plan's three lobby rules: a cheap signature changed, Launch shown disabled while every player is ready, or
    // every 10 s. The Launch backstop is limited to once per 2 s (the original's own gate is 1 s).
    public const double FallbackSeconds=10;
    public const double BackstopSeconds=2;
    public const double SummarySeconds=60;

    public enum Verdict{Original,SkipRun,SkipLobby,PassFirst,PassChanged,PassFallback,PassLaunchStale}

    // One periodic call (CoopAdvancedHardening.OnFrameUpdate every 0.5 s -> RefreshRoomStatusMenuIfVisible, release IL
    // 163855, 81517-81545). inMenu: _inHostStatusMenu || _inClientWaitingMenu (without one the original returns at once,
    // IL 81518-81522: Original). In the run (InActualRun) or once the launch has fired (AutoStartTriggered) the periodic
    // rebuild never runs (the mod's own event refresh already skips there, IL 177445-177453). In the lobby it runs when
    // nothing was rebuilt since the part is on (PassFirst), when the signature differs from the one taken at the last real
    // rebuild (PassChanged), when that rebuild is FallbackSeconds old (PassFallback), or when the host's menu shows Launch
    // disabled although every player is ready, at most every BackstopSeconds (PassLaunchStale). Non-finite or negative
    // ages count as old (the rebuild runs).
    public static Verdict Decide(bool inMenu,bool inRun,bool autoStart,bool haveBaseline,bool sameSignature,double sinceBuilt,bool launchStale,double sinceBackstop)
    {
        if(!inMenu)return Verdict.Original;
        if(inRun||autoStart)return Verdict.SkipRun;
        if(!haveBaseline)return Verdict.PassFirst;
        if(!sameSignature)return Verdict.PassChanged;
        if(!Fresh(sinceBuilt,FallbackSeconds))return Verdict.PassFallback;
        if(launchStale&&!Fresh(sinceBackstop,BackstopSeconds))return Verdict.PassLaunchStale;
        return Verdict.SkipLobby;
    }
    // True when the original may run (the prefix returns true).
    public static bool Runs(Verdict v)=>v!=Verdict.SkipRun&&v!=Verdict.SkipLobby;
    static bool Fresh(double age,double limit)=>!double.IsNaN(age)&&!double.IsInfinity(age)&&age>=0&&age<limit;

    // The base mod's AllPlayersReady (IL 72510-72528): the players list is never empty for a session role, and every
    // listed player is ready. Here: the local player plus each remote snapshot.
    public static bool AllReady(bool localReady,IEnumerable<bool> remoteReady)=>localReady&&(remoteReady??Enumerable.Empty<bool>()).All(r=>r);
    // The host menu shows Launch disabled (built state 0; 1 = enabled, -1 = unknown) although every player is ready now.
    public static bool LaunchStale(bool hostMenu,int builtLaunch,bool allReadyNow)=>hostMenu&&builtLaunch==0&&allReadyNow;
    // The host menu's Start button ("Normal Mode", ShowHostStatusMenu IL 71704-71716; enabled iff AllPlayersReady): its
    // index among the pending buttons when exactly one carries startLabel, else -1 (unknown menu).
    public static int StartIndex(IList<string> labels,string startLabel)
    {
        if(labels==null||string.IsNullOrEmpty(startLabel))return -1;
        int found=-1;
        for(int i=0;i<labels.Count;i++)if(string.Equals(labels[i],startLabel,StringComparison.Ordinal)){if(found>=0)return -1;found=i;}
        return found;
    }
    // 1 / 0 for the Start button's Enabled, -1 when it cannot be identified.
    public static int LaunchState(IList<string> labels,IList<bool> enabled,string startLabel)
    {
        int i=StartIndex(labels,startLabel);
        if(i<0||enabled==null||enabled.Count!=labels.Count)return -1;
        return enabled[i]?1:0;
    }

    public sealed class Remote{public int Id;public string Name;public bool Ready;}
    public sealed class CoopState{public int Id;public bool HasContinueSave;public string CoopId;}
    // Everything the two lobby menus show that can change while one is up (ShowHostStatusMenu IL 71617-71840,
    // ShowClientWaitingMenu IL 71871-72022): which menu, the session snapshot (role, ready, launch intent, names; its
    // record print), the transport and whether a Steam lobby exists (invite button), the save slot, the save's custom
    // flag (continue / mode labels), every remote player (id, name, ready: sorted by id), and every remote coop state
    // (host continue button). Language is not in it (the lobby is never up while the options change it). Kept in memory
    // only, never logged (it holds player names).
    public static string Compose(bool hostMenu,string snapshot,int transport,bool steamLobby,int slot,bool customSave,IEnumerable<Remote> remotes,IEnumerable<CoopState> coop)
    {
        var s=new StringBuilder(160);
        s.Append(hostMenu?'H':'C').Append('|').Append(snapshot??"").Append('|').Append(transport.ToString(CultureInfo.InvariantCulture)).Append('|')
         .Append(steamLobby?'S':'-').Append('|').Append(slot.ToString(CultureInfo.InvariantCulture)).Append('|').Append(customSave?'c':'n').Append("|P");
        foreach(var r in (remotes??Enumerable.Empty<Remote>()).Where(r=>r!=null).OrderBy(r=>r.Id))
            s.Append(';').Append(r.Id.ToString(CultureInfo.InvariantCulture)).Append(':').Append(Escape(r.Name)).Append(':').Append(r.Ready?'R':'-');
        s.Append("|K");
        foreach(var c in (coop??Enumerable.Empty<CoopState>()).Where(c=>c!=null).OrderBy(c=>c.Id))
            s.Append(';').Append(c.Id.ToString(CultureInfo.InvariantCulture)).Append(':').Append(c.HasContinueSave?'1':'0').Append(':').Append(Escape(c.CoopId));
        return s.ToString();
    }
    // Names may hold the separators: length-prefixed, so two different lists never compose to the same text.
    static string Escape(string v)=>v==null?"-1":v.Length.ToString(CultureInfo.InvariantCulture)+"#"+v;

    // The counters line: due at most every SummarySeconds and only when something changed since the last line.
    public static bool SummaryDue(double sinceLast,bool changed)=>changed&&!Fresh(sinceLast,SummarySeconds);
}
