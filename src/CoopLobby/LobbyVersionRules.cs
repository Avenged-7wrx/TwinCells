using System;
using System.Globalization;

// Pure rules of the Twin Cells Alpha2 version gate (CoopLobby part VERSION, LobbyVersion.cs; Alpha2 integration U15,
// PLAN-ALPHA2.md section 7 constraint 4 and section 8). No game, mod or Harmony types: Test-Managed.ps1 compiles this
// file with the other *Rules.cs and checks\LobbyVersionChecks.cs, which also replays the whole HELLO/WELCOME validator
// chain of Alpha1 and Alpha2 peers in both directions.
//
// Why: an Alpha1 and an Alpha2 peer must not play together, and the Alpha2 side must say why. Before this part the only
// Alpha2-only handshake token was CSF's optional +csfelev2. With it, an Alpha1 peer refuses the Alpha2 line with CSF's
// "Both players require CoopSyncFixes 1.3.0 integration candidate" (the Alpha1 chain strips +csfcine1/+csfelev1 only, so
// the line no longer ends with +csf1.3a); without it (COOP_SYNC_DISABLE_ELEVATOR_RIDE=1 or a RIDE install failure) the
// two versions passed each other's handshake. And an Alpha2 peer always accepted an Alpha1 line (+csfelev2 is optional).
//
// Token: "+TwinCells-Alpha2" in the third '|' field (the build field) of HELLO and WELCOME, appended right after the base
// build by a Priority.First postfix, so it precedes CSF's +csf1.3a (CSF's Priority.Normal postfix) and the Priority.Last
// CSF tokens: "HELLO|19|0.8.91+TwinCells-Alpha2+csf1.3a+csfcine1+csfelev1+csfelev2". Every Alpha2 chain strips the one
// complete token (prefix Priority.First, the exact-token rule of CSF's optional tokens: the next character is '+' or the
// end, and it occurs once) and refuses a line without it (prefix Priority.Last, after every CSF validator). The one
// exception is the CoopLobby lobby-full reject line (protocol 0 plus the CAP marker), which CAP turns into its own
// "lobby is full" popup: an Alpha1 host's reject carries no Alpha2 token and is still shown as lobby full.
// ASCII, no '|', inside the game fonts: an Alpha1 peer prints the field it could not validate in its version-mismatch
// popup ("Other player: <field> (protocol N)"), so the token names the version in that line.
public static class LobbyVersionRules
{
    public const string Token="+TwinCells-Alpha2";
    public const string Release="Twin Cells Alpha2";
    public const string Failure="Both players require Twin Cells Alpha2 (handshake token +TwinCells-Alpha2)";
    public const string CapMarker="[dccoop-cap2]";      // = LobbyCapRules.Marker (checked by LobbyVersionChecks)

    // Index of the one complete token in a build field, or -1: absent, repeated, or only a longer token such as
    // "+TwinCells-Alpha2x". A token that is not exactly one complete occurrence is left in place (the line is then
    // refused: the base validator compares the build exactly).
    public static int TokenIndex(string build)
    {
        if(build==null)return -1;
        int at=build.IndexOf(Token,StringComparison.Ordinal);
        if(at<0||at+Token.Length<build.Length&&build[at+Token.Length]!='+')return -1;
        return build.IndexOf(Token,at+Token.Length,StringComparison.Ordinal)>=0?-1:at;
    }
    public static bool HasToken(string line)
    {
        var parts=(line??"").TrimEnd('\r','\n').Split('|');
        return parts.Length==3&&TokenIndex(parts[2])>=0;
    }

    // BuildHelloLine/BuildWelcomeLine postfix (Priority.First): the base line "HELLO|19|0.8.91\n" gets the token right after
    // the build. A malformed line (not exactly three fields) or one that already holds the token is left unchanged.
    public static string Advertise(string line)
    {
        if(line==null)return null;
        var trimmed=line.TrimEnd('\r','\n');
        var parts=trimmed.Split('|');
        if(parts.Length!=3||parts[2].IndexOf(Token,StringComparison.Ordinal)>=0)return line;
        return trimmed+Token+"\n";
    }

    public enum Seen{None=0,Present=1,Absent=2}

    // TryValidateHandshakeLine prefix (Priority.First): removes the one complete token from the build field and reports
    // whether it was there. raw is the build field as received (before any validator stripped anything).
    public static Seen Strip(ref string line,out string raw)
    {
        raw=null;
        var parts=(line??"").TrimEnd('\r','\n').Split('|');
        if(parts.Length!=3)return Seen.Absent;
        raw=parts[2];
        int at=TokenIndex(parts[2]);
        if(at<0)return Seen.Absent;
        parts[2]=parts[2].Remove(at,Token.Length);
        line=string.Join("|",parts);
        return Seen.Present;
    }

    // The CoopLobby CAP reject line after the other validators ran: protocol field "0" and the CAP marker in the build
    // field (LobbyCapRules.RejectLine / IsRejectBuild). CAP shows its own "lobby is full" popup for it.
    public static bool CapReject(string line)
    {
        var parts=(line??"").TrimEnd('\r','\n').Split('|');
        return parts.Length==3&&parts[1].Trim()=="0"&&parts[2].IndexOf(CapMarker,StringComparison.Ordinal)>=0;
    }

    public enum Verdict{Pass,DeferToCap,Refuse}
    // TryValidateHandshakeLine prefix (Priority.Last): Present passes (the base validator then compares the rest exactly);
    // None means the strip prefix did not run for this call (a fault): nothing is known, so nothing is refused here;
    // Absent refuses, unless the line is a CAP reject (shown by CAP as "lobby is full").
    public static Verdict Decide(Seen seen,string line)
    {
        if(seen!=Seen.Absent)return Verdict.Pass;
        return CapReject(line)?Verdict.DeferToCap:Verdict.Refuse;
    }

