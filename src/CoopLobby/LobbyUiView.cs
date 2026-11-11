using System;
using System.Collections.Generic;
using System.Diagnostics;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;
using dc.libs.heaps.slib;
using HaxeProxy.Runtime;
using ModCore.Utilities;

// One captured PendingButton (ConnectionUI.PendingButtons, IL 93677-93707). OnClick is the mod's own closure; it is
// replaced in place on every hidden rebuild (new closure objects each time) without redrawing the bar.
sealed class LobbyUiButton
{
    public string Label="",Help="";public bool Enabled;public int Color;public Action OnClick;
    // "Ready: On" / "Ready: Off" (IL 80229-80238) both show as our "Ready" with the state drawn from the poll, so a
    // ready toggle does not rebuild the bar
    public bool SameLook(LobbyUiButton o)=>o!=null&&(o.Label==Label||IsReady(o.Label)&&IsReady(Label))&&o.Help==Help&&o.Enabled==Enabled&&o.Color==Color;
    static bool IsReady(string s)=>s!=null&&s.StartsWith("Ready: ",StringComparison.Ordinal);
}

// Our own lobby layer: a child of ConnectionUI.root at the bottom of its layer 0 (below the hidden _panelRoot and the
// text prompt). Everything is laid out on the 640x384 mockup stage scaled into the window (UiLayout) and every text
// is measured after makeText + customScale + onResize. Persistent: the hidden original keeps rebuilding itself about
// once a second (brief C7); this layer only changes the parts whose inputs changed.
sealed class LobbyUiView
{
    internal const int BgColor=0x0D1018,CardFace=0x151A27,CardEdgeEmpty=0x2B3956,TagFace=0x24324D,TagInk=0xA9C3EF,Fg=0xE8E6DF,Muted=0x8D93A3,
        Gold=0xE0B43A,GoldInk=0x2A1F05,GoldText=0xF3D27A,PillOff=0x232836,TitleColor=0xF2C14E,Accent=0x59D5FF,HintColor=0x7B8296,
        BtnFace=0x161B28,BtnEdge=0x2F3E5E,BtnOffEdge=0x232B3D,BtnOffText=0x5F6577,ModeColor=0xB9BFCC;
    const double NoticeSeconds=3;

    sealed class Seat{public UiReadyAnim Anim;public bool Ready;}
    sealed class Card
    {
        public readonly int Index;public Card(int i){Index=i;}
        public dc.h2d.Object Root,Back,BodyRoot,Front;
        public HSprite Body;public dc.h2d.Object Head;public readonly List<object> Fx=new();
        public string Skin="",HeadId="",BodyKey,Sig;public int Id=-1,Color=-1;public bool Empty=true,CanCarry,Carry,Silhouette;
        public int LastCursor=-1,Shown=-1,DotsN=-1;public double HeadFallback;public dc.ui.Text Dots;
    }
    sealed class Btn
    {
        public LobbyUiButton M;public UiButtonKind Kind;public UiRect R;public dc.h2d.Graphics G;public dc.ui.Text T;public dc.h2d.Interactive I;
        public bool Hover;public int Drawn=-1;public string HelpText="";public HlAction<dc.hxd.Event> Over,Out,Click;
    }

    public readonly ConnectionUI Ui;
    readonly dc.h2d.Layers _root;
    readonly dc.h2d.Object _layer,_bgRoot,_headRoot,_cardRoot,_swRoot,_barRoot,_modeRoot,_hintRoot;
    readonly dc.ui.Text _probe;
    readonly Card[] _cards={new Card(0),new Card(1)};
    readonly Dictionary<int,Seat> _seats=new();
    readonly List<Btn> _btns=new();
    // Delegates handed to native Interactives stay referenced while their Interactive can still call them: one list per
    // section. A section that clears its root retires its list; retired delegates are dropped at the next Tick (P3,
    // never inside an Interactive callback), so a rebuild started from inside a callback never drops the running one.
    // Without this the views' delegates piled up for the whole lobby (21 per bar rebuild, 18 per swatch redraw).
    readonly List<HlAction<dc.hxd.Event>> _keepBg=new(),_keepHead=new(),_keepSw=new(),_keepBar=new(),_retired=new();
    UiLayout _l;int _w,_h;double _unit,_lastProbe;string _lang="";
    List<LobbyUiButton> _buttons=new();bool _hostBar;string _modeLabel,_exitLabel;
    string _headSig,_swSig,_modeSig,_hintShown,_stateKey,_stateText="";
    object _hoverOwner;string _hoverText;string _notice;double _noticeUntil;
    bool _localReady,_allReady;int _localCard=-1;string _otherName="";
    readonly double _born=Now;

    static double Now=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;

