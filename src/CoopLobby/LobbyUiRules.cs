using System;
using System.Collections.Generic;

// Pure decisions of the UI part (no game types, so Test-Managed.ps1 compiles this file with LobbyUiChecks.cs alone):
// layout of the approved mockup (a 640x384 stage scaled into the window), button identification, ellipsis,
// the ready animation, card assignment and small helpers. Everything is in mockup units unless it says px.
public struct UiRect
{
    public double X,Y,W,H;
    public UiRect(double x,double y,double w,double h){X=x;Y=y;W=w;H=h;}
    public double Right=>X+W;
    public double Bottom=>Y+H;
    public double CenterX=>X+W/2;
    public bool Overlaps(UiRect o)=>X<o.Right&&o.X<Right&&Y<o.Bottom&&o.Y<Bottom;
    public bool Inside(double w,double h)=>X>=-0.001&&Y>=-0.001&&Right<=w+0.001&&Bottom<=h+0.001;
    public override string ToString()=>$"({X:0.#},{Y:0.#} {W:0.#}x{H:0.#})";
}

public enum UiButtonKind{Continue,Start,Custom,Ready,Save,Invite,Back,Disconnect,Unknown}
public enum UiPose{Idle,Raising,Held,Lowering}

// Ready animation of one player (owner's choice 2026-11-10, scroll-anim verify.md section 3): turning ready plays
// carryItem frames 0..20 driven by setFrame from our own tick (one frame per 1/30 s), holds frame 20 while ready,
// turning not ready steps 20..0 and then goes back to the idle loop. Never AnimManager.reverse(): under reverse the
// cursor keeps counting up while the shown frame is mirrored (f@1189 op60-70, f@1210 op66-77), which puts the
// lobby head on the wrong bone key (REL-IL 99418-99458).
public struct UiReadyAnim
{
    public const int Top=20;                 // carryItem has 21 frames 0..20 in every hero atlas (verify.md row 12)
    public const double FrameTime=1.0/30;
    public UiPose Pose;public int Frame;public double Acc;
    // First sight of a player (or a rebuilt body): no animation, the held pose at once when already ready.
    public static UiReadyAnim Initial(bool ready)=>new UiReadyAnim{Pose=ready?UiPose.Held:UiPose.Idle,Frame=ready?Top:0};
    public void SetReady(bool ready)
    {
        if(ready){if(Pose==UiPose.Idle){Pose=UiPose.Raising;Frame=0;Acc=0;}else if(Pose==UiPose.Lowering){Pose=UiPose.Raising;Acc=0;}}
        else{if(Pose==UiPose.Held){Pose=UiPose.Lowering;Frame=Top;Acc=0;}else if(Pose==UiPose.Raising){Pose=UiPose.Lowering;Acc=0;}}
    }
    // Advances by real time; returns true when the shown frame or the pose changed.
    public bool Step(double dt)
    {
        if(Pose!=UiPose.Raising&&Pose!=UiPose.Lowering)return false;
        if(!(dt>0))return false;
        Acc+=Math.Min(dt,0.25);
        bool changed=false;
        while(Acc>=FrameTime&&(Pose==UiPose.Raising||Pose==UiPose.Lowering))
        {
            Acc-=FrameTime;changed=true;
            if(Pose==UiPose.Raising){if(++Frame>=Top){Frame=Top;Pose=UiPose.Held;Acc=0;}}
            else if(--Frame<=0){Frame=0;Pose=UiPose.Idle;Acc=0;}
        }
        return changed;
    }
    public bool Carry=>Pose!=UiPose.Idle;
}

public sealed class UiLayout
{
    public const double StageW=640,StageH=384;
    public const double CardW=180,CardH=206,CardTop=56,CardGap=40;
    public const double SwatchTop=272,SwatchSize=18,SwatchStep=26;
    public const double RowLeft=80,RowWidth=480,RowGap=15,ButtonMaxW=150,ButtonH=26,RowStep=32,BottomRowTop=332;
    public const double TitleTop=12,SubTop=35,ModeTop=304,HintTop=364,ChipTop=10,ChipH=20,ChipRight=628;
    // card internals (mockup .card children)
    public const double NickTop=160,NickPad=8,PillX=30,PillTop=180,PillH=18,TagX=8,TagY=7,DotR=5.5,DotRight=9,DotTop=9,FeetY=154,WaitTop=166,DotsTop=182;
    // text sizes in mockup px (the CSS font-size of the mockup)
    public const double SizeTitle=17,SizeSub=12,SizeTag=11,SizeNick=13,SizePill=11,SizeButton=13,SizeHint=12,SizeWait=12,SizeDots=15,SizeMode=13,SizeChip=11;
    public const double LineFactor=1.2;      // CSS line-height "normal": the text box we place is size*1.2 tall

