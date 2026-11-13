using System;
using System.Globalization;
using System.Text;

// Pure rules of CoopPartyHud (Alpha2 unit U16, OWNER-DECISIONS "Partner HUD rework", layout B). No game types: compiled
// alone with checks\CoopPartyHudChecks.cs by Test-Managed.ps1, and into the part.
//
// Layout B (one panel per remote player, panel-local units; the base plate it replaces is 300 x 42 + a 3 px shadow,
// MultiplayerUI.DrawPartyChip IL:87159):
//   +---------------------------------------------------------------+
//   | Nickname (full, partner's lobby colour)            7   4   3   |   top row: Brutality red, Tactics purple, Survival green
//   | [==========HP bar======= 245 / 310 ====================]       |   bottom row: "current / max", never a percent
//   +---------------------------------------------------------------+
// A long name first gets a slightly smaller text scale (0.8 down to the floor 0.65, at most 19% smaller); only if it still
// does not fit the panel widens (up to MaxWidth). The name is never trimmed. Last resort, only when even MaxWidth at the
// floor cannot hold the name next to the stats: the stats are hidden for that panel so the full name stays inside it.
//
// Wire (new tag CPHUD1|, in-band capability as CPTG1|: a peer is capable while its CPHUD1 lines arrive):
//   CPHUD1|S|<senderId>|<brutality>|<tactic>|<survival>      own hero's stat levels; on change (at most every 250 ms)
//                                                           and as a heartbeat every 2 s while in a run
// Without a CPHUD1 line from the partner for CapabilityTtlMs the stats are hidden; nickname and HP still work.
public static class CoopPartyHudRules
{
    // ---------------- wire ----------------
    public const string Tag="CPHUD1|";
    public const int MaxTier=999,MaxLineLength=64,InboxCap=32,MaxSenderId=64;
    public const long CapabilityTtlMs=10000,HeartbeatMs=2000,MinSendMs=250,SampleMs=200;

