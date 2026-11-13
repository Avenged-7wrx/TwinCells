using System;
using System.Globalization;
using System.Text.RegularExpressions;

// Pure rules of CoopTransitionGate (Alpha2 unit U10, PLAN-ALPHA2.md issue 3; OWNER-DECISIONS OD2 and Q3). No game types:
// compiled alone with CoopTransitionGateChecks.cs by Test-Managed.ps1.
//
// The doors behind Guillain (en.inter.npc.PerkMaster) and the Collector (en.inter.npc.CollectorPanelNpc) are per-player
// gates: every 2 s NPC.fixedUpdate calls setDoor(wantOpen) from the LOCAL game state (PerkMaster f@26023: wantOpen =
// getAvailablePerks()<=0; CollectorPanelNpc f@14453, within 15 cells: wantOpen = no blueprint to give and not (cells>0 and
// an item to unlock)). setDoor (f@26022 / f@14449) opens a closed TriggeredDoor of the NPC's room when wantOpen, and
// closeFast()s an open one when not. So the faster player walks on alone.
// The gate: when wantOpen is true and the partner is not done yet, the native setDoor is not called (for a closed door that
// equals the vanilla setDoor(false), which does nothing to a closed door). Every other call goes to the native function
// unchanged. Release (latched per NPC for this level instance, never closed again by the gate) when the partner is done at
// the same NPC, downed, disconnected, has left the level (3 s), or lacks the gate. No time limit (OWNER-DECISIONS Q3).
//
// Wire (new tag CPTG1|, capability in-band: a peer is "capable" while its CPTG1 lines arrive):
//   CPTG1|C|<senderId>                                             capability heartbeat, every 2 s while in a run
//   CPTG1|D|<senderId>|<level>|<branch>|<npc>|<cx>:<cy>|1          "done" at that NPC, on change and every 0.5 s while in the level
// branch: CoopBranchVisibilityFix token (0 = main map), or "-" when not known (then any branch matches).
public static class CoopTransitionGateRules
{
    public const string Tag="CPTG1|";
    public const string NpcPerk="PerkMaster",NpcCollector="Collector";
    public const int KindPerk=1,KindCollector=2;
    public const long CapabilityTtlMs=10000,CapabilitySendMs=2000,DoneResendMs=500,LeftLevelMs=3000,WaitMessageMs=8000;
    public const int MessageRadiusCells=24,MaxCell=100000,MaxLineLength=256,InboxCap=64;
    public const int MaxConsecutiveFaults=8,MaxTotalFaults=64;
    static readonly Regex LevelRx=new("^[A-Za-z0-9_./-]{1,64}$",RegexOptions.CultureInvariant);

    public static string NpcName(int kind)=>kind==KindPerk?NpcPerk:kind==KindCollector?NpcCollector:null;