    public double K,Ox,Oy,WindowW,WindowH;
    public UiRect[] Cards=new UiRect[2];
    public UiRect[] Swatches=new UiRect[6];
    public List<UiRect[]> Rows=new();        // top to bottom
    public UiRect Title,Sub,Hint,Mode;

    // px of a mockup x / y / length
    public double X(double mx)=>Ox+mx*K;
    public double Y(double my)=>Oy+my*K;
    public double L(double m)=>m*K;
    public UiRect Px(UiRect m)=>new UiRect(X(m.X),Y(m.Y),L(m.W),L(m.H));

    // rowCounts: button count per row, top to bottom (see Rows()).
    public static UiLayout Compute(double windowW,double windowH,IList<int> rowCounts,int localCard)
    {
        var l=new UiLayout{WindowW=Math.Max(1,windowW),WindowH=Math.Max(1,windowH)};
        l.K=Math.Min(l.WindowW/StageW,l.WindowH/StageH);
        l.Ox=(l.WindowW-StageW*l.K)/2;l.Oy=(l.WindowH-StageH*l.K)/2;
        double x0=(StageW-2*CardW-CardGap)/2;
        l.Cards[0]=new UiRect(x0,CardTop,CardW,CardH);
        l.Cards[1]=new UiRect(x0+CardW+CardGap,CardTop,CardW,CardH);
        var local=l.Cards[localCard==1?1:0];
        double sw=5*SwatchStep+SwatchSize,sx=local.X+(CardW-sw)/2;
        for(int i=0;i<6;i++)l.Swatches[i]=new UiRect(sx+i*SwatchStep,SwatchTop,SwatchSize,SwatchSize);
        int n=rowCounts?.Count??0;
        for(int r=0;r<n;r++)
        {
            double top=BottomRowTop-(n-1-r)*RowStep;
            l.Rows.Add(Row(Math.Max(0,rowCounts[r]),top));
        }
        l.Title=new UiRect(0,TitleTop,StageW,SizeTitle*LineFactor);
        l.Sub=new UiRect(0,SubTop,StageW,SizeSub*LineFactor);
        l.Mode=new UiRect(0,ModeTop,StageW,SizeMode*LineFactor);
        l.Hint=new UiRect(0,HintTop,StageW,SizeHint*LineFactor);
        return l;
    }
    public static UiRect[] Row(int count,double top)
    {
        var r=new UiRect[count];if(count==0)return r;
        double w=Math.Min(ButtonMaxW,(RowWidth-(count-1)*RowGap)/count);
        double total=count*w+(count-1)*RowGap,x=RowLeft+(RowWidth-total)/2;
        for(int i=0;i<count;i++)r[i]=new UiRect(x+i*(w+RowGap),top,w,ButtonH);
        return r;
    }
    // Hero sprite scale in px per sprite pixel: about 2 mockup px per sprite pixel (the mockup's hero crops), on
    // quarter steps so the pixel art stays even.
    public static double HeroScale(double k)=>Math.Max(1,Math.Round(k*2*4,MidpointRounding.AwayFromZero)/4);
}