    public readonly struct Stats:IEquatable<Stats>
    {
        public readonly int Brutality,Tactic,Survival;
        public Stats(int brutality,int tactic,int survival){Brutality=brutality;Tactic=tactic;Survival=survival;}
        public bool Equals(Stats o)=>Brutality==o.Brutality&&Tactic==o.Tactic&&Survival==o.Survival;
        public override bool Equals(object o)=>o is Stats s&&Equals(s);
        public override int GetHashCode()=>(Brutality*1000+Tactic)*1000+Survival;
        public int this[int i]=>i==0?Brutality:i==1?Tactic:Survival;
        public override string ToString()=>Brutality.ToString(CultureInfo.InvariantCulture)+"/"+Tactic.ToString(CultureInfo.InvariantCulture)+"/"+Survival.ToString(CultureInfo.InvariantCulture);
    }
    public readonly struct Message
    {
        public readonly int Sender;public readonly Stats Stats;
        public Message(int sender,Stats stats){Sender=sender;Stats=stats;}
    }
    public static int ClampTier(int v)=>v<0?0:v>MaxTier?MaxTier:v;
    public static Stats Clamp(int b,int t,int s)=>new Stats(ClampTier(b),ClampTier(t),ClampTier(s));
    static string I(int v)=>v.ToString(CultureInfo.InvariantCulture);
    public static string Encode(int sender,Stats s)=>Tag+"S|"+I(sender)+"|"+I(ClampTier(s.Brutality))+"|"+I(ClampTier(s.Tactic))+"|"+I(ClampTier(s.Survival))+"\n";
    // Canonical non-negative decimal (no sign, no leading zero except "0", no white space), within [min,max].
    static bool Number(string s,int min,int max,out int v)
    {
        v=0;
        if(string.IsNullOrEmpty(s)||s.Length>4)return false;
        for(int i=0;i<s.Length;i++)if(s[i]<'0'||s[i]>'9')return false;
        if(s.Length>1&&s[0]=='0')return false;
        if(!int.TryParse(s,NumberStyles.None,CultureInfo.InvariantCulture,out v))return false;
        return v>=min&&v<=max;
    }
    public static bool TryParse(string line,out Message m)
    {
        m=default;
        if(line==null||line.Length>MaxLineLength||!line.StartsWith(Tag,StringComparison.Ordinal))return false;
        var body=line;
        if(body.EndsWith("\n",StringComparison.Ordinal))body=body.Substring(0,body.Length-1);
        if(body.EndsWith("\r",StringComparison.Ordinal))body=body.Substring(0,body.Length-1);
        var p=body.Split('|');
        if(p.Length!=6||p[1]!="S")return false;
        if(!Number(p[2],1,MaxSenderId,out var sender))return false;
        if(!Number(p[3],0,MaxTier,out var b)||!Number(p[4],0,MaxTier,out var t)||!Number(p[5],0,MaxTier,out var s))return false;
        m=new Message(sender,new Stats(b,t,s));return true;
    }
    public static bool Capable(long lastSeenMs,long nowMs)=>lastSeenMs>0&&nowMs>=lastSeenMs&&nowMs-lastSeenMs<=CapabilityTtlMs;
    public static bool Due(long lastMs,long nowMs,long periodMs)=>lastMs==0||nowMs<lastMs||nowMs-lastMs>=periodMs;
    // A changed value goes out at most every MinSendMs; an unchanged one as a heartbeat every HeartbeatMs.
    public static bool ShouldSend(bool changed,long lastSentMs,long nowMs)=>Due(lastSentMs,nowMs,changed?MinSendMs:HeartbeatMs);
    // Stats are shown only when the partner announced them (capability) and the values are known.
    public static bool ShowStats(bool partEnabled,bool capable,bool known)=>partEnabled&&capable&&known;
    // Per panel (CoopPartyHudPatch.Label): the panel of a base slot shows stats only for a resolved partner net id whose
    // stats are known and whose CPHUD1 lines still arrive (capability window).
    public static bool SlotShowsStats(int remoteId,bool peerKnown,long peerSeenMs,long nowMs)=>remoteId>0&&ShowStats(true,peerKnown&&Capable(peerSeenMs,nowMs),peerKnown);
    // Per frame tick (CoopPartyHudPatch.AfterFrame): send the own stats only while sending is on, a remote is connected and
    // the own net id is known, on the ShouldSend schedule.
    public static bool SendNow(bool sendOff,bool hasRemote,int ownId,bool changed,long lastSentMs,long nowMs)=>!sendOff&&hasRemote&&ownId>0&&ShouldSend(changed,lastSentMs,nowMs);
    // Receive gate (CoopPartyHudWire.BeforeHandleLine). Host: the transport sender must equal the payload sender, must not
    // be the host itself and must be a completed connection. Client: its own id must be known and the line not its own.
    public static bool AcceptLine(bool isHost,int ownId,int? transportSender,int payloadSender,bool senderCompleted)=>
        isHost?transportSender.HasValue&&transportSender.Value==payloadSender&&transportSender.Value!=ownId&&senderCompleted
              :ownId>0&&payloadSender!=ownId;

    // ---------------- slot <-> remote net id (mirror of ModEntry.TryGetClientIndex, IL:36986) ----------------
    public static bool SlotOf(int localId,int remoteId,out int slot)
    {
        slot=-1;
        if(localId<=0||remoteId<=0||remoteId==localId)return false;
        slot=remoteId>localId?remoteId-2:remoteId-1;
        return slot>=0;
    }
    public static int RemoteIdForSlot(int localId,int slot)
    {
        if(localId<=0||slot<0)return 0;
        int id=slot+1;
        if(id>=localId)id=slot+2;
        return id;
    }

    // ---------------- HP text and bar ----------------
    public static int SafeMax(int maxLife)=>maxLife<0?0:maxLife;
    public static int SafeLife(int life,int maxLife){int m=SafeMax(maxLife);return life<0?0:life>m?m:life;}
    // "current / max", e.g. "245 / 310": both clamped (no negative, current never above max); never a percent.
    public static string HpText(int life,int maxLife)=>I(SafeLife(life,maxLife))+" / "+I(SafeMax(maxLife));
    // Same rounding and thresholds as the base (UpdateLifeBar IL:87020, DrawPartyChip IL:87159).
    public static int Percent(int life,int maxLife){int m=SafeMax(maxLife);return m<=0?0:(int)Math.Round(SafeLife(life,maxLife)*100.0/m);}
    public static double FillWidth(double barWidth,int life,int maxLife){int m=SafeMax(maxLife);return m<=0||barWidth<=0?0:barWidth*SafeLife(life,maxLife)/m;}
    public static int FillColor(int percent)=>percent<=25?0xC94040:percent<=50?0xD89036:0x4CBB5E;
    public static int FillHighlight(int percent)=>percent<=25?0xE87B6E:percent<=50?0xF0BC6E:0x7FDD82;
    public static int FillShade(int percent)=>percent<=25?0x8C2A2A:percent<=50?0x9C6420:0x2E8F45;
    public static int HpTextColor(int percent)=>percent<=25?0xFF9B8A:percent<=50?0xFFE0A6:0xF4F8FF;
    public const int BarSegments=6;

