using System;
using System.Globalization;
using System.Text;

// Pure decisions of the colour part (no game types; Test-Managed.ps1 compiles this file with LobbyColorChecks.cs alone).
//
// Wire (one '\n'-terminated ASCII line each, fields split by '|'). Nothing is added to the HELLO/WELCOME handshake:
//   client->host HI  : CPCOL1|HI|1|<nonce>              once the client's WELCOME was accepted and its ID line applied
//   client->host REQ : CPCOL1|REQ|<seq>|<index>|<nonce>
//   host->client SET : CPCOL1|SET|<rev>|<id>=<index>[,<id>=<index>]...|<ack>|<nonce>
// Why no handshake token (SPEC's "+cpcol1"): every peer that does not strip it rejects the connection. The base compares
// the build field with "0.8.91" exactly (release IL 4669-4674 -> RejectIncompatiblePeer true, IL 4704-4750) and CSF
// requires EndsWith("+csf1.3a") (CSF Join.cs:51), so a colour part that is absent, switched off or rolled back on ONE
// machine would block joining. HI travels after the handshake instead. Release HandleLine ignores an unknown tag
// (forwardLine=null at IL 9908-9910, TryParsePositionLine fails on the non-numeric first field at IL 14020-14021, return
// true at IL 14204-14205) and never forwards it (only when forwardLine!=null, IL 166712-166731 / 166573-166592). So a host
// without the part drops HI silently and the client falls back to defaults after HiTimeoutMs; the host sends SET only to
// connections that said HI, so a client without the part never gets one.
// nonce: 32 lowercase hex chars the CLIENT makes per accepted WELCOME (one connection epoch). The host binds the
// connection object to the latest nonce it heard in HI; REQ and SET must carry it. A message of an older epoch, or of a
// dropped connection whose id 2 was reused, is dropped (player-color verify.md section 2 item 2). A Steam client that
// comes back on the same SteamClientConnection object (TryGetOrRegisterSteamClient returns the registered one for a
// known SteamID, IL 19582-19599) starts over with its new nonce.
// seq: the client's request counter (1, 2, ...) per epoch. rev: the host's table revision, bumped on every table change
// and every handled REQ; a client applies a SET only when rev grew. ack: the last REQ seq the host handled in this epoch
// (0 = none); once ack reaches the request's seq the requester compares its own row with what it asked for: equal =
// granted, otherwise denied (no separate DENY message). index: palette index 0..Colors-1. Ids 1 (host) .. MaxId.
// The leading fields are SPEC.md's "CPCOL1|REQ|seq|index" and "CPCOL1|SET|rev|id=index,id=index"; ack and nonce are
// appended for the connection binding the SPEC asks for.
public static class LobbyColorRules
{
    public const string Tag="CPCOL1|",HiVersion="1";
    public const int Colors=6,MaxId=4,HostId=1,MaxLine=256;
    // HI is resent while unanswered (a HI that raced the host's CompleteHostHandshake is dropped there), at most HiMaxSends
    // times. Without a SET of this epoch after HiTimeoutMs the partner counts as one without colour support (both sides
    // use the same wait). The host answers a repeated HI of the same epoch at most every HiAnswerMs.
    public const long HiIntervalMs=1500,HiTimeoutMs=6000,HiAnswerMs=1000;
    public const int HiMaxSends=4;