public static class LobbyUiRules
{
    // Release order (IL 71686-71794 host, 71930-71976 client): host Continue, Start, Custom, Ready, Save, [Invite], Back;
    // client Ready, Save, Disconnect. Labels that the mod never localizes ("Normal Mode", "Custom Mode", "Ready: On/Off",
    // "Continue (...)": IL 80229-80238, 82811-82901, 71717) confirm the position; the others are localized by the mod
    // (e.g. "Save: Slot {0}" -> ru "Сохранение: слот {0}") and are identified by position only.
    // exitLabel: the mod's own exit label of this screen, GetText.Instance.GetString("Back") on the host / ("Disconnect")
    // on the client (IL 71778-71780, 71960-71962), passed unchanged by UiButton -> AddPendingButton (IL 74387-74402).
    // When it is known the release shape must also end with it; outside the release shape only a button with exactly
    // that label is the exit. null = not known: the release shape by position as before, no exit by label.
    public static UiButtonKind[] Classify(bool host,IList<string> labels,string exitLabel=null)
    {
        int n=labels?.Count??0;var k=new UiButtonKind[n];
        bool positional=false,exitKnown=!string.IsNullOrEmpty(exitLabel);
        if(host&&(n==6||n==7))
            positional=Starts(labels[0],"Continue (")&&labels[1]=="Normal Mode"&&labels[2]=="Custom Mode"&&Starts(labels[3],"Ready: ");
        else if(!host&&n==3)positional=Starts(labels[0],"Ready: ");
        if(positional&&exitKnown)positional=labels[n-1]==exitLabel;     // the release ends with the exit button
        if(positional)
        {
            if(host)
            {
                var order=n==7?new[]{UiButtonKind.Continue,UiButtonKind.Start,UiButtonKind.Custom,UiButtonKind.Ready,UiButtonKind.Save,UiButtonKind.Invite,UiButtonKind.Back}
                    :new[]{UiButtonKind.Continue,UiButtonKind.Start,UiButtonKind.Custom,UiButtonKind.Ready,UiButtonKind.Save,UiButtonKind.Back};
                Array.Copy(order,k,n);
            }
            else{k[0]=UiButtonKind.Ready;k[1]=UiButtonKind.Save;k[2]=UiButtonKind.Disconnect;}
            return k;
        }
        // never "the last one is the exit": a button appended after Back would get our Back label over its own action
        for(int i=0;i<n;i++)k[i]=exitKnown&&labels[i]==exitLabel?(host?UiButtonKind.Back:UiButtonKind.Disconnect):ByLabel(labels[i]);
        return k;
    }
    static UiButtonKind ByLabel(string s)
    {
        if(Starts(s,"Ready: "))return UiButtonKind.Ready;
        if(Starts(s,"Continue ("))return UiButtonKind.Continue;
        if(s=="Normal Mode")return UiButtonKind.Start;
        if(s=="Custom Mode")return UiButtonKind.Custom;
        if(Starts(s,"Save: "))return UiButtonKind.Save;
        if(s=="Invite Steam friends")return UiButtonKind.Invite;
        return UiButtonKind.Unknown;
    }
    static bool Starts(string s,string p)=>s!=null&&s.StartsWith(p,StringComparison.Ordinal);
    public static bool IsLaunch(UiButtonKind k)=>k==UiButtonKind.Continue||k==UiButtonKind.Start||k==UiButtonKind.Custom;

    // Rows top to bottom as lists of button indexes: host launch row (Continue/Start/Custom) above the action row;
    // more than 5 action buttons (never in the release) wrap into extra rows. Captured order kept inside a row.
    public static List<List<int>> Rows(bool host,IList<UiButtonKind> kinds)
    {
        var launch=new List<int>();var other=new List<int>();
        for(int i=0;i<kinds.Count;i++)(host&&IsLaunch(kinds[i])?launch:other).Add(i);
        var rows=new List<List<int>>();
        if(launch.Count>0)rows.Add(launch);
        for(int i=0;i<other.Count;i+=5)rows.Add(other.GetRange(i,Math.Min(5,other.Count-i)));
        return rows;
    }

    // Longest prefix that fits with "..." (three ASCII dots: U+2026 has no glyph in the latin fonts, language
    // verify.md row 8), by MEASURED width. Returns s when it fits, "..." or "" when nothing fits.
    public static string Ellipsize(string s,Func<string,double> width,double max)
    {
        if(string.IsNullOrEmpty(s)||width(s)<=max)return s??"";
        const string dots="...";
        int lo=0,hi=s.Length-1,best=-1;
        while(lo<=hi)
        {
            int mid=(lo+hi)/2;
            if(width(s.Substring(0,mid).TrimEnd()+dots)<=max){best=mid;lo=mid+1;}else hi=mid-1;
        }
        if(best<=0)return width(dots)<=max?dots:"";
        return s.Substring(0,best).TrimEnd()+dots;
    }
    // Scale factor that makes a measured width fit (never below minFactor; the rest is ellipsized).
    public static double FitFactor(double width,double max,double minFactor)=>width<=max||width<=0?1:Math.Max(minFactor,max/width);