    // ---------------- colours ----------------
    // The game's own stat colours: Options.getTierColor(tierId) (hlboot f@5791, Options.hx:1275-1296) with the tier ids
    // "Brutality", "Tactic", "Survival" (Options.hx:1279; the override colour when the player set one, else the data colour).
    // Fallbacks = the game's default override colours (Options.hx:340 f@5802 and :575-579 f@5796).
    public static readonly string[] TierIds={"Brutality","Tactic","Survival"};
    public const int BrutalityFallback=0xF26C6C,TacticFallback=0xB886F7,SurvivalFallback=0x36F087;
    public static int StatFallback(int index)=>index==0?BrutalityFallback:index==1?TacticFallback:SurvivalFallback;
    public static int StatColor(int index,bool gameKnown,int gameColor)
    {
        if(index<0||index>2)throw new ArgumentOutOfRangeException(nameof(index));
        if(!gameKnown)return StatFallback(index);
        int rgb=gameColor&0xFFFFFF;
        return rgb==0?StatFallback(index):rgb;           // black would vanish on the navy panel: never draw it
    }
    public const int NameGold=0xF2D98C;                 // the base nameText colour (initkingLife IL_018C 15915404)
    // Lobby colour of the partner (CoopLobby LobbyColorState.ColorOf + LobbyPalette.Rgb), else the gold.
    public static int NameColor(bool lobbyKnown,int paletteIndex,int paletteCount,int rgb)=>lobbyKnown&&paletteIndex>=0&&paletteIndex<paletteCount&&(rgb&0xFFFFFF)!=0?rgb&0xFFFFFF:NameGold;

    // ---------------- name ----------------
    // The full nickname: trimmed, line breaks and other control characters become spaces, "Guest" when empty. No length
    // limit and no ellipsis (the base FitDisplayName cuts at 9 characters, IL:86962).
    public static string Name(string displayName)
    {
        if(string.IsNullOrWhiteSpace(displayName))return "Guest";
        var sb=new StringBuilder(displayName.Length);
        foreach(var c in displayName)sb.Append(char.IsControl(c)?' ':c);
        var s=sb.ToString().Trim();
        return s.Length==0?"Guest":s;
    }
    // The text handed to ui.Text.set_text: the plain name, exactly as the base does. ui.Text.set_text (hlboot f@3411,
    // ui/Text.hx:249-269) already passes every text part that matches its HTML regex "[<>&]" (ui.$Text.HTML, global 20437,
    // ui/Text.hx:228) through StringTools.htmlEscape before h2d.HtmlText parses it, so '<', '>' and '&' show as typed.
    // Escaping here as well would escape twice ("&lt;" would show literally). Evidence: evidence\native-ui.Text.set_text.txt.
    public static string UiText(string name)=>name;

    // ---------------- layout ----------------
    public const double BaseWidth=300,MaxWidth=480,PanelHeight=56,Shadow=3;
    public const double Pad=12,RowTop=8,RowBottom=29,BarX=12,BarY=33,BarH=12,BarShell=2;
    public const double StatGap=8,NameGap=10;
    // Floor 0.65: "a slightly smaller font" (owner) = at most 19% below the base 0.8; widening comes next.
    public const double NameScale=0.8,NameScaleFloor=0.65,NameScaleStep=0.05,StatScale=0.7,HpScale=0.6;
    public static double[] NameScales()
    {
        int n=(int)Math.Round((NameScale-NameScaleFloor)/NameScaleStep)+1;
        var r=new double[n];
        for(int i=0;i<n;i++)r[i]=Math.Round(NameScale-i*NameScaleStep,4);
        return r;
    }
    public static double StatsWidth(double w0,double w1,double w2,bool visible)=>visible?Math.Max(0,w0)+Math.Max(0,w1)+Math.Max(0,w2)+2*StatGap:0;
    // Width the name may use in a panel of the given width.
    public static double NameRoom(double panelWidth,double statsWidth)=>panelWidth-2*Pad-(statsWidth>0?statsWidth+NameGap:0);
    public static double BarWidth(double panelWidth)=>panelWidth-2*BarX;