    // ---- key ----
    public readonly struct GateKey
    {
        public readonly string Level,Branch,Npc;public readonly int Cx,Cy;
        public GateKey(string level,string branch,string npc,int cx,int cy){Level=level;Branch=branch;Npc=npc;Cx=cx;Cy=cy;}
        public override string ToString()=>Level+"|"+Branch+"|"+Npc+"|"+Cx.ToString(CultureInfo.InvariantCulture)+":"+Cy.ToString(CultureInfo.InvariantCulture);
    }
    public static string Branch(bool fromBranchPart,int token,bool subLevel,bool secret)
    {
        if(fromBranchPart&&token>=0)return token.ToString(CultureInfo.InvariantCulture);
        return !subLevel&&!secret?"0":"-";           // 0 is the main map for every version; a sub/secret level without the part is unknown
    }
    public static bool ValidLevel(string level)=>level!=null&&LevelRx.IsMatch(level);
    public static bool ValidBranch(string branch)=>branch=="-"||(branch!=null&&int.TryParse(branch,NumberStyles.None,CultureInfo.InvariantCulture,out var v)&&v>=0&&v.ToString(CultureInfo.InvariantCulture)==branch);
    public static bool ValidNpc(string npc)=>npc==NpcPerk||npc==NpcCollector;
    public static bool ValidCell(int v)=>v>=0&&v<=MaxCell;
    public static bool TryKey(string level,string branch,string npc,int cx,int cy,out GateKey key)
    {
        key=default;
        if(!ValidLevel(level)||!ValidBranch(branch)||!ValidNpc(npc)||!ValidCell(cx)||!ValidCell(cy))return false;
        key=new GateKey(level,branch,npc,cx,cy);return true;
    }
    // An NPC's cell is not stable to the cell: Guillain read cy=27 for one sample while standing on cy=28 (native run
    // a2-U9-presence-U10-gate-3, the hero on his cell). setDoor keys on the cell read at that call, so the two peers' keys
    // (and two calls on one peer) may differ by a cell. A key matches within CellTolerance cells on both axes; the local
    // entry is anchored at its first key, so one peer always sends the same key. One Guillain/Collector per level room.
    public const int CellTolerance=2;
    public static bool SameCell(int ax,int ay,int bx,int by)=>Math.Abs((long)ax-bx)<=CellTolerance&&Math.Abs((long)ay-by)<=CellTolerance;
    public static bool SameGate(GateKey a,GateKey b)=>a.Level==b.Level&&a.Npc==b.Npc&&SameCell(a.Cx,a.Cy,b.Cx,b.Cy)&&
        (a.Branch==b.Branch||a.Branch=="-"||b.Branch=="-");

    // ---- wire ----
    public enum Kind{None,Capability,Done}
    public readonly struct Message
    {
        public readonly Kind Kind;public readonly int Sender;public readonly GateKey Key;
        public Message(Kind kind,int sender,GateKey key){Kind=kind;Sender=sender;Key=key;}
    }
    public static string Capability(int sender)=>Tag+"C|"+sender.ToString(CultureInfo.InvariantCulture)+"\n";
    public static string Done(int sender,GateKey k)=>Tag+"D|"+sender.ToString(CultureInfo.InvariantCulture)+"|"+k.Level+"|"+k.Branch+"|"+k.Npc+"|"+
        k.Cx.ToString(CultureInfo.InvariantCulture)+":"+k.Cy.ToString(CultureInfo.InvariantCulture)+"|1\n";
    static bool Id(string s,out int v)=>int.TryParse(s,NumberStyles.None,CultureInfo.InvariantCulture,out v)&&v>0&&v.ToString(CultureInfo.InvariantCulture)==s;
    static bool Cell(string s,out int v)=>int.TryParse(s,NumberStyles.None,CultureInfo.InvariantCulture,out v)&&ValidCell(v)&&v.ToString(CultureInfo.InvariantCulture)==s;
    public static bool TryParse(string line,out Message m)
    {
        m=default;
        if(line==null||line.Length>MaxLineLength||!line.StartsWith(Tag,StringComparison.Ordinal))return false;
        var p=line.TrimEnd('\r','\n').Split('|');
        if(p.Length==3&&p[1]=="C"&&Id(p[2],out var c)){m=new Message(Kind.Capability,c,default);return true;}
        if(p.Length!=8||p[1]!="D"||p[7]!="1"||!Id(p[2],out var sender))return false;
        var cell=p[6].Split(':');
        if(cell.Length!=2||!Cell(cell[0],out var cx)||!Cell(cell[1],out var cy))return false;
        if(!TryKey(p[3],p[4],p[5],cx,cy,out var key))return false;
        m=new Message(Kind.Done,sender,key);return true;
    }