    // Two cards: left = the host, right = the local client or the first other player. 0 = empty card.
    public static (int Left,int Right) PickCards(IList<(int Id,bool IsHost,bool IsYou)> players)
    {
        int left=0,right=0;
        foreach(var p in players)if(p.IsHost&&left==0)left=p.Id;
        foreach(var p in players)if(!p.IsHost&&p.IsYou){right=p.Id;break;}
        if(right==0)foreach(var p in players)if(!p.IsHost&&p.Id!=left){right=p.Id;break;}
        return(left,right);
    }
    public static string RoleKey(bool isHost,bool isYou)=>isHost?(isYou?"hostYou":"host"):(isYou?"you":"player2");
    // Empty left card = the client still waits for the host's lines; empty right card = waiting for the partner.
    public static string EmptyKey(int card)=>card==0?"connecting":"waiting";
    public static int Dots(double seconds)=>seconds<0?0:(int)Math.Floor(seconds/0.45)%4;
    public static string DotsText(int n)=>n<=0?"":n==1?".":n==2?". .":". . .";

    // Labels/help of the action bar: LobbyText key or null (= keep the captured original text).
    public static string LabelKey(UiButtonKind k)=>k switch
    {
        UiButtonKind.Continue=>"continue",UiButtonKind.Start=>"newGame",UiButtonKind.Custom=>"customMode",UiButtonKind.Ready=>"ready",
        UiButtonKind.Save=>"slot",UiButtonKind.Invite=>"invite",UiButtonKind.Back=>"back",UiButtonKind.Disconnect=>"disconnect",_=>null
    };
    public static string HelpKey(UiButtonKind k)=>k switch
    {
        UiButtonKind.Continue=>"continueHelp",UiButtonKind.Start=>"normalMode",UiButtonKind.Ready=>"toggleReady",UiButtonKind.Save=>"saveSlotHelp",
        UiButtonKind.Back=>"backHelp",UiButtonKind.Disconnect=>"disconnectHelp",_=>null    // Custom, Invite: no key yet (report)
    };
    // "Save: Slot 3" or its translation "Сохранение: слот 3" -> 3 (last number), -1 when none.
    public static int SlotNumber(string label)
    {
        if(string.IsNullOrEmpty(label))return -1;
        int end=label.Length-1;while(end>=0&&!char.IsDigit(label[end]))end--;
        if(end<0)return -1;
        int start=end;while(start>0&&char.IsDigit(label[start-1]))start--;
        return int.TryParse(label.Substring(start,end-start+1),out var n)&&n>=0&&n<1000?n:-1;
    }
    // Client info line "Selected mode: <GetPendingLaunchSummaryLabel>" (IL 71908-71915: not localized) -> the mode label.
    public static string SelectedMode(string info)
    {
        const string p="Selected mode: ";
        return info!=null&&info.StartsWith(p,StringComparison.Ordinal)?info.Substring(p.Length).Trim():null;
    }
    // GetPendingLaunchSummaryLabel values (IL 80239-80290, 82811-82817) -> how to say them in the game language:
    // "normalMode"/"customMode" (LobbyText keys) or a game msgid (prefixed "msgid:") with the whole-label translation
    // (language verify.md missed item 2). null = unknown, show as is.
    public static string ModeText(string label)=>label switch
    {
        "Normal Mode"=>"normalMode","Custom Mode"=>"customMode",
        "Continue (Normal Mode)"=>"msgid:Continuer (mode normal)","Continue (Custom Mode)"=>"msgid:Continuer (mode personnalisé)",_=>null
    };
    public static string Hint(string hover,string notice,string state)=>!string.IsNullOrEmpty(hover)?hover:!string.IsNullOrEmpty(notice)?notice:state??"";
    public static int Mix(int a,int b,double t)
    {
        t=Math.Clamp(t,0,1);
        int C(int s)=>(int)Math.Round(((a>>s)&255)*t+((b>>s)&255)*(1-t));
        return (C(16)<<16)|(C(8)<<8)|C(0);
    }
    // Centred squares along the -45 degree diagonal of a circle of radius r (a crossed-out swatch drawn with drawRect only).
    public static List<UiRect> CrossSquares(double cx,double cy,double r,double thick)
    {
        var list=new List<UiRect>();
        double half=r+thick;int n=Math.Max(3,(int)Math.Ceiling(2*half/thick));    // one square per thickness along the line
        for(int i=0;i<=n;i++)
        {
            double t=-half+2*half*i/n;
            list.Add(new UiRect(cx+t*Math.Sqrt(0.5)-thick/2,cy-t*Math.Sqrt(0.5)-thick/2,thick,thick));
        }
        return list;
    }