    public LobbyUiView(ConnectionUI ui)
    {
        Ui=ui;_root=ui.root??throw new InvalidOperationException("ConnectionUI has no root");
        _layer=new dc.h2d.Object(null);
        _root.addChild(_layer);
        try
        {
            _root.under(_layer);    // bottom of layer 0: below the (hidden) _panelRoot and below _promptRoot (h2d/Layers.hx under)
            _bgRoot=new dc.h2d.Object(_layer);_headRoot=new dc.h2d.Object(_layer);_cardRoot=new dc.h2d.Object(_layer);
            _swRoot=new dc.h2d.Object(_layer);_modeRoot=new dc.h2d.Object(_layer);_barRoot=new dc.h2d.Object(_layer);_hintRoot=new dc.h2d.Object(_layer);
            _probe=LobbyUiNative.MakeText("Ag",0xFFFFFF,_layer);_probe.set_visible(false);
            Rebuild();
        }
        catch{foreach(var c in _cards)DropBody(c);try{_layer.remove();}catch{}throw;}   // never leave a half-built layer attached
    }
    public bool Valid(ConnectionUI ui)=>ReferenceEquals(ui,Ui)&&ui.root!=null&&ui.root.HashlinkPointer==_root.HashlinkPointer&&_layer.parent!=null;
    public void Show(){if(!_layer.visible)_layer.set_visible(true);}
    public void HideNow(){if(_layer.visible)_layer.set_visible(false);}

    // The kept delegate lists are not cleared here: Dispose can run inside one of those delegates (Safe -> Fault ->
    // Shutdown, or a click whose action re-enters the lobby), and they go away with this view object anyway.
    public void Dispose()
    {
        foreach(var c in _cards)DropBody(c);
        try{_layer.remove();}catch{}
        _btns.Clear();
    }

    // ---- inputs ----------------------------------------------------------------------------------------------
    // exitLabel: the mod's own label of this screen's exit button (LobbyUiRules.Classify)
    public void SetButtons(List<LobbyUiButton> list,bool host,string modeLabel,string exitLabel)
    {
        bool same=list.Count==_buttons.Count&&host==_hostBar&&exitLabel==_exitLabel;
        for(int i=0;same&&i<list.Count;i++)same=list[i].SameLook(_buttons[i]);
        if(same){for(int i=0;i<list.Count;i++)_buttons[i].OnClick=list[i].OnClick;}
        else{_buttons=list;_hostBar=host;_exitLabel=exitLabel;try{BuildBar();}catch{_buttons=new List<LobbyUiButton>();throw;}}   // a failed build is retried by the next call
        if(modeLabel!=_modeLabel){_modeLabel=modeLabel;try{BuildMode();}catch{_modeLabel="\0";throw;}}
    }

    // After LobbyModel.Poll (10 Hz): seat states keyed by net id, then the two cards, header and swatches in place.
    public void Apply()
    {
        var players=LobbyModel.Players;
        var seen=new HashSet<int>();
        var pick=new List<(int,bool,bool)>(players.Count);
        foreach(var p in players)
        {
            seen.Add(p.Id);pick.Add((p.Id,p.IsHost,p.IsYou));
            // trigger only on a change seen by the poll diff, never on a rebuild (SPEC Part UI)
            if(!_seats.TryGetValue(p.Id,out var s))_seats[p.Id]=new Seat{Anim=UiReadyAnim.Initial(p.Ready),Ready=p.Ready};
            else if(s.Ready!=p.Ready){s.Ready=p.Ready;s.Anim.SetReady(p.Ready);}
            if(p.IsYou)_localReady=p.Ready;
        }
        foreach(var id in new List<int>(_seats.Keys))if(!seen.Contains(id))_seats.Remove(id);
        var (left,right)=LobbyUiRules.PickCards(pick);
        int localCard=-1;
        for(int i=0;i<2;i++)
        {
            var p=Find(i==0?left:right);
            if(p!=null&&p.IsYou)localCard=i;
            UpdateCard(_cards[i],p);
        }
        var other=Find(localCard==0?right:left);_otherName=other?.Name??"";
        if(localCard!=_localCard){_localCard=localCard;_l=Layout();_swSig=null;}
        BuildHeader();BuildSwatches();
        foreach(var b in _btns)if(b.Kind==UiButtonKind.Ready)DrawButton(b);
    }
    static LobbyPlayer Find(int id){if(id<=0)return null;foreach(var p in LobbyModel.Players)if(p.Id==id)return p;return null;}

    public void Notice(string text){if(string.IsNullOrEmpty(text))return;_notice=text;_noticeUntil=Now+NoticeSeconds;}

