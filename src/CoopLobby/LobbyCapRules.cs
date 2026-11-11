using System;
using System.Collections.Generic;
using System.Text;

// Pure decisions of the two-player cap (no game, mod or Harmony types: Test-Managed.ps1 compiles this file alone
// with LobbyCapChecks.cs). Times are Stopwatch ticks; callers convert the windows below with Ticks().
public static class LobbyCapRules
{
    // The reject line travels in the 3rd WELCOME field. A 0.5.1 client without this add-on prints that field in its
    // version-mismatch popup ("Other player: <field> (protocol 0)", NotifyProtocolMismatch IL 81546-81610), so the
    // text is plain ASCII (game font) without '|' (the validator needs exactly 3 fields, IL 4591-4611).
    public const string Marker="[dccoop-cap2]";
    public const string Reason="LOBBY FULL - 2 players max "+Marker;
    public const string ColorToken="+cpcol1";          // CoopLobby colour capability (SPEC Part COLOR)
    public const double PendingSeconds=10;             // a not yet handshaken peer keeps the seat this long
    public const double SteamResendSeconds=2;          // one reject line per refused SteamID per 2 s
    public const double BurstSeconds=15;               // refusals closer than this are one burst (one host toast)
    public const double PopupSettleSeconds=0.25, PopupFallbackSeconds=3, PopupGiveUpSeconds=12, ToastSweepSeconds=6;
    // A late-refused Steam peer is unregistered this long after its reject line (reliable, still in flight), so the
    // mod's TryClosePeer cannot drop it; well inside the mod's own 5 s session-fail grace.
    public const double SteamEvictSeconds=2;
    // A lobby notice no lobby layer picked up within this time is withdrawn (it would surface on a later screen).
    public const double LobbyNoticeKeepSeconds=1;

    public static long Ticks(double seconds,long frequency)=>(long)(seconds*frequency);

    // Another connection the host already holds when a new peer arrives.
    public readonly struct Peer
    {
        public readonly bool Rejected,Complete,Known;public readonly long Since;
        public Peer(bool rejected,bool complete,bool known,long since){Rejected=rejected;Complete=complete;Known=known;Since=since;}
    }
    // A peer holds the single client seat unless we refused it; a handshaken peer always holds it, a pending one
    // while it is young. A pending peer we never judged (cannot happen: every TCP connection passes our WELCOME gate)
    // holds it too, so an unexpected state refuses rather than admits a third player.
    public static bool Occupies(in Peer p,long now,long pending)=>!p.Rejected&&(p.Complete||!p.Known||now-p.Since<pending);
    public static bool SeatTaken(IReadOnlyList<Peer> others,long now,long pending)
    {
        for(int i=0;i<others.Count;i++)if(Occupies(others[i],now,pending))return true;
        return false;
    }

    public static bool IsWelcome(string line)=>line!=null&&line.StartsWith("WELCOME|",StringComparison.Ordinal);
    // Exactly the mod's HELLO branch test (StartsWith "HELLO", OrdinalIgnoreCase = 5, release IL 10030-10032). The two
    // tests before it ("ID|", "WELCOME") cannot match a line that starts with HELLO, so this line takes that branch.
    public static bool IsHello(string line)=>line!=null&&line.StartsWith("HELLO",StringComparison.OrdinalIgnoreCase);

    // Late seat decision at HELLO (before the original HELLO branch): the peer is refused when the part runs, the
    // connection that holds the sender id is one we have not refused, and another player is already handshaken. Same
    // rule as HandshakeVerdict.RefuseLate below, taken one step earlier so the HELLO branch never runs for it.
    public static bool RefuseAtHello(bool hostRole,bool hello,bool running,bool connPresent,bool connRefused,bool otherComplete)
        =>hostRole&&hello&&running&&connPresent&&!connRefused&&otherComplete;