    // A list of count entries shown perPage at a time: the wanted page clamped into range; never an empty page while
    // there are entries (a shrinking friend list pulls the page back).
    public static (int Page,int Pages,int First,int Shown) Paginate(int count,int perPage,int page)
    {
        count=Math.Max(0,count);perPage=Math.Max(1,perPage);
        int pages=Math.Max(1,(count+perPage-1)/perPage);
        int p=Math.Clamp(page,0,pages-1);
        int first=p*perPage;
        return(p,pages,first,Math.Max(0,Math.Min(perPage,count-first)));
    }
    // A filled triangle made of columns (h2d.Graphics here has drawRect / drawCircle only): pointing right or left,
    // inside the box (x,y,w,h), tip at the middle of the far side.
    public static List<UiRect> Arrow(double x,double y,double w,double h,bool right,int columns)
    {
        var list=new List<UiRect>();
        int n=Math.Max(2,columns);double cw=w/n,cy=y+h/2;
        for(int j=0;j<n;j++)
        {
            double t=(double)j/n;                      // 0 at the base, towards the tip
            double ch=Math.Max(cw*0.5,h*(1-t));
            double cx=right?x+j*cw:x+w-(j+1)*cw;
            list.Add(new UiRect(cx,cy-ch/2,cw,ch));
        }
        return list;
    }
}

// ---- the other Multiplayer pages (hub, host transport, Steam host mode, join transport, LAN setup) ------------------
// Same stage, colours and text rules as the lobby. Each page is recognised from the captured PendingButtons /
// PendingInfos against the strings the mod itself builds for that page in the current language (UiPageExpect is
// filled by the native side from GetText.Instance.GetString of the release literals, LobbySession IL 76032-77019).
// Anything that does not match exactly is not drawn: the original page stays.
public enum UiPageKind{None,Hub,HostTransport,SteamHostMode,JoinTransport,LanSetup}
public enum UiItemRole{Choice,Field,Friend,Primary,Back}

public sealed class UiPageExpect
{
    public UiPageKind Kind;public bool Host;        // LanSetup: the host variant (role == NetRole.Host)
    public string[] Infos;                          // [title, accepted sub 0, accepted sub 1 ...]
    public string[] Choices;                        // exact labels of the leading choice buttons
    public string[] Tail;                           // JoinTransport: exact labels after the friend run (Open Steam friends, code join)
    public string FriendSuffix;                     // JoinTransport: " - " + Localize("Join") (IL 76509-76513)
    public string[] FieldPrefixes;                  // LanSetup: GetString("Username: "), ("IP: "), ("Port: ")
    public string Primary;                          // LanSetup: GetString("Host") or ("Join")
    public string Back;                             // GetString("Back")
}
public sealed class UiPageItem{public UiItemRole Role;public int Button,Slot;public string Value;}
public sealed class UiPagePlan
{
    public UiPageKind Kind;public bool Host;public int SubVariant,Friends;
    public readonly List<UiPageItem> Items=new();
    public IEnumerable<UiPageItem> Of(UiItemRole r){foreach(var i in Items)if(i.Role==r)yield return i;}
}

// Page geometry in mockup units on the lobby's 640x384 stage (UiLayout): header and hint line exactly where the lobby
// has them, the exit (and the LAN page's primary action) in the lobby's bottom row, the page's own content between.
public sealed class UiPageLayout
{
    public const double BigW=230,BigH=100,BigGap=30,BigTop=136;          // two choice tiles (hub, host transport, Steam mode)
    public const double SmallW=150,SmallH=60,SmallGap=15,SmallTop=62;    // three choice tiles (join page)
    public const double ListW=360,ListTop=140,RowH=28,RowStep=34,PerPage=4,PagerTop=284,PagerH=22,PagerBtnW=26,PagerLabelW=80;
    public const double FieldW=360,FieldTop=62,FieldStep=58,CaptionH=14,WellGap=16,WellH=28;
    public const double SizeBig=16,SizeBigHelp=11,SizeSmall=13,SizeSmallHelp=10,SizeRow=13,SizeCaption=11,SizeValue=13,SizePager=12,SizeEmpty=12;