    // ---- per frame -------------------------------------------------------------------------------------------
    public void Tick(double dt)
    {
        _retired.Clear();
        var win=dc.hxd.Window.Class.getInstance();
        int w=win.get_width(),h=win.get_height();
        double now=Now;
        bool rebuild=w!=_w||h!=_h;
        if(!rebuild&&now-_lastProbe>0.5)
        {
            // the font step follows Main.pixelScale, which can change a few frames after the window (map C7d), and the
            // language can change in Options: re-measure twice a second and rebuild when either moved
            _lastProbe=now;
            rebuild=Math.Abs(MeasureUnit()-_unit)>_unit*0.01||LobbyText.Lang!=_lang;
        }
        if(rebuild){Rebuild();return;}
        double tmod=Ui.tmod;if(!(tmod>0))tmod=1;      // the mod's own lobby step (GetLobbyAnimDt, IL 97759-97775)
        foreach(var s in _seats.Values)s.Anim.Step(dt);
        foreach(var c in _cards)TickCard(c,dt,tmod,now);
        var n=LobbyColorState.Take();      // fresh notices only (posted within 1.5 s)
        if(n!=null)Notice(n=="taken"?LobbyText.Format("colorTaken",_otherName):n);
        if(_notice!=null&&now>_noticeUntil)_notice=null;
        string key=LobbyColorState.CanPick&&LobbyColorState.Pending>=0?"colorPending":_hostBar?(_allReady?"allReady":"notAllReady"):"waitHost";
        if(key!=_stateKey){_stateKey=key;_stateText=LobbyText.Get(key);}
        SetHint(LobbyUiRules.Hint(_hoverText,_notice,_stateText));
    }

    void TickCard(Card c,double dt,double tmod,double now)
    {
        if(c.Dots!=null)
        {
            int d=LobbyUiRules.Dots(now-_born);
            if(d!=c.DotsN){c.DotsN=d;c.Dots.set_text(LobbyUiRules.DotsText(d).AsHaxeString());Put(c.Dots,_l.L(UiLayout.CardW/2)-TW(c.Dots)/2,_l.L(UiLayout.DotsTop));}
        }
        var b=c.Body;if(b==null)return;
        UiReadyAnim a=c.Id>0&&_seats.TryGetValue(c.Id,out var s)?s.Anim:default;
        bool want=!c.Silhouette&&c.CanCarry&&a.Carry;
        if(want&&!c.Carry)
        {
            // carryItem driven by setFrame from this tick: genSpeed 0 so neither _update nor HSprite.sync moves it
            // (cpt += dt*genSpeed*speed, f@1210 op25-31); the probe's pose capture used the same calls
            var am=LobbyUiNative.PlayOnce(b.get_anim(),"carryItem");
            if(am!=null)am.genSpeed=0;
            c.Carry=true;c.Shown=-1;
        }
        else if(!want&&c.Carry){LobbyUiNative.IdleLoop(b,"idle");c.Carry=false;c.LastCursor=-1;}
        if(c.Carry){if(c.Shown!=a.Frame){b.get_anim().setFrame(a.Frame);c.Shown=a.Frame;}}
        else
        {
            // title-screen sprites do not always step on their own: same rule as TickLobbyHeadBones (IL 97694-97712)
            int cur=LobbyUiNative.AnimCursor(b);
            if(cur==c.LastCursor){var am=b.get_anim();if(am!=null){am._update(tmod);cur=LobbyUiNative.AnimCursor(b);}}
            c.LastCursor=cur;
        }
        // per frame with fallbackLocalY 0 like TickLobbyHeadBones (IL 97741-97750): keeps the creation placement when
        // a skin has no headBone key for the shown frame
        if(c.Head!=null)LobbyUiNative.PlaceHead(Ui,c.Head,b,c.Skin,0,c.HeadId);
        LobbyUiNative.TickEmitters(c.Fx,Math.Min(Math.Max(dt,0),0.1));
    }

    // ---- build -----------------------------------------------------------------------------------------------
    // Every section stores its signature only after it was drawn, so a section that threw is drawn again on the next
    // poll (and keeps faulting until the part trips) instead of staying half-drawn.
    void Rebuild(){try{RebuildAll();}catch{_w=-1;throw;}}
    void RebuildAll()
    {
        var win=dc.hxd.Window.Class.getInstance();
        _w=win.get_width();_h=win.get_height();_lang=LobbyText.Lang;
        _unit=MeasureUnit();_lastProbe=Now;
        _l=Layout();
        _bgRoot.removeChildren();Retire(_keepBg);
        var bg=new dc.h2d.Graphics(_bgRoot);Fill(bg,0,0,_w,_h,BgColor,0.94);     // the original panel's full-window cover (IL 96262-96283)
        var block=new dc.h2d.Interactive(_w,_h,_bgRoot,null);                    // swallows clicks like the original's inter (IL 96408-96423)
        Keep(_keepBg,block,null,null,e=>{});
        _headSig=_swSig=_modeSig=_hintShown=_stateKey=null;
        foreach(var c in _cards){DropBody(c);c.Sig=null;c.BodyKey=null;c.Root=null;}
        _cardRoot.removeChildren();
        for(int i=0;i<2;i++)
        {
            var c=_cards[i];
            c.Root=new dc.h2d.Object(_cardRoot);Put(c.Root,_l.X(_l.Cards[i].X),_l.Y(_l.Cards[i].Y));
            c.Back=new dc.h2d.Object(c.Root);c.BodyRoot=new dc.h2d.Object(c.Root);c.Front=new dc.h2d.Object(c.Root);
            UpdateCard(c,Find(c.Id));
        }
        BuildHeader();BuildSwatches();BuildBar();BuildMode();
        _hintRoot.removeChildren();
    }
    UiLayout Layout()
    {
        var kinds=LobbyUiRules.Classify(_hostBar,Labels(),_exitLabel);
        var rows=LobbyUiRules.Rows(_hostBar,kinds);
        var counts=new List<int>();foreach(var r in rows)counts.Add(r.Count);
        return UiLayout.Compute(_w,_h,counts,_localCard<0?0:_localCard);
    }
    List<string> Labels(){var l=new List<string>(_buttons.Count);foreach(var b in _buttons)l.Add(b.Label);return l;}
    double MeasureUnit()
    {
        _probe.customScale=1;_probe.onResize();
        double u=TH(_probe);
        return u>0.5?u:16;
    }