    // ---- messages ----
    public enum Kind{None,Hi,Req,Set}
    public sealed class Message
    {
        public Kind Kind;public int Number;       // REQ: seq, SET: rev
        public int Index=-1;                     // REQ only
        public int Ack;public string Nonce;      // SET: ack; all: nonce
        public int[] Table;                      // SET only, by id (0 unused), -1 = no row
    }
    public static bool ValidColor(int index)=>index>=0&&index<Colors;
    public static bool ValidId(int id)=>id>=1&&id<=MaxId;
    public static int[] NewTable(){var t=new int[MaxId+1];for(int i=0;i<t.Length;i++)t[i]=-1;return t;}
    public static bool IsNonce(string s)
    {
        if(s==null||s.Length!=32)return false;
        foreach(char c in s)if(!(c>='0'&&c<='9'||c>='a'&&c<='f'))return false;
        return true;
    }
    public static string NewNonce()=>Guid.NewGuid().ToString("N");
    static string I(int v)=>v.ToString(CultureInfo.InvariantCulture);
    public static string Hi(string nonce)=>Tag+"HI|"+HiVersion+"|"+nonce+"\n";
    public static string Req(int seq,int index,string nonce)=>Tag+"REQ|"+I(seq)+"|"+I(index)+"|"+nonce+"\n";
    public static string Set(int rev,int[] table,int ack,string nonce)
    {
        var b=new StringBuilder(Tag).Append("SET|").Append(I(rev)).Append('|');
        bool first=true;
        for(int id=1;id<=MaxId&&id<table.Length;id++)
        {
            if(!ValidColor(table[id]))continue;
            if(!first)b.Append(',');
            b.Append(I(id)).Append('=').Append(I(table[id]));first=false;
        }
        return b.Append('|').Append(I(ack)).Append('|').Append(nonce).Append('\n').ToString();
    }
    // Digits only (no sign, no spaces, no culture), 1..9 characters.
    static bool Num(string s,out int v)
    {
        v=0;if(string.IsNullOrEmpty(s)||s.Length>9)return false;
        foreach(char c in s)if(c<'0'||c>'9')return false;
        return int.TryParse(s,NumberStyles.None,CultureInfo.InvariantCulture,out v);
    }
    // Strict parse of one of our lines. Anything else (unknown kind or version, extra fields, bad numbers, duplicate ids
    // or colours, oversized) is rejected; the caller consumes the line anyway.
    public static bool TryParse(string line,out Message m)
    {
        m=null;
        if(line==null||line.Length>MaxLine||!line.StartsWith(Tag,StringComparison.Ordinal))return false;
        var parts=line.TrimEnd('\r','\n').Split('|');
        if(parts.Length<2)return false;
        if(parts[1]=="HI")
        {
            if(parts.Length!=4||parts[2]!=HiVersion||!IsNonce(parts[3]))return false;
            m=new Message{Kind=Kind.Hi,Nonce=parts[3]};return true;
        }
        if(parts[1]=="REQ")
        {
            if(parts.Length!=5||!Num(parts[2],out int seq)||seq<1||!Num(parts[3],out int index)||!ValidColor(index)||!IsNonce(parts[4]))return false;
            m=new Message{Kind=Kind.Req,Number=seq,Index=index,Nonce=parts[4]};return true;
        }
        if(parts[1]=="SET")
        {
            if(parts.Length!=6||!Num(parts[2],out int rev)||rev<1||!Num(parts[4],out int ack)||!IsNonce(parts[5])||parts[3].Length==0)return false;
            var table=NewTable();
            foreach(var entry in parts[3].Split(','))
            {
                int eq=entry.IndexOf('=');
                if(eq<=0||!Num(entry.Substring(0,eq),out int id)||!ValidId(id)||table[id]>=0||
                   !Num(entry.Substring(eq+1),out int index)||!ValidColor(index)||Taken(table,index,id))return false;
                table[id]=index;
            }
            m=new Message{Kind=Kind.Set,Number=rev,Ack=ack,Nonce=parts[5],Table=table};return true;
        }
        return false;
    }

    // ---- host decisions (host = id 1, authoritative; one table per host session) ----
    public static bool Taken(int[] table,int index,int exceptId)
    {
        for(int id=1;id<=MaxId&&id<table.Length;id++)if(id!=exceptId&&table[id]==index)return true;
        return false;
    }
    // Who other than exceptId holds a colour, or -1: nobody (a denial for another reason, e.g. picks frozen for the run
    // or the holder left between the REQ and the SET).
    public static int Holder(int[] table,int index,int exceptId)
    {
        if(table==null||!ValidColor(index))return -1;
        for(int id=1;id<=MaxId&&id<table.Length;id++)if(id!=exceptId&&table[id]==index)return id;
        return -1;
    }
    // The host's own colour: its saved pick, else the host default.
    public static int HostColor(int preferred,int hostDefault)=>ValidColor(preferred)?preferred:hostDefault;
    // A joining peer: the wished colour (the client default) when free, else the first free one in palette order.
    // The host keeps its own colour on a clash at connect; the client's saved pick follows as a normal REQ.
    public static int JoinColor(int[] table,int id,int wish)
    {
        if(ValidColor(wish)&&!Taken(table,wish,id))return wish;
        for(int i=0;i<Colors;i++)if(!Taken(table,i,id))return i;
        return -1;
    }
    // A request (remote REQ or the host's own pick): granted when the colour is free or already this id's.
    // Denied leaves the table unchanged. Both kinds run on the game thread in arrival order, so the first one wins.
    public static bool TryGrant(int[] table,int id,int wanted)
    {
        if(!ValidId(id)||id>=table.Length||!ValidColor(wanted))return false;
        if(table[id]==wanted)return true;
        if(Taken(table,wanted,id))return false;
        table[id]=wanted;return true;
    }
    // Resend: on change, on first contact, then every period: often in the lobby (the UI shows it), rarely in a run (a
    // backstop for a client whose epoch changed mid-run, e.g. Steam's second WELCOME of a mid-run join). A clock that
    // went backwards resends.
    public static bool ShouldSend(bool changed,bool inLobby,long now,long lastSent,long lobbyPeriod,long runPeriod)
        =>changed||lastSent==0||now<lastSent||now-lastSent>=(inLobby?lobbyPeriod:runPeriod);