    public double K,Ox,Oy,WindowW,WindowH;
    public UiRect Title,Sub,Hint,Empty,Prev,Next,PageLabel;
    public readonly List<UiRect> Choices=new(),Captions=new(),Wells=new(),Rows=new();
    public UiRect[] Bottom=new UiRect[0];
    public bool Pager;public int Page,Pages,First,Shown;

    public double X(double mx)=>Ox+mx*K;
    public double Y(double my)=>Oy+my*K;
    public double L(double m)=>m*K;
    public UiRect Px(UiRect m)=>new UiRect(X(m.X),Y(m.Y),L(m.W),L(m.H));
    public IEnumerable<UiRect> All()
    {
        yield return Title;yield return Sub;yield return Hint;
        foreach(var r in Choices)yield return r;foreach(var r in Captions)yield return r;foreach(var r in Wells)yield return r;
        foreach(var r in Rows)yield return r;foreach(var r in Bottom)yield return r;
        if(Pager){yield return Prev;yield return Next;yield return PageLabel;}
        if(Empty.W>0)yield return Empty;
    }

    public static int ChoiceCount(UiPageKind k)=>k==UiPageKind.JoinTransport?3:k==UiPageKind.LanSetup||k==UiPageKind.None?0:2;
    public static int BottomCount(UiPageKind k)=>k==UiPageKind.LanSetup?2:k==UiPageKind.None?0:1;

    // friends: friend lobbies on the join page; page: wanted list page (clamped)
    public static UiPageLayout Compute(double windowW,double windowH,UiPageKind kind,int friends,int page)
    {
        var l=new UiPageLayout{WindowW=Math.Max(1,windowW),WindowH=Math.Max(1,windowH)};
        l.K=Math.Min(l.WindowW/UiLayout.StageW,l.WindowH/UiLayout.StageH);
        l.Ox=(l.WindowW-UiLayout.StageW*l.K)/2;l.Oy=(l.WindowH-UiLayout.StageH*l.K)/2;
        l.Title=new UiRect(0,UiLayout.TitleTop,UiLayout.StageW,UiLayout.SizeTitle*UiLayout.LineFactor);
        l.Sub=new UiRect(0,UiLayout.SubTop,UiLayout.StageW,UiLayout.SizeSub*UiLayout.LineFactor);
        l.Hint=new UiRect(0,UiLayout.HintTop,UiLayout.StageW,UiLayout.SizeHint*UiLayout.LineFactor);
        int n=ChoiceCount(kind);
        if(n==2){double x0=(UiLayout.StageW-2*BigW-BigGap)/2;for(int i=0;i<2;i++)l.Choices.Add(new UiRect(x0+i*(BigW+BigGap),BigTop,BigW,BigH));}
        else if(n==3){double x0=(UiLayout.StageW-3*SmallW-2*SmallGap)/2;for(int i=0;i<3;i++)l.Choices.Add(new UiRect(x0+i*(SmallW+SmallGap),SmallTop,SmallW,SmallH));}
        double cx=(UiLayout.StageW-ListW)/2;
        if(kind==UiPageKind.LanSetup)for(int i=0;i<3;i++)
        {
            double top=FieldTop+i*FieldStep;
            l.Captions.Add(new UiRect(cx,top,FieldW,CaptionH));
            l.Wells.Add(new UiRect(cx,top+WellGap,FieldW,WellH));
        }
        if(kind==UiPageKind.JoinTransport)
        {
            var pg=LobbyUiRules.Paginate(friends,(int)PerPage,page);
            l.Page=pg.Page;l.Pages=pg.Pages;l.First=pg.First;l.Shown=pg.Shown;
            for(int i=0;i<pg.Shown;i++)l.Rows.Add(new UiRect(cx,ListTop+i*RowStep,ListW,RowH));
            if(friends<=0)l.Empty=new UiRect(cx,ListTop,ListW,(PerPage-1)*RowStep+RowH);
            l.Pager=pg.Pages>1;
            if(l.Pager)
            {
                double mid=UiLayout.StageW/2;
                l.PageLabel=new UiRect(mid-PagerLabelW/2,PagerTop,PagerLabelW,PagerH);
                l.Prev=new UiRect(l.PageLabel.X-PagerBtnW,PagerTop,PagerBtnW,PagerH);
                l.Next=new UiRect(l.PageLabel.Right,PagerTop,PagerBtnW,PagerH);
            }
        }
        l.Bottom=UiLayout.Row(BottomCount(kind),UiLayout.BottomRowTop);
        return l;
    }
}