    void UpdateCard(Card c,LobbyPlayer p)
    {
        if(c.Root==null)return;
        int id=p?.Id??0,color=p==null?-1:LobbyColorState.ColorOf(p.Id);
        string bodyKey=p==null?"empty":id+"|"+p.Skin+"|"+p.Head;
        c.Id=id;c.Empty=p==null;
        if(bodyKey!=c.BodyKey){BuildBody(c,p,color);c.BodyKey=bodyKey;}
        else if(color!=c.Color&&!c.Silhouette)Tint(c.Body,color);
        c.Color=color;
        string sig=p==null?"E|"+c.Index:id+"|"+p.Name+"|"+p.Ready+"|"+p.IsHost+"|"+p.IsYou+"|"+color;
        if(sig==c.Sig)return;
        c.Sig=null;
        BuildChrome(c,p,color);
        c.Sig=sig;
    }
    void BuildChrome(Card c,LobbyPlayer p,int color)
    {
        c.Back.removeChildren();c.Front.removeChildren();c.Dots=null;c.DotsN=-1;
        var g=new dc.h2d.Graphics(c.Back);
        double cw=_l.L(UiLayout.CardW),ch=_l.L(UiLayout.CardH),border=Math.Max(1,Math.Round(_l.L(2)));
        int edge=p==null?CardEdgeEmpty:LobbyUiRules.Mix(LobbyPalette.Rgb(color),CardFace,0.6);
        Frame(g,0,0,cw,ch,_l.L(8),edge,CardFace,border);
        double nickW=_l.L(UiLayout.CardW-2*UiLayout.NickPad);
        if(p==null)
        {
            var wait=Text(c.Front,LobbyText.Get(LobbyUiRules.EmptyKey(c.Index)),HintColor,UiLayout.SizeWait,nickW);
            Put(wait,cw/2-TW(wait)/2,_l.L(UiLayout.WaitTop));
            c.Dots=Text(c.Front,"",HintColor,UiLayout.SizeDots,0);
            return;
        }
        // role tag (top left) with a plate measured from its text
        var tag=Text(c.Front,LobbyText.Get(LobbyUiRules.RoleKey(p.IsHost,p.IsYou)),TagInk,UiLayout.SizeTag,cw-_l.L(UiLayout.TagX+30+12));
        double tx=_l.L(UiLayout.TagX),ty=_l.L(UiLayout.TagY),tw=TW(tag)+_l.L(12),th=TH(tag)+_l.L(2);
        Round(g,tx,ty,tw,th,_l.L(4),TagFace);
        Put(tag,tx+_l.L(6),ty+(th-TH(tag))/2);
        double dr=_l.L(UiLayout.DotR);
        Circle(g,cw-_l.L(UiLayout.DotRight)-dr,_l.L(UiLayout.DotTop)+dr,dr,LobbyPalette.Rgb(color));
        // nickname: game charset choice for peer text (LeaderboardPanel.addEntry -> autoCharset, f@6509 op24), centred
        // and ellipsized by measured width
        var nick=Text(c.Front,p.Name,Fg,UiLayout.SizeNick,nickW,nick:true);
        Put(nick,cw/2-TW(nick)/2,_l.L(UiLayout.NickTop));
        double px=_l.L(UiLayout.PillX),py=_l.L(UiLayout.PillTop),pw=cw-2*px,ph=_l.L(UiLayout.PillH);
        Round(g,px,py,pw,ph,ph/2,p.Ready?Gold:PillOff);
        var pill=Text(c.Front,LobbyText.Get(p.Ready?"ready":"notReady"),p.Ready?GoldInk:Muted,UiLayout.SizePill,pw-_l.L(12));
        Put(pill,px+(pw-TW(pill))/2,py+(ph-TH(pill))/2);
    }