    // Host bookkeeping of one connection object (game thread).
    public sealed class HostPeer
    {
        public bool Supports;public string Nonce;public int HandledSeq,SentRev;public long LastSent;
        // HI with a new nonce: a new client epoch on this connection object (its next accepted WELCOME, or a reconnect
        // that reuses the object): start over with a fresh ack and send state, so its REQ seq 1 is not taken for a replay.
        // The same nonce again: the client has not seen our SET yet, answer again (rate limited). True for a new epoch.
        public bool OnHi(string nonce,long now)
        {
            if(!IsNonce(nonce))return false;
            if(Supports&&Nonce==nonce){if(LastSent==0||now<LastSent||now-LastSent>=HiAnswerMs)SentRev=0;return false;}
            Supports=true;Nonce=nonce;HandledSeq=0;SentRev=0;LastSent=0;return true;
        }
        // A REQ of the current epoch not handled yet (an older seq is a replay or a reorder: already answered by an ack).
        public bool TakeReq(Message m)
        {
            if(!Supports||m==null||m.Kind!=Kind.Req||m.Nonce!=Nonce||m.Number<=HandledSeq)return false;
            HandledSeq=m.Number;return true;
        }
    }

    // ---- client decisions ----
    public static bool AcceptSet(int lastRev,int rev)=>rev>lastRev;
    public enum Answer{None,Granted,Denied}
    // The pending request is answered once the host's ack reaches its seq.
    public static Answer Resolve(int pending,int pendingSeq,int ack,int[] table,int myId)
    {
        if(!ValidColor(pending)||pendingSeq<1||ack<pendingSeq)return Answer.None;
        return ValidId(myId)&&myId<table.Length&&table[myId]==pending?Answer.Granted:Answer.Denied;
    }
    // After the first SET of an epoch: ask for the saved pick once if the host gave us something else.
    public static bool NeedsInitialRequest(int preferred,int assigned)=>ValidColor(preferred)&&preferred!=assigned;
    public static bool Expired(long now,long since,long timeout)=>since>0&&(now<since||now-since>=timeout);

    // Client bookkeeping of one NetNode (game thread).
    public sealed class ClientLink
    {
        public int Epoch;public bool WelcomeAccepted,Answered,GaveUp,InitialSent,PendingExplicit;public string Nonce;
        public int LastRev,Seq,PendingSeq,HiSends;public long HiFirst,HiLast,PendingSince;
        // Every accepted WELCOME is a new epoch with a new nonce: the Steam host sends one per HELLO (HandleLine IL
        // 10076-10121 -> SendInitialStateToSteamClient force:true) and one at registration, a reconnect sends another.
        public void NewEpoch()
        {
            Epoch++;WelcomeAccepted=true;Nonce=NewNonce();
            Answered=GaveUp=InitialSent=PendingExplicit=false;LastRev=Seq=PendingSeq=HiSends=0;HiFirst=HiLast=PendingSince=0;
        }
        public bool HiDue(long now)=>WelcomeAccepted&&Nonce!=null&&!Answered&&HiSends<HiMaxSends&&(HiSends==0||now<HiLast||now-HiLast>=HiIntervalMs);
        public void SentHi(long now){if(now==0)now=1;HiSends++;HiLast=now;if(HiFirst==0)HiFirst=now;}
        public bool GiveUpDue(long now)=>!Answered&&!GaveUp&&Expired(now,HiFirst,HiTimeoutMs);
        // A SET of this epoch with a newer revision; the first one is the host's "colours supported".
        public bool TakeSet(Message m)
        {
            if(m==null||m.Kind!=Kind.Set||Nonce==null||m.Nonce!=Nonce||!AcceptSet(LastRev,m.Number))return false;
            LastRev=m.Number;Answered=true;GaveUp=false;return true;
        }
        public int NextSeq(long now,bool explicitPick){Seq++;PendingSeq=Seq;PendingSince=now==0?1:now;PendingExplicit=explicitPick;return Seq;}
    }

    // ---- persistence: the short palette key ("ice") of the last explicit pick; anything else means no pick ----
    public static string StoreKey(string paletteKey)=>paletteKey!=null&&paletteKey.StartsWith("color.",StringComparison.Ordinal)?paletteKey.Substring(6):paletteKey;
    public static int ParsePreference(string stored,string[] shortKeys)
    {
        if(string.IsNullOrEmpty(stored)||shortKeys==null)return -1;
        for(int i=0;i<shortKeys.Length&&i<Colors;i++)if(string.Equals(shortKeys[i],stored,StringComparison.Ordinal))return i;
        return -1;
    }

    // ---- render nesting (FrameFixRules.EnterRender/LeaveRender of CoopFrameFix): only the outermost render tints ----
    public static bool EnterRender(ref int depth)=>depth++==0;
    public static bool LeaveRender(ref int depth)
    {
        if(depth<=0){depth=0;return true;}       // unbalanced leave: restore anyway, never leave colours multiplied
        return --depth==0;
    }
    // Multiplier of one 0xRRGGBB channel (shift 16, 8, 0) as the swatch and the tint use it.
    public static double Channel(int rgb,int shift)=>((rgb>>shift)&255)/255.0;
}