public static class LobbyUiPageRules
{
    static bool Eq(string a,string b)=>a!=null&&b!=null&&string.Equals(a,b,StringComparison.Ordinal);

    // The captured page against what the mod builds for it (release order, IL 76032-77019). null = not that page.
    public static UiPagePlan Match(UiPageExpect e,IList<string> labels,IList<bool> fields,IList<string> infos)
    {
        if(e==null||labels==null||fields==null||infos==null||labels.Count!=fields.Count||e.Back==null)return null;
        if(e.Infos==null||e.Infos.Length<2||infos.Count!=2||!Eq(infos[0],e.Infos[0]))return null;
        int sub=-1;for(int i=1;i<e.Infos.Length;i++)if(Eq(infos[1],e.Infos[i])){sub=i-1;break;}
        if(sub<0)return null;
        var p=new UiPagePlan{Kind=e.Kind,Host=e.Host,SubVariant=sub};
        int n=labels.Count;
        void Add(UiItemRole r,int b,int slot,string v)=>p.Items.Add(new UiPageItem{Role=r,Button=b,Slot=slot,Value=v});
        switch(e.Kind)
        {
            case UiPageKind.Hub:case UiPageKind.HostTransport:case UiPageKind.SteamHostMode:
                if(n!=3||e.Choices==null||e.Choices.Length!=2)return null;
                for(int i=0;i<2;i++){if(!Eq(labels[i],e.Choices[i])||fields[i])return null;Add(UiItemRole.Choice,i,i,null);}
                if(!Eq(labels[2],e.Back)||fields[2])return null;
                Add(UiItemRole.Back,2,0,null);
                return p;
            case UiPageKind.JoinTransport:
            {
                // Lan join, one "{name} - Join" per friend lobby, Open Steam friends, code join, Back (IL 76460-76582)
                if(n<4||e.Choices==null||e.Choices.Length!=1||e.Tail==null||e.Tail.Length!=2||string.IsNullOrEmpty(e.FriendSuffix))return null;
                for(int i=0;i<n;i++)if(fields[i])return null;
                if(!Eq(labels[0],e.Choices[0]))return null;
                Add(UiItemRole.Choice,0,0,null);
                for(int i=1;i<n-3;i++)
                {
                    var s=labels[i];
                    if(s==null||s.Length<=e.FriendSuffix.Length||!s.EndsWith(e.FriendSuffix,StringComparison.Ordinal))return null;
                    Add(UiItemRole.Friend,i,p.Friends++,s.Substring(0,s.Length-e.FriendSuffix.Length));
                }
                if(!Eq(labels[n-3],e.Tail[0])||!Eq(labels[n-2],e.Tail[1])||!Eq(labels[n-1],e.Back))return null;
                Add(UiItemRole.Choice,n-3,1,null);Add(UiItemRole.Choice,n-2,2,null);Add(UiItemRole.Back,n-1,0,null);
                // the info line is chosen by the same friend count (IL 76445-76459): variant 0 = friends, 1 = none
                if((p.Friends>0)!=(sub==0))return null;
                return p;
            }
            case UiPageKind.LanSetup:
                // Username / IP / Port field blocks, Host or Join, Back (IL 76832-76971)
                if(n!=5||e.FieldPrefixes==null||e.FieldPrefixes.Length!=3||e.Primary==null)return null;
                for(int i=0;i<3;i++)
                {
                    var pre=e.FieldPrefixes[i];
                    if(!fields[i]||string.IsNullOrEmpty(pre)||labels[i]==null||!labels[i].StartsWith(pre,StringComparison.Ordinal))return null;
                    Add(UiItemRole.Field,i,i,labels[i].Substring(pre.Length));
                }
                if(fields[3]||!Eq(labels[3],e.Primary)||fields[4]||!Eq(labels[4],e.Back))return null;
                Add(UiItemRole.Primary,3,0,null);Add(UiItemRole.Back,4,0,null);
                return p;
        }
        return null;
    }