    // Body + head with the mod's own lobby helpers, called (not patched), exactly like PlaceLobbyBeheadedUnderPlayerList
    // (IL 97161-97240): CreateLobbyBeheaded -> addChild -> ApplyLobbyBeheadedSkin(.., "idle", ..) -> CreateLobbyHead
    // (same scale) -> addChild -> PositionLobbyHeadOnBone(.., -48*u, ..). Rebuilt only when the player's id, skin or head
    // changes; a body rebuilt while ready shows frame 20 at once (the seat state is kept).
    void BuildBody(Card c,LobbyPlayer p,int color)
    {
        DropBody(c);
        c.Silhouette=p==null;
        string skin=p==null||string.IsNullOrWhiteSpace(p.Skin)?"PrisonerDefault":p.Skin;
        string head=p==null||string.IsNullOrWhiteSpace(p.Head)?"BaseFlame":p.Head;
        double s=UiLayout.HeroScale(_l.K);
        var body=LobbyUiNative.CreateBody(Ui,skin,0,s);
        if(body==null&&skin!="PrisonerDefault"){skin="PrisonerDefault";body=LobbyUiNative.CreateBody(Ui,skin,0,s);}
        if(body==null)return;
        c.Body=body;
        c.BodyRoot.addChild(body);
        Put(body,_l.L(UiLayout.CardW/2),_l.L(UiLayout.FeetY)-2*s);      // pivot 0.5/0.5, feet 2 px under the centre (scroll-anim verify row 6)
        LobbyUiNative.ApplySkin(Ui,body,skin,"idle",-1,"coop-lobby");
        c.Body=body;c.Skin=skin;c.HeadId=head;c.Carry=false;c.Shown=-1;c.LastCursor=-1;
        try{c.CanCarry=body.lib?.groups?.exists("carryItem".AsHaxeString())==true;}catch{c.CanCarry=false;}
        if(c.Silhouette){LobbyUiNative.Silhouette(body);Darken(body);}else Tint(body,color);
        int before=LobbyUiNative.ActiveCount();
        var h=LobbyUiNative.CreateHead(head,s);
        LobbyUiNative.TakeEmitters(before,h,c.Fx);
        if(h==null)return;
        c.Head=h;c.BodyRoot.addChild(h);c.HeadFallback=-48*s/2.75;
        if(c.Silhouette){LobbyUiNative.DisposeEmitters(c.Fx);LobbyUiNative.Silhouette(h);Darken(h);}   // no live flames on the empty seat
        LobbyUiNative.PlaceHead(Ui,h,body,skin,c.HeadFallback,head);
    }
    static void DropBody(Card c)
    {
        LobbyUiNative.DisposeEmitters(c.Fx);
        try{c.Body?.remove();}catch{}
        try{c.Head?.remove();}catch{}
        try{c.BodyRoot?.removeChildren();}catch{}     // anything a failed build left behind
        c.Body=null;c.Head=null;c.BodyKey=null;c.Carry=false;
    }
    // Player colour: body colour multipliers (the CoopSyncFixes / CoopFrameFix body tint method, PlayerTint.cs:216)
    // The mod's silhouette (0.18,0.18,0.22, IL 97481) still reads as a lit hero under the lobby DirLighted pass (native
    // capture lobbyqa-full-01 host-alone); the empty seat of the approved mockup is a dark shape, so go darker.
    static void Darken(dc.h2d.Object o)
    {
        if(o==null)return;
        if(o is HSprite s){var v=s.color;if(v!=null){v.x=0.07;v.y=0.07;v.z=0.09;}}
        var ch=o.children;if(ch==null)return;
        for(int i=0;i<ch.length;i++)if(ch.getDyn(i) is dc.h2d.Object k)Darken(k);
    }
    static void Tint(HSprite body,int color)
    {
        var v=body?.color;if(v==null)return;
        var (r,g,b)=LobbyPalette.Multipliers(color);v.x=r;v.y=g;v.z=b;
    }