    // Reject line from the host's own WELCOME: protocol 0, our reason, and the WELCOME's capability tokens kept as
    // they are, so every installed CoopSyncFixes validator (exact-token strips, then EndsWith "+csf1.3a") passes on
    // the client and the base validator fails on protocol 0 with remoteBuild = Reason. Our own colour token is
    // dropped: a client without the colour part would leave it in place and fail CSF's EndsWith check instead.
    public static string RejectLine(string welcome)
    {
        var tokens="";
        var parts=(welcome??"").TrimEnd('\r','\n').Split('|');
        if(parts.Length==3&&parts[0]=="WELCOME")
        {
            var build=parts[2].Trim();int plus=build.IndexOf('+');
            if(plus>=0)tokens=build.Substring(plus);
        }
        var kept=new StringBuilder();
        foreach(var t in tokens.Split('+'))
            if(t.Length>0&&"+"+t!=ColorToken&&Printable(t))kept.Append('+').Append(t);
        return "WELCOME|0|"+Reason+kept+"\n";
    }
    static bool Printable(string t){foreach(char c in t)if(c<' '||c=='|'||c==127)return false;return true;}

    // Client side: the base validator parsed protocol 0 and kept the reason (or a CSF prefix failed and kept field 3).
    public static bool IsRejectBuild(int remoteProtocol,string remoteBuild)=>remoteProtocol==0&&remoteBuild!=null&&remoteBuild.IndexOf(Marker,StringComparison.Ordinal)>=0;

    // HandleLine on the host: drop a line when the connection that now holds the sender id is one we refused, or,
    // when nothing holds that id any more, the last connection we saw with it was refused (its lines can still sit
    // in the main-thread queue after CleanupHostClient). Ids are reused, so the record is by connection identity.
    // When this is false for an id that has a record, the record no longer applies (an admitted connection holds the
    // id, or the refused one is gone and collected) and is cleared, so later lines skip the lock.
    public static bool DropLine(bool hostRole,bool hasSender,bool currentPresent,bool currentRejected,bool recordedRejected)
        =>hostRole&&hasSender&&(currentPresent?currentRejected:recordedRejected);

    // CompleteHostHandshake is the one place where a peer starts to count (ConnectedClientCount feeds boss HP, exit
    // gating, HUD). A refused peer never completes. While the part runs, a peer that reaches it while another player
    // is already handshaken is refused late (backstop only: RefuseAtHello normally refuses it before the HELLO branch
    // calls CompleteHostHandshake). After a fault trip only the first rule stays.
    public enum HandshakeVerdict{Allow,Refuse,RefuseLate}
    public static HandshakeVerdict Handshake(bool rejected,bool tripped,bool otherComplete)
        =>rejected?HandshakeVerdict.Refuse:tripped?HandshakeVerdict.Allow:otherComplete?HandshakeVerdict.RefuseLate:HandshakeVerdict.Allow;

    public static bool RateAllows(long last,long now,long interval)=>last==0||now<last||now-last>=interval;
    public static bool NewBurst(long previous,long now,long burst)=>previous==0||now<previous||now-previous>=burst;

    // Refused-join popup on the client: wait until the mod's disconnect path cleared the waiting menu (it does not
    // rebuild the menu, NotifyRemoteDisconnected IL 72412-72433), or the fallback time; give up without a screen.
    public enum PopupStep{Idle,Wait,Show,GiveUp}
    public static PopupStep Popup(long since,long now,bool waitingMenu,bool haveScreen,long settle,long fallback,long giveUp)
    {
        if(since==0)return PopupStep.Idle;
        long age=now-since;
        if(age>=giveUp)return PopupStep.GiveUp;
        if(!haveScreen)return PopupStep.Wait;
        return age>=fallback||(!waitingMenu&&age>=settle)?PopupStep.Show:PopupStep.Wait;
    }

    public static int NewestIndex(IReadOnlyList<string> texts,string target)
    {
        if(string.IsNullOrEmpty(target))return -1;
        for(int i=texts.Count-1;i>=0;i--)if(texts[i]==target)return i;
        return -1;
    }

    // Keeps the per-SteamID resend map small: entries older than keep are dropped once it grows.
    public static void Prune(Dictionary<ulong,long> sent,long now,long keep,int maxEntries)
    {
        if(sent.Count<=maxEntries)return;
        var old=new List<ulong>();
        foreach(var kv in sent)if(now-kv.Value>=keep||now<kv.Value)old.Add(kv.Key);
        foreach(var k in old)sent.Remove(k);
    }
}