    // ---- decision at setDoor(wantOpen) ----
    public enum Verdict{Pass,Hold,Release}
    public struct Inputs
    {
        public bool WantOpen,Enabled,Session,Connected,SinglePartner,PartnerCapable,PartnerDowned,PartnerLeftLevel,PartnerDone,Released;
    }
    // Pass: call the native setDoor unchanged. Hold: do not call it (the door stays closed). Release: latch and call it.
    public static Verdict Decide(Inputs i,out string reason)
    {
        if(!i.WantOpen){reason="closing";return Verdict.Pass;}            // vanilla setDoor(false), never touched
        if(!i.Enabled){reason="off";return Verdict.Pass;}
        if(!i.Session){reason="solo";return Verdict.Pass;}                // no co-op session: vanilla
        if(i.Released){reason="released";return Verdict.Pass;}
        if(!i.Connected){reason="partner-disconnected";return Verdict.Release;}
        if(!i.SinglePartner){reason="more-than-two-players";return Verdict.Release;}
        if(!i.PartnerCapable){reason="partner-without-gate";return Verdict.Release;}
        if(i.PartnerDowned){reason="partner-downed";return Verdict.Release;}
        if(i.PartnerLeftLevel){reason="partner-left-level";return Verdict.Release;}
        if(i.PartnerDone){reason="partner-done";return Verdict.Release;}
        reason="waiting";return Verdict.Hold;
    }

    // ---- partner left the level: another level id for LeftLevelMs, latched for this level instance ----
    public sealed class LeftLevel
    {
        long since;public bool Latched{get;private set;}
        public void Reset(){since=0;Latched=false;}
        public bool Step(bool partnerLevelKnown,bool sameLevel,long nowMs)
        {
            if(Latched)return true;
            if(!partnerLevelKnown||sameLevel){since=0;return false;}
            if(since==0)since=nowMs==0?1:nowMs;
            if(nowMs-since>=LeftLevelMs)Latched=true;
            return Latched;
        }
    }
    public static bool Capable(long lastSeenMs,long nowMs)=>lastSeenMs>0&&nowMs>=lastSeenMs&&nowMs-lastSeenMs<=CapabilityTtlMs;
    public static bool Due(long lastMs,long nowMs,long periodMs)=>lastMs==0||nowMs<lastMs||nowMs-lastMs>=periodMs;
    public static bool Near(int heroCx,int heroCy,int npcCx,int npcCy)=>Math.Abs((long)heroCx-npcCx)<=MessageRadiusCells&&Math.Abs((long)heroCy-npcCy)<=MessageRadiusCells;
    public static bool ShowWait(bool held,bool released,bool near,long lastShownMs,long nowMs)=>held&&!released&&near&&Due(lastShownMs,nowMs,WaitMessageMs);
    // A held door is opened at once (not on the NPC's next 2 s tick) when it becomes releasable and the local player is still done.
    public static bool OpenNow(bool held,bool released,bool lastWantOpen,Verdict now)=>held&&!released&&lastWantOpen&&now==Verdict.Release;
    // Partner-facing note when the wait ends for a reason other than the partner finishing.
    public static bool NoteRelease(bool held,string reason)=>held&&(reason=="partner-downed"||reason=="partner-left-level"||reason=="partner-disconnected");

    // ---- texts (game fonts: no em dash, ellipsis, NBSP) ----
    public static string Text(string what,bool ru,string name)
    {
        bool named=!string.IsNullOrWhiteSpace(name);string n=named?name.Trim():(ru?"напарника":"your partner");
        switch(what)
        {
            case "waiting":return ru?"Ждём "+n+": дверь откроется, когда он закончит":"Waiting for "+n+": the door opens when they are done";
            case "partner-downed":return ru?(named?n:"Напарник")+" пал: дверь открыта":(named?n:"Your partner")+" is down: the door is open";
            case "partner-left-level":return ru?(named?n:"Напарник")+" покинул уровень: дверь открыта":(named?n:"Your partner")+" left the level: the door is open";
            case "partner-disconnected":return ru?"Напарник отключился: дверь открыта":"Your partner disconnected: the door is open";
            default:return null;
        }
    }

    // ---- fault gate (CoopPackRuntime.PartGate thresholds) ----
    public sealed class Gate
    {
        int consecutive;long total;public bool Tripped;public long Faults=>total;
        public void Ok(){consecutive=0;}
        public bool Fail(){total++;if(++consecutive>=MaxConsecutiveFaults||total>=MaxTotalFaults)Tripped=true;return Tripped;}
    }
}