    void BuildHeader()
    {
        var players=LobbyModel.Players;
        bool steam=LobbyUiNative.Steam;
        string code="";try{code=LobbyUiNative.LobbyCode()??"";}catch{}
        LobbyPlayer host=null;foreach(var p in players)if(p.IsHost){host=p;break;}
        string second=_hostBar?LobbyText.Format("players",players.Count):host!=null?LobbyText.Format("hostedBy",host.Name):LobbyText.Get("connecting");
        string sig=_hostBar+"|"+steam+"|"+second+"|"+code;
        if(sig==_headSig)return;
        _headSig=null;
        _headRoot.removeChildren();Retire(_keepHead);
        double mid=_l.X(UiLayout.StageW/2),titleMax=_l.L(400);
        if(code.Length>0)titleMax=Math.Max(_l.L(80),Math.Min(titleMax,2*(Chip(code)-mid)-_l.L(12)));   // the centred title never reaches the chip
        var title=Text(_headRoot,LobbyText.Get("title"),TitleColor,UiLayout.SizeTitle,titleMax);
        Put(title,mid-TW(title)/2,_l.Y(UiLayout.TitleTop));
        // "LAN <dot> Players: 1 of 2": the separator is drawn (no bullet glyphs in the fonts)
        double gap=_l.L(14),max=_l.L(420);
        var a=Text(_headRoot,steam?"Steam":"LAN",Muted,UiLayout.SizeSub,0);
        var b=Text(_headRoot,second,Muted,UiLayout.SizeSub,Math.Max(_l.L(40),max-TW(a)-gap));
        double total=TW(a)+gap+TW(b),x=_l.X(UiLayout.StageW/2)-total/2,y=_l.Y(UiLayout.SubTop);
        Put(a,x,y);Put(b,x+TW(a)+gap,y);
        var g=new dc.h2d.Graphics(_headRoot);Circle(g,x+TW(a)+gap/2,y+TH(a)/2,Math.Max(1,_l.L(1.5)),Muted);
        _headSig=sig;
    }
    // Steam lobby code chip, top right of the stage, apart from the cards (fixes the old card overlap, brief C4).
    // Returns its left edge in px. It ends above the subline (ChipTop+ChipH = 30 < SubTop 35).
    double Chip(string code)
    {
        var t=Text(_headRoot,LobbyText.Format("code",code),TagInk,UiLayout.SizeChip,_l.L(200));
        double cw=TW(t)+_l.L(16),ch=_l.L(UiLayout.ChipH),cx=_l.X(UiLayout.ChipRight)-cw,cy=_l.Y(UiLayout.ChipTop);
        var cg=new dc.h2d.Graphics(_headRoot);Put(cg,cx,cy);
        Frame(cg,0,0,cw,ch,_l.L(5),BtnEdge,BtnFace,Math.Max(1,Math.Round(_l.L(1.5))));
        cg.parent.addChild(t);   // text above its plate
        Put(t,cx+(cw-TW(t))/2,cy+(ch-TH(t))/2);
        var hit=new dc.h2d.Interactive(cw,ch,_headRoot,null);Put(hit,cx,cy);
        Keep(_keepHead,hit,null,null,e=>Safe(()=>
        {
            // same action as the original card click (ConnectionUI.OnClick, IL 96772-96777), our words
            if(LobbyUiNative.CopyCode()){var msg=LobbyText.Get("codeCopied");Notice(msg);LobbyUiNative.Toast(msg,10,2.5);}
        }));
        return cx;
    }

    void BuildSwatches()
    {
        int local=LobbyModel.LocalId;
        bool show=LobbyColorState.CanPick&&_localCard>=0;
        var sb=new System.Text.StringBuilder().Append(show).Append('|').Append(_localCard).Append('|').Append(LobbyColorState.Revision).Append('|').Append(LobbyColorState.Pending).Append('|').Append(_otherName);
        if(show)for(int i=0;i<LobbyPalette.Count;i++)sb.Append(LobbyColorState.TakenByOther(i,local)?'x':'-');
        string sig=sb.ToString();
        if(sig==_swSig)return;
        _swSig=null;
        if(_hoverOwner is dc.h2d.Interactive)ClearHover(_hoverOwner);      // its Interactive is about to go (no onOut then)
        _swRoot.removeChildren();Retire(_keepSw);
        if(show)DrawSwatches(local);
        _swSig=sig;
    }
    void DrawSwatches(int local)
    {
        int mine=LobbyColorState.ColorOf(local),pending=LobbyColorState.Pending;
        var g=new dc.h2d.Graphics(_swRoot);
        for(int i=0;i<LobbyPalette.Count&&i<_l.Swatches.Length;i++)
        {
            var r=_l.Px(_l.Swatches[i]);double cx=r.X+r.W/2,cy=r.Y+r.H/2,rad=r.W/2;
            bool taken=LobbyColorState.TakenByOther(i,local);
            int rgb=LobbyPalette.Rgb(i);
            if(i==pending&&i!=mine){Circle(g,cx,cy,rad+_l.L(2.5),Accent);Circle(g,cx,cy,rad+_l.L(1),BgColor);}
            if(taken)
            {
                Circle(g,cx,cy,rad,LobbyUiRules.Mix(rgb,BgColor,0.3));
                int ink=LobbyUiRules.Mix(0xFFFFFF,BgColor,0.3);
                foreach(var q in LobbyUiRules.CrossSquares(cx,cy,rad,Math.Max(1,_l.L(2))))Fill(g,q.X,q.Y,q.W,q.H,ink,1);
            }
            else if(i==mine){Circle(g,cx,cy,rad,0xFFFFFF);Circle(g,cx,cy,rad-_l.L(2),rgb);}
            else Circle(g,cx,cy,rad,rgb);
            int index=i;
            var hit=new dc.h2d.Interactive(r.W,r.H,_swRoot,null);Put(hit,r.X,r.Y);
            string name=LobbyText.Get(LobbyPalette.Colors[i].Key),takenText=LobbyText.Format("colorTaken",_otherName);
            Keep(_keepSw,hit,e=>Safe(()=>SetHover(hit,taken?takenText:name)),e=>Safe(()=>ClearHover(hit)),e=>Safe(()=>
            {
                if(taken){Notice(takenText);return;}
                LobbyColorState.Request(index);LobbyUi.PollSoon();
            }));
        }
    }