    // LobbyText keys of one page: title, sub (by variant), and per role/slot label + help.
    public static string TitleKey(UiPageKind k,bool host)=>k switch
    {
        UiPageKind.Hub=>"mpTitle",UiPageKind.HostTransport=>"hostOptions",UiPageKind.SteamHostMode=>"steamVis",UiPageKind.JoinTransport=>"joinTitle",
        UiPageKind.LanSetup=>host?"lanHostTitle":"lanJoinTitle",_=>null
    };
    public static string SubKey(UiPageKind k,bool host,int variant)=>k switch
    {
        UiPageKind.Hub=>"mpSub",UiPageKind.HostTransport=>"hostOptionsSub",UiPageKind.SteamHostMode=>"steamVisSub",
        UiPageKind.JoinTransport=>variant==0?"joinSubFriends":"joinSubNone",UiPageKind.LanSetup=>host?"lanHostSub":"lanJoinSub",_=>null
    };
    static readonly string[][] ChoiceKeys=
    {
        null,
        new[]{"hostGame","joinGame"},
        new[]{"lanHost","steamHost"},
        new[]{"steamFriends","steamPublic"},
        new[]{"lanJoin","steamOverlay","codeJoin"},
        null,
    };
    public static string LabelKey(UiPageKind k,bool host,UiItemRole r,int slot)
    {
        switch(r)
        {
            case UiItemRole.Choice:{var a=ChoiceKeys[(int)k];return a!=null&&slot>=0&&slot<a.Length?a[slot]:null;}
            case UiItemRole.Field:return slot==0?"fieldUser":slot==1?"fieldIp":slot==2?"fieldPort":null;
            case UiItemRole.Friend:return "joinFriend";
            case UiItemRole.Primary:return host?"startHost":"connect";
            case UiItemRole.Back:return "back";
        }
        return null;
    }
    public static string HelpKey(UiPageKind k,bool host,UiItemRole r,int slot)
    {
        if(r==UiItemRole.Back)return k switch
        {
            UiPageKind.Hub=>"backToTitle",UiPageKind.HostTransport=>"backToMp",UiPageKind.JoinTransport=>"backToMp",
            UiPageKind.SteamHostMode=>"backToHost",UiPageKind.LanSetup=>host?"backToHost":"backToJoin",_=>null
        };
        if(r==UiItemRole.Field)return slot==0?"editUser":slot==1?"editIp":slot==2?"editPort":null;
        if(r==UiItemRole.Friend)return "joinFriendHelp";
        if(r==UiItemRole.Primary)return host?"startHostHelp":"connectHelp";
        var l=LabelKey(k,host,r,slot);return l==null?null:l+"Help";
    }
    // Our own wording (LobbyText en / ru) in English and Russian; in any other language the mod's own captured string
    // (what the original page shows in that language). Game words (Back) always come from the game's own table.
    public static bool UseOwnText(string lang)=>lang=="ru"||lang=="en";
    public static string Pick(bool own,string ours,string captured)=>own||string.IsNullOrWhiteSpace(captured)?ours:Plain(captured);

    // Glyph-safe text: no em / en dash, ellipsis, NBSP, bullets or guillemets (missing glyphs, brief C10), no control
    // characters and no ui.Text markup braces.
    public static string Plain(string s)
    {
        if(string.IsNullOrEmpty(s))return s??"";
        var b=new System.Text.StringBuilder(s.Length);
        foreach(char c in s)
        {
            switch(c)
            {
                case '—':case '–':case '‒':case '―':case '•':case '·':b.Append('-');continue;
                case '…':b.Append("...");continue;
                case ' ':case ' ':case ' ':b.Append(' ');continue;
                case '«':case '»':case '“':case '”':case '„':b.Append('"');continue;
                case '‘':case '’':b.Append('\'');continue;
            }
            if(char.IsControl(c)||c=='{'||c=='}'||char.IsSurrogate(c))continue;
            b.Append(c);
        }
        return b.ToString().Trim();
    }
    // Field values and Steam persona names: shown as typed (no glyph replacement, nick charset at draw time), only
    // control characters, surrogates and markup braces dropped.
    public static string Value(string s)
    {
        if(string.IsNullOrEmpty(s))return "";
        var b=new System.Text.StringBuilder(s.Length);
        foreach(char c in s){if(char.IsControl(c)||c=='{'||c=='}'||char.IsSurrogate(c))continue;b.Append(c);}
        return b.ToString().Trim();
    }

    // Field caption: the mod's "Username: " prefix without the colon (SplitFieldCaption behaviour).
    public static string Caption(string prefix)
    {
        var s=(prefix??"").Trim();
        while(s.EndsWith(":",StringComparison.Ordinal)||s.EndsWith("：",StringComparison.Ordinal))s=s.Substring(0,s.Length-1).TrimEnd();
        return s;
    }
}