    public struct Fit
    {
        public double Scale,Width,NameWidth,Room;public int Tries;public bool Widened,Overflow,StatsDropped;
    }
    // Panel width that gives the name w px of room next to stats of the given width (whole pixels, at least BaseWidth).
    public static double WidthFor(double nameWidth,double statsWidth)=>Math.Max(BaseWidth,Math.Ceiling(BaseWidth+(nameWidth-NameRoom(BaseWidth,statsWidth))));
    // measure(scale) -> rendered name width at that scale (the game sets the text's customScale and measures). The first
    // scale (from 0.8 down) at which the name fits the base width wins; at the floor the panel widens just enough, up to
    // MaxWidth. Last resort, only when MaxWidth at the floor is not enough next to the stats: the stats are dropped for this
    // panel (StatsDropped) and the panel is as wide as the name alone needs. Overflow only when even MaxWidth without stats
    // at the floor is not enough (far beyond Steam's 32 characters).
    public static Fit FitName(Func<double,double> measure,double statsWidth)
    {
        var f=new Fit();var scales=NameScales();
        double room=NameRoom(BaseWidth,statsWidth);
        for(int i=0;i<scales.Length;i++)
        {
            double w=measure(scales[i]);f.Tries++;
            if(double.IsNaN(w)||w<0)w=0;
            if(w<=room){f.Scale=scales[i];f.Width=BaseWidth;f.NameWidth=w;f.Room=room;return f;}
            if(i==scales.Length-1)
            {
                f.Scale=scales[i];f.NameWidth=w;
                double need=WidthFor(w,statsWidth);
                if(need>MaxWidth&&statsWidth>0){f.StatsDropped=true;statsWidth=0;need=WidthFor(w,0);}
                f.Width=Math.Min(MaxWidth,need);f.Widened=f.Width>BaseWidth;
                f.Room=NameRoom(f.Width,statsWidth);f.Overflow=w>f.Room;
                return f;
            }
        }
        f.Scale=NameScaleFloor;f.Width=BaseWidth;f.Room=room;return f;   // unreachable: scales is never empty
    }
    // The stat texts are visible only when the partner's stats are shown and the layout did not drop them for the name.
    public static bool StatsVisible(bool statsShown,bool statsDropped)=>statsShown&&!statsDropped;
    // Relayout when the text metrics the last layout used (scaleX, scaleY, raw width and height of each text) changed, e.g.
    // after ui.Text.onResize picked another font or pixel scale (ui/Text.hx:298-394) from outside the part.
    public static bool MetricsChanged(double[] laid,double[] now)
    {
        if(laid==null||now==null||laid.Length!=now.Length)return true;
        for(int i=0;i<laid.Length;i++)
        {
            double a=laid[i],b=now[i];
            if(double.IsNaN(a)&&double.IsNaN(b))continue;
            if(double.IsNaN(a)||double.IsNaN(b)||Math.Abs(a-b)>1e-6)return true;
        }
        return false;
    }
    // Text placements inside the panel.
    public static double CenterY(double top,double bottom,double height,double fallback)=>height>0?Math.Round(top+(bottom-top-height)/2.0):fallback;
    public static double StatX(int index,double panelWidth,double w0,double w1,double w2)
    {
        double right=panelWidth-Pad;
        double x2=right-Math.Max(0,w2),x1=x2-StatGap-Math.Max(0,w1),x0=x1-StatGap-Math.Max(0,w0);
        return index==0?x0:index==1?x1:x2;
    }
    public static double HpX(double panelWidth,double textWidth)=>BarX+Math.Max(0,(BarWidth(panelWidth)-Math.Max(0,textWidth))/2.0);

    // ---------------- fault gate ----------------
    // Any fault switches the party HUD off for the session: every converted panel reverts to the base mod's plate.
    public const int MaxConsecutiveFaults=1,MaxTotalFaults=1;
    public sealed class Gate
    {
        int consecutive;long total;public bool Tripped;public long Faults=>total;
        public void Ok(){consecutive=0;}
        public bool Fail(){total++;if(++consecutive>=MaxConsecutiveFaults||total>=MaxTotalFaults)Tripped=true;return Tripped;}
    }
    // Rate-limited log keys: at most once per key per cooldown and at most MaxLogLines lines per process.
    public const int MaxLogLines=400;
    public static bool LogAllowed(bool seen,long lastMs,long nowMs,long cooldownMs,int linesSoFar)=>linesSoFar<MaxLogLines&&(!seen||nowMs<lastMs||nowMs-lastMs>=cooldownMs);
}