    void BuildMode()
    {
        string text=null;
        if(!_hostBar&&_modeLabel!=null)
        {
            var key=LobbyUiRules.ModeText(_modeLabel);
            string mode=key==null?_modeLabel:key.StartsWith("msgid:",StringComparison.Ordinal)?LobbyText.GameString(key.Substring(6))??LobbyText.Get("continue"):LobbyText.Get(key);
            text=LobbyText.Format("hostMode",mode);
        }
        if(_modeSig!=null&&text==_modeSig)return;
        _modeSig=null;
        _modeRoot.removeChildren();
        if(text!=null)
        {
            var t=Text(_modeRoot,text,ModeColor,UiLayout.SizeMode,_l.L(UiLayout.RowWidth));
            Put(t,_l.X(UiLayout.StageW/2)-TW(t)/2,_l.Y(UiLayout.ModeTop));
        }
        _modeSig=text;
    }

    void BuildBar()
    {
        if(_l==null)return;
        _barRoot.removeChildren();_btns.Clear();Retire(_keepBar);
        if(_hoverOwner is Btn)ClearHover(_hoverOwner);
        _l=Layout();
        var kinds=LobbyUiRules.Classify(_hostBar,Labels(),_exitLabel);
        var rows=LobbyUiRules.Rows(_hostBar,kinds);
        _allReady=false;
        for(int i=0;i<kinds.Length;i++)if(kinds[i]==UiButtonKind.Start)_allReady=_buttons[i].Enabled;   // Start is enabled iff AllPlayersReady() (IL 71671, 71711-71712)
        for(int r=0;r<rows.Count&&r<_l.Rows.Count;r++)for(int j=0;j<rows[r].Count;j++)
        {
            int idx=rows[r][j];var m=_buttons[idx];
            var b=new Btn{M=m,Kind=kinds[idx],R=_l.Px(_l.Rows[r][j])};
            b.G=new dc.h2d.Graphics(_barRoot);Put(b.G,b.R.X,b.R.Y);
            b.T=Text(_barRoot,ButtonLabel(b.Kind,m),Fg,UiLayout.SizeButton,b.R.W-_l.L(12));
            Put(b.T,b.R.X+(b.R.W-TW(b.T))/2,b.R.Y+(b.R.H-TH(b.T))/2);
            b.HelpText=ButtonHelp(b.Kind,m);
            b.I=new dc.h2d.Interactive(b.R.W,b.R.H,_barRoot,null);Put(b.I,b.R.X,b.R.Y);
            var bb=b;
            b.Over=e=>Safe(()=>{bb.Hover=true;DrawButton(bb);SetHover(bb,bb.HelpText);});
            b.Out=e=>Safe(()=>{bb.Hover=false;DrawButton(bb);ClearHover(bb);});
            b.Click=e=>Safe(()=>{if(bb.M.Enabled)LobbyUi.Click(bb.M);});
            Keep(_keepBar,b.I,b.Over,b.Out,b.Click);
            DrawButton(b);
            _btns.Add(b);
        }
    }
    string ButtonLabel(UiButtonKind k,LobbyUiButton m)
    {
        var key=LobbyUiRules.LabelKey(k);
        if(key==null)return m.Label;
        if(k==UiButtonKind.Save){int n=LobbyUiRules.SlotNumber(m.Label);return n>=0?LobbyText.Format("slot",n):m.Label;}
        return LobbyText.Get(key);
    }
    string ButtonHelp(UiButtonKind k,LobbyUiButton m)
    {
        // a disabled launch button says why (its captured Help is the mod's reason, IL 71680-71694)
        if(!m.Enabled)return LobbyUiRules.IsLaunch(k)&&!_allReady?LobbyText.Get("notAllReady"):m.Help;
        var key=LobbyUiRules.HelpKey(k);
        return key==null?m.Help:LobbyText.Get(key);
    }
    void DrawButton(Btn b)
    {
        bool on=b.Kind==UiButtonKind.Ready&&_localReady&&b.M.Enabled;
        int edge=!b.M.Enabled?BtnOffEdge:on?Gold:b.Hover?Accent:BtnEdge;
        int state=(on?1:0)|(b.Hover?2:0)|(b.M.Enabled?4:0);
        if(state==b.Drawn)return;
        b.Drawn=state;
        b.G.clear();
        Frame(b.G,0,0,b.R.W,b.R.H,_l.L(6),edge,BtnFace,Math.Max(1,Math.Round(_l.L(1.5))));
        b.T.set_textColor(!b.M.Enabled?BtnOffText:on?GoldText:Fg);
    }