    // The values the refusing prefix reports (RejectIncompatiblePeer logs them, NotifyProtocolMismatch receives them):
    // the protocol number of the line (0 when it is not a number) and the build field as received.
    public static int Protocol(string line)
    {
        var parts=(line??"").TrimEnd('\r','\n').Split('|');
        return parts.Length>=2&&int.TryParse(parts[1],NumberStyles.Integer,CultureInfo.InvariantCulture,out var p)?p:0;
    }
    public static string RemoteBuild(string raw)=>string.IsNullOrWhiteSpace(raw)?"unknown":raw.Trim();

    // The whole Priority.Last prefix as a pure function (LobbyVersion.RequireToken is a thin wrapper, so the checks and
    // the Harmony chain test run this exact code). Only a refusal writes the out-parameters of the original: a pass
    // leaves the values an earlier validator set. Returns false to skip the original validator.
    public static bool Require(Seen seen,string raw,ref string line,ref int remoteProtocol,ref string remoteBuild,ref string failure,ref bool result,out bool refused)
    {
        refused=false;
        if(Decide(seen,line)!=Verdict.Refuse)return true;
        refused=true;
        result=false;
        remoteProtocol=Protocol(line);
        remoteBuild=RemoteBuild(raw);
        failure=Failure;
        return false;
    }

    // A received, unprocessed WELCOME is evidence only when its shape and protocol are known. In the pinned mod,
    // TCP EOF can enqueue critical disconnect ahead of the WELCOME's main-thread callback. This receipt does not
    // validate/accept a connection and never claims a version mismatch from an EOF alone. A CAP reject (protocol 0),
    // unknown/malformed line, or an exact Alpha2 token stays entirely with the existing native handlers.
    public static bool ReceivedVersionRefusal(string line,out string build,out int protocol)
    {
        build=null;protocol=0;
        if(line==null||line.Length>512||!line.StartsWith("WELCOME|",StringComparison.Ordinal))return false;
        var parts=line.TrimEnd('\r','\n').Split('|');
        if(parts.Length!=3||parts[0]!="WELCOME"||parts[1]!="19"||string.IsNullOrWhiteSpace(parts[2]))return false;
        // The build field is an ASCII wire identifier, not free text. Never retain controls or arbitrary long text.
        foreach(char c in parts[2])if(c<33||c>126)return false;
        if(TokenIndex(parts[2])>=0||parts[2].IndexOf(CapMarker,StringComparison.Ordinal)>=0)return false;
        build=parts[2];protocol=19;return true;
    }

    public enum ReceiptContext{Drop,WaitForNativeDisconnect,Ready}
    public static ReceiptContext RecoveryContext(object owner,object current,object active,bool inRun,bool hasRemote)
    {
        if(owner==null||!ReferenceEquals(owner,current)||inRun||hasRemote||active!=null&&!ReferenceEquals(owner,active))return ReceiptContext.Drop;
        return active==null?ReceiptContext.Ready:ReceiptContext.WaitForNativeDisconnect;
    }
    public static bool PopupOwnerCurrent(object owner,object current,bool cancelled)
        =>owner!=null&&ReferenceEquals(owner,current)&&!cancelled;

    // One record per NetNode in a ConditionalWeakTable. Background hooks only touch these managed values; game/UI
    // access happens on the frame thread. First WELCOME wins, including a valid/same-version or malformed first line.
    // MarkHandled also covers explicit Dispose and any native mismatch (including CAP), so cleanup cannot duplicate UI.
    public sealed class ReceivedWelcome
    {
        readonly object _sync=new();
        bool _observed,_cleaned,_handled,_cancelled;
        long _cleanedAt;
        string _build;
        int _protocol;
        public void Observe(string line)
        {
            lock(_sync)
            {
                if(_observed||_handled)return;
                _observed=true;
                if(ReceivedVersionRefusal(line,out var build,out var protocol)){_build=build;_protocol=protocol;}
            }
        }
        public bool Cleaned(long now)
        {
            lock(_sync){if(!_cleaned){_cleaned=true;_cleanedAt=now;}return !_handled&&_build!=null;}
        }
        public void MarkHandled(){lock(_sync){_handled=true;_build=null;}}
        public void Cancel(){lock(_sync){_cancelled=true;_handled=true;_build=null;}}
        public bool Cancelled{get{lock(_sync)return _cancelled;}}
        public bool Expire(long now,long timeout)
        {
            lock(_sync)
            {
                if(!_cleaned||_handled||_build==null||now-_cleanedAt<=timeout)return false;
                _handled=true;_build=null;return true;
            }
        }
        public bool Take(out string build,out int protocol)
        {
            lock(_sync)
            {
                build=null;protocol=0;
                if(!_cleaned||_handled||_build==null)return false;
                build=_build;protocol=_protocol;_handled=true;_build=null;return true;
            }
        }
    }

    // ---- texts (game language: en and ru, every other language uses en, as LobbyText) ----
    // Inside the game fonts' glyph sets: no em dash, ellipsis, NBSP or typographic quotes.
    public static string Title(bool ru)=>ru?"Другая версия Twin Cells":"Different Twin Cells version";
    public static string ClientText(bool ru)=>ru?"Обоим игрокам нужна Twin Cells Alpha2."
        :"Both players need Twin Cells Alpha2.";
    public static string HostNotice(bool ru)=>ru?"Игрок с другой версией Twin Cells не подключён: обоим игрокам нужна Twin Cells Alpha2"
        :"A player with another Twin Cells version was refused: both players need Twin Cells Alpha2";
}