    void SetHint(string text)
    {
        text??="";
        if(text==_hintShown)return;
        _hintShown=null;
        _hintRoot.removeChildren();
        if(text.Length>0)
        {
            var t=Text(_hintRoot,text,HintColor,UiLayout.SizeHint,_l.L(UiLayout.StageW-40));
            Put(t,_l.X(UiLayout.StageW/2)-TW(t)/2,_l.Y(UiLayout.HintTop));
        }
        _hintShown=text;
    }
    void SetHover(object owner,string text){_hoverOwner=owner;_hoverText=text;}
    void ClearHover(object owner){if(ReferenceEquals(owner,_hoverOwner)){_hoverOwner=null;_hoverText=null;}}

    // ---- primitives ------------------------------------------------------------------------------------------
    // Text measured in px: the Haxe getters h2d.Text.get_textWidth / get_textHeight (findex 3389 / 3388, bound by
    // MethodInfo in LobbyUiNative, not the same-signature raw field accessor) times the scale ui.Text.onResize set
    // (f@3413). customScale gives a line box of size*1.2 mockup px; too wide: shrink to 80 %, then ellipsize
    // (nicknames: ellipsize only).
    dc.ui.Text Text(dc.h2d.Object parent,string s,int color,double size,double maxW,bool nick=false)
    {
        s??="";
        var t=LobbyUiNative.MakeText(s,color,parent);
        double cs=size*UiLayout.LineFactor*_l.K/_unit;
        t.customScale=cs;
        if(nick)t.autoCharset(s.AsHaxeString());else t.onResize();     // autoCharset ends with onResize (Text.hx:196-197)
        if(maxW>0&&TW(t)>maxW)
        {
            if(!nick){double f=LobbyUiRules.FitFactor(TW(t),maxW,0.8);if(f<1){t.customScale=cs*f;t.onResize();}}
            if(TW(t)>maxW){var fit=LobbyUiRules.Ellipsize(s,c=>{t.set_text(c.AsHaxeString());return TW(t);},maxW);t.set_text(fit.AsHaxeString());}
        }
        return t;
    }
    static double TW(dc.ui.Text t)=>LobbyUiNative.TextWidth(t)*t.scaleX;
    static double TH(dc.ui.Text t)=>LobbyUiNative.TextHeight(t)*t.scaleY;
    // x / y are raw field writes in the proxy (h2d.Object.set_x is inlined in the game), so the transform must be
    // flagged as the mod does after moving a head (PositionLobbyHeadOnBone set_posChanged, IL 99246-99248)
    static void Put(dc.h2d.Object o,double x,double y){o.x=x;o.y=y;o.posChanged=true;}
    void Round(dc.h2d.Graphics g,double x,double y,double w,double h,double r,int c)=>LobbyUiNative.RoundRect(g,x,y,w,h,r,c,1.0);
    void Frame(dc.h2d.Graphics g,double x,double y,double w,double h,double r,int edge,int face,double b)
    {
        Round(g,x,y,w,h,r,edge);
        Round(g,x+b,y+b,w-2*b,h-2*b,Math.Max(0,r-b),face);
    }
    static void Fill(dc.h2d.Graphics g,double x,double y,double w,double h,int color,double alpha)
    {
        int c=color;double a=alpha;
        g.beginFill(Ref<int>.From(ref c),Ref<double>.From(ref a));g.drawRect(x,y,w,h);g.endFill();
    }
    static void Circle(dc.h2d.Graphics g,double cx,double cy,double r,int color)
    {
        if(!(r>0))return;
        int c=color;double a=1;
        g.beginFill(Ref<int>.From(ref c),Ref<double>.From(ref a));g.drawCircle(cx,cy,r,Ref<int>.Null);g.endFill();
    }
    static void Keep(List<HlAction<dc.hxd.Event>> keep,dc.h2d.Interactive i,HlAction<dc.hxd.Event> over,HlAction<dc.hxd.Event> @out,HlAction<dc.hxd.Event> click)
    {
        if(over!=null){i.onOver=over;keep.Add(over);}
        if(@out!=null){i.onOut=@out;keep.Add(@out);}
        if(click!=null){i.onClick=click;keep.Add(click);}
    }
    // called right after the section's root lost its children (their Interactives left the scene, so the scene sends
    // them no more events: h2d Interactive.onRemove -> Scene.removeEventTarget)
    void Retire(List<HlAction<dc.hxd.Event>> keep){_retired.AddRange(keep);keep.Clear();}
    // Interactive callbacks run inside the game's event dispatch: nothing may escape into it.
    static void Safe(Action a){try{a();}catch(Exception e){LobbyUi.Fault(e);}}
}
