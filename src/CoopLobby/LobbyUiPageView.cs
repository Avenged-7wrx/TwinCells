using System;
using System.Collections.Generic;
using System.Diagnostics;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;
using HaxeProxy.Runtime;
using ModCore.Utilities;

// One Multiplayer page drawn in the lobby's look (same 640x384 stage, palette, measured text, header, bottom row and
// hint line as LobbyUiView). A child of ConnectionUI.root at the bottom of its layer 0, below the hidden _panelRoot
// and the text prompt. Built once per captured page (LobbyUiPages makes a new view when the page changes); only the
// friend list section and the hover state change in place. Every click calls the captured PendingButton.OnClick.
sealed class LobbyUiPageView
{
    const int WellFace=0x0B0E15,WellEdge=0x26324D;
    const int KindTile=0,KindBar=1,KindWell=2,KindRow=3,KindPager=4;

    sealed class Btn
    {
        public int Index=-1,Kind,Dir,Drawn=-1;public UiRect R;public double Radius,MarkW,MarkH;
        public dc.h2d.Graphics G,Mark;public dc.h2d.Interactive I;
        public bool Hover,Accent,Enabled=true;public string Help="";
    }

    public readonly ConnectionUI Ui;
    readonly dc.h2d.Layers _root;
    readonly dc.h2d.Object _layer,_bgRoot,_headRoot,_bodyRoot,_listRoot,_barRoot,_hintRoot;
    readonly dc.ui.Text _probe;
    UiPageSnap _p;UiPageLayout _l;int _w,_h;double _unit,_lastProbe;string _lang="";bool _own;
    readonly List<Btn> _btns=new();
    // Delegates handed to native Interactives stay referenced while their Interactive can still call them (same rule
    // as LobbyUiView): one list per section; a cleared section retires its list, dropped at the next Tick (P3).
    readonly List<HlAction<dc.hxd.Event>> _keepBg=new(),_keepBody=new(),_keepList=new(),_keepBar=new(),_retired=new();
    object _hoverOwner;string _hoverText,_hintShown;bool _listDirty;

    static double Now=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;

    public LobbyUiPageView(ConnectionUI ui,UiPageSnap page)
    {
        Ui=ui;_p=page??throw new ArgumentNullException(nameof(page));
        _root=ui.root??throw new InvalidOperationException("ConnectionUI has no root");
        _layer=new dc.h2d.Object(null);
        _root.addChild(_layer);
        try
        {
            _root.under(_layer);    // bottom of layer 0: below the (hidden) _panelRoot and below _promptRoot
            _bgRoot=new dc.h2d.Object(_layer);_headRoot=new dc.h2d.Object(_layer);_bodyRoot=new dc.h2d.Object(_layer);
            _listRoot=new dc.h2d.Object(_layer);_barRoot=new dc.h2d.Object(_layer);_hintRoot=new dc.h2d.Object(_layer);
            _probe=LobbyUiNative.MakeText("Ag",0xFFFFFF,_layer);_probe.set_visible(false);
            Rebuild();
        }
        catch{try{_layer.remove();}catch{}throw;}   // never leave a half-built layer attached
    }
    public bool Valid(ConnectionUI ui)=>ReferenceEquals(ui,Ui)&&ui.root!=null&&ui.root.HashlinkPointer==_root.HashlinkPointer&&_layer.parent!=null;
    public void Show(){if(!_layer.visible)_layer.set_visible(true);}
    public void HideNow(){if(_layer.visible)_layer.set_visible(false);}
    public void Dispose(){try{_layer.remove();}catch{}_btns.Clear();}
    // The same page committed again (same strings, new closures): clicks resolve the model through _p at click time.
    public void Rebind(UiPageSnap page){if(page!=null&&page.Kind==_p.Kind&&page.Buttons.Length==_p.Buttons.Length)_p=page;}

    // ---- per frame (P3) --------------------------------------------------------------------------------------------
    public void Tick(double dt)
    {
        _retired.Clear();
        var win=dc.hxd.Window.Class.getInstance();
        int w=win.get_width(),h=win.get_height();
        double now=Now;
        bool rebuild=w!=_w||h!=_h;
        if(!rebuild&&now-_lastProbe>0.5)
        {
            // font step follows Main.pixelScale a few frames after the window; the language can change in Options
            _lastProbe=now;
            rebuild=Math.Abs(MeasureUnit()-_unit)>_unit*0.01||LobbyText.Lang!=_lang;
        }
        if(rebuild){Rebuild();return;}
        if(_listDirty){_listDirty=false;BuildList();}
        SetHint(_hoverText??"");
    }

    // ---- build -----------------------------------------------------------------------------------------------------
    void Rebuild(){try{RebuildAll();}catch{_w=-1;throw;}}
    void RebuildAll()
    {
        var win=dc.hxd.Window.Class.getInstance();
        _w=win.get_width();_h=win.get_height();_lang=LobbyText.Lang;_own=LobbyUiPageRules.UseOwnText(_lang);
        _unit=MeasureUnit();_lastProbe=Now;
        _l=Layout();
        _hoverOwner=null;_hoverText=null;_btns.Clear();
        _bgRoot.removeChildren();Retire(_keepBg);
        var bg=new dc.h2d.Graphics(_bgRoot);Fill(bg,0,0,_w,_h,LobbyUiView.BgColor,0.94);   // the original panel's full-window cover
        var block=new dc.h2d.Interactive(_w,_h,_bgRoot,null);                               // swallows clicks like the original's inter
        Keep(_keepBg,block,null,null,e=>{});
        BuildHeader();BuildBody();BuildBar();BuildList();
        _hintRoot.removeChildren();_hintShown=null;
    }
    UiPageLayout Layout()
    {
        var l=UiPageLayout.Compute(_w,_h,_p.Kind,_p.Plan.Friends,LobbyUiPages.FriendPage);
        LobbyUiPages.FriendPage=l.Page;          // clamped (a shorter friend list pulls the page back)
        return l;
    }
    double MeasureUnit()
    {
        _probe.customScale=1;_probe.onResize();
        double u=TH(_probe);
        return u>0.5?u:16;
    }
    // Our words in en / ru; in other languages the mod's own captured string (glyph-safe). key null: captured only.
    string Say(string key,string captured)=>key==null?LobbyUiPageRules.Plain(captured):LobbyUiPageRules.Pick(_own,LobbyText.Get(key),captured);
    LobbyUiButton Model(Btn b)=>b.Index>=0&&b.Index<_p.Buttons.Length?_p.Buttons[b.Index]:null;

    void BuildHeader()
    {
        _headRoot.removeChildren();
        bool host=_p.Plan.Host;
        string title=Say(LobbyUiPageRules.TitleKey(_p.Kind,host),_p.Infos.Length>0?_p.Infos[0]:"");
        string sub=Say(LobbyUiPageRules.SubKey(_p.Kind,host,_p.Plan.SubVariant),_p.Infos.Length>1?_p.Infos[1]:"");
        double mid=_l.X(UiLayout.StageW/2);
        var t=Text(_headRoot,title,LobbyUiView.TitleColor,UiLayout.SizeTitle,_l.L(560));
        Put(t,mid-TW(t)/2,_l.Y(UiLayout.TitleTop));
        if(sub.Length>0){var s=Text(_headRoot,sub,LobbyUiView.Muted,UiLayout.SizeSub,_l.L(600));Put(s,mid-TW(s)/2,_l.Y(UiLayout.SubTop));}
    }

    void BuildBody()
    {
        _bodyRoot.removeChildren();Retire(_keepBody);
        bool big=_l.Choices.Count==2;
        foreach(var it in _p.Plan.Of(UiItemRole.Choice))if(it.Slot<_l.Choices.Count)Tile(it,_l.Choices[it.Slot],big);
        foreach(var it in _p.Plan.Of(UiItemRole.Field))if(it.Slot<_l.Wells.Count)Field(it);
    }
    // Choice tile: label and its help line inside, both centred and fitted to the tile by measured width.
    void Tile(UiPageItem it,UiRect m,bool big)
    {
        var mdl=_p.Buttons[it.Button];bool host=_p.Plan.Host;
        var b=NewBtn(it.Button,_l.Px(m),KindTile,_l.L(8),mdl,_bodyRoot);
        string label=Say(LobbyUiPageRules.LabelKey(_p.Kind,host,UiItemRole.Choice,it.Slot),mdl.Label);
        string help=Say(LobbyUiPageRules.HelpKey(_p.Kind,host,UiItemRole.Choice,it.Slot),mdl.Help);
        double pad=_l.L(10),maxW=b.R.W-2*pad;
        var lt=Text(_bodyRoot,label,!b.Enabled?LobbyUiView.BtnOffText:b.Accent?LobbyUiView.Accent:LobbyUiView.Fg,big?UiPageLayout.SizeBig:UiPageLayout.SizeSmall,maxW);
        dc.ui.Text ht=null;
        if(help.Length>0)ht=Text(_bodyRoot,help,!b.Enabled?LobbyUiView.BtnOffText:LobbyUiView.Muted,big?UiPageLayout.SizeBigHelp:UiPageLayout.SizeSmallHelp,maxW);
        double gap=_l.L(big?6:3),total=TH(lt)+(ht!=null?gap+TH(ht):0),y=b.R.Y+(b.R.H-total)/2;
        Put(lt,b.R.X+(b.R.W-TW(lt))/2,y);
        if(ht!=null)Put(ht,b.R.X+(b.R.W-TW(ht))/2,y+TH(lt)+gap);
        Hit(b,_bodyRoot,_keepBody,"");      // the help is on the tile itself
    }
    // LAN field block (the original's field style): caption above, a well with the current value, an edit mark; the
    // click opens the mod's own text prompt (original, above this layer).
    void Field(UiPageItem it)
    {
        var mdl=_p.Buttons[it.Button];bool host=_p.Plan.Host;
        var cap=_l.Px(_l.Captions[it.Slot]);
        string value=it.Value??"";
        string prefix=mdl.Label.Length>=value.Length?mdl.Label.Substring(0,mdl.Label.Length-value.Length):"";
        var ct=Text(_bodyRoot,Say(LobbyUiPageRules.LabelKey(_p.Kind,host,UiItemRole.Field,it.Slot),LobbyUiPageRules.Caption(prefix)),LobbyUiView.Muted,UiPageLayout.SizeCaption,cap.W);
        Put(ct,cap.X+_l.L(2),cap.Y+(cap.H-TH(ct))/2);
        var b=NewBtn(it.Button,_l.Px(_l.Wells[it.Slot]),KindWell,_l.L(5),mdl,_bodyRoot);
        double pad=_l.L(10);b.MarkW=_l.L(6);b.MarkH=_l.L(10);b.Dir=1;
        string shown=LobbyUiPageRules.Value(value);
        // the player name is peer-style text (nick charset, as the lobby cards); IP and port are plain
        var vt=Text(_bodyRoot,shown,!b.Enabled?LobbyUiView.BtnOffText:LobbyUiView.Fg,UiPageLayout.SizeValue,b.R.W-2*pad-b.MarkW-_l.L(8),nick:it.Slot==0&&shown.Length>0);
        Put(vt,b.R.X+pad,b.R.Y+(b.R.H-TH(vt))/2);
        b.Mark=new dc.h2d.Graphics(_bodyRoot);Put(b.Mark,b.R.Right-pad-b.MarkW,b.R.Y+(b.R.H-b.MarkH)/2);
        b.Drawn=-1;Draw(b);
        Hit(b,_bodyRoot,_keepBody,Say(LobbyUiPageRules.HelpKey(_p.Kind,host,UiItemRole.Field,it.Slot),mdl.Help));
    }

    // Bottom row (the lobby's exit row): Back, and the LAN page's Host / Join before it, in the captured order.
    void BuildBar()
    {
        _barRoot.removeChildren();Retire(_keepBar);
        bool host=_p.Plan.Host;int i=0;
        foreach(var it in _p.Plan.Items)
        {
            if(it.Role!=UiItemRole.Primary&&it.Role!=UiItemRole.Back)continue;
            if(i>=_l.Bottom.Length)break;
            var mdl=_p.Buttons[it.Button];
            var b=NewBtn(it.Button,_l.Px(_l.Bottom[i++]),KindBar,_l.L(6),mdl,_barRoot);
            // Back is the game's own word in every language (LobbyText "back" = game msgid "Retour")
            string label=it.Role==UiItemRole.Back?LobbyText.Get("back"):Say(LobbyUiPageRules.LabelKey(_p.Kind,host,it.Role,0),mdl.Label);
            var t=Text(_barRoot,label,!b.Enabled?LobbyUiView.BtnOffText:b.Accent?LobbyUiView.Accent:LobbyUiView.Fg,UiLayout.SizeButton,b.R.W-_l.L(12));
            Put(t,b.R.X+(b.R.W-TW(t))/2,b.R.Y+(b.R.H-TH(t))/2);
            Hit(b,_barRoot,_keepBar,Say(LobbyUiPageRules.HelpKey(_p.Kind,host,it.Role,0),mdl.Help));
        }
    }

    // Join page: the friend lobbies, PerPage at a time with a pager (the original lists them all, unbounded: brief 1.2
    // #4). Rebuilt in place from Tick only (a pager click marks it dirty), never inside an Interactive callback.
    void BuildList()
    {
        _listRoot.removeChildren();Retire(_keepList);
        _btns.RemoveAll(x=>x.Kind==KindRow||x.Kind==KindPager);
        if(_hoverOwner is Btn hb&&(hb.Kind==KindRow||hb.Kind==KindPager)){_hoverOwner=null;_hoverText=null;}
        if(_p.Kind!=UiPageKind.JoinTransport)return;
        _l=Layout();
        double border=Math.Max(1,Math.Round(_l.L(1.5)));
        if(_p.Plan.Friends==0)
        {
            var e=_l.Px(_l.Empty);
            var g=new dc.h2d.Graphics(_listRoot);Put(g,e.X,e.Y);
            Frame(g,0,0,e.W,e.H,_l.L(8),LobbyUiView.CardEdgeEmpty,LobbyUiRules.Mix(LobbyUiView.CardFace,LobbyUiView.BgColor,0.5),border);
            var t=Text(_listRoot,LobbyText.Get("noFriendLobbies"),LobbyUiView.HintColor,UiPageLayout.SizeEmpty,e.W-_l.L(20));
            Put(t,e.X+(e.W-TW(t))/2,e.Y+(e.H-TH(t))/2);
            return;
        }
        var friends=new List<UiPageItem>(_p.Plan.Of(UiItemRole.Friend));
        bool host=_p.Plan.Host;
        for(int i=0;i<_l.Shown&&_l.First+i<friends.Count;i++)
        {
            var it=friends[_l.First+i];var mdl=_p.Buttons[it.Button];
            var b=NewBtn(it.Button,_l.Px(_l.Rows[i]),KindRow,_l.L(6),mdl,_listRoot);
            double pad=_l.L(10);
            string name=LobbyUiPageRules.Value(it.Value);if(name.Length==0)name="Steam";
            string word=mdl.Label.Length>=(it.Value??"").Length?mdl.Label.Substring((it.Value??"").Length).TrimStart(' ','-'):"";
            var act=Text(_listRoot,Say(LobbyUiPageRules.LabelKey(_p.Kind,host,UiItemRole.Friend,0),word),!b.Enabled?LobbyUiView.BtnOffText:LobbyUiView.Accent,UiPageLayout.SizeRow,b.R.W*0.35);
            Put(act,b.R.Right-pad-TW(act),b.R.Y+(b.R.H-TH(act))/2);
            var nt=Text(_listRoot,name,!b.Enabled?LobbyUiView.BtnOffText:LobbyUiView.Fg,UiPageLayout.SizeRow,Math.Max(_l.L(20),b.R.W-3*pad-TW(act)),nick:true);
            Put(nt,b.R.X+pad,b.R.Y+(b.R.H-TH(nt))/2);
            Hit(b,_listRoot,_keepList,Say(LobbyUiPageRules.HelpKey(_p.Kind,host,UiItemRole.Friend,0),mdl.Help));
        }
        if(!_l.Pager)return;
        Pager(-1,_l.Prev);Pager(1,_l.Next);
        var pl=_l.Px(_l.PageLabel);
        var pt=Text(_listRoot,LobbyText.Format("pageOf",_l.Page+1,_l.Pages),LobbyUiView.Muted,UiPageLayout.SizePager,pl.W);
        Put(pt,pl.X+(pl.W-TW(pt))/2,pl.Y+(pl.H-TH(pt))/2);
    }
    void Pager(int dir,UiRect m)
    {
        var b=new Btn{Kind=KindPager,R=_l.Px(m),Radius=_l.L(5),Dir=dir,Enabled=dir<0?_l.Page>0:_l.Page<_l.Pages-1};
        b.G=new dc.h2d.Graphics(_listRoot);Put(b.G,b.R.X,b.R.Y);
        b.MarkW=_l.L(7);b.MarkH=_l.L(10);
        b.Mark=new dc.h2d.Graphics(_listRoot);Put(b.Mark,b.R.X+(b.R.W-b.MarkW)/2,b.R.Y+(b.R.H-b.MarkH)/2);
        b.Help=LobbyText.Get(dir<0?"pagePrev":"pageNext");
        _btns.Add(b);Draw(b);
        b.I=new dc.h2d.Interactive(b.R.W,b.R.H,_listRoot,null);Put(b.I,b.R.X,b.R.Y);
        var bb=b;
        Keep(_keepList,b.I,e=>Safe(()=>{bb.Hover=true;Draw(bb);SetHover(bb,bb.Help);}),e=>Safe(()=>{bb.Hover=false;Draw(bb);ClearHover(bb);}),
            e=>Safe(()=>{if(!bb.Enabled)return;LobbyUiPages.FriendPage=_l.Page+bb.Dir;_listDirty=true;}));
    }

    // ---- buttons ---------------------------------------------------------------------------------------------------
    // The plate goes into the parent first, so the texts added after it are drawn on top; Hit adds the Interactive last.
    Btn NewBtn(int index,UiRect px,int kind,double radius,LobbyUiButton mdl,dc.h2d.Object parent)
    {
        var b=new Btn{Index=index,Kind=kind,R=px,Radius=radius,Enabled=mdl.Enabled,Accent=mdl.Color!=0xFFFFFF};
        b.G=new dc.h2d.Graphics(parent);Put(b.G,b.R.X,b.R.Y);
        _btns.Add(b);Draw(b);
        return b;
    }
    void Hit(Btn b,dc.h2d.Object parent,List<HlAction<dc.hxd.Event>> keep,string help)
    {
        b.Help=help??"";
        b.I=new dc.h2d.Interactive(b.R.W,b.R.H,parent,null);Put(b.I,b.R.X,b.R.Y);
        var bb=b;
        Keep(keep,b.I,
            e=>Safe(()=>{bb.Hover=true;Draw(bb);if(bb.Help.Length>0)SetHover(bb,bb.Help);}),
            e=>Safe(()=>{bb.Hover=false;Draw(bb);ClearHover(bb);}),
            e=>Safe(()=>{var m=Model(bb);if(m!=null&&m.Enabled)LobbyUiPages.Click(m);}));
    }
    void Draw(Btn b)
    {
        int state=(b.Hover?1:0)|(b.Enabled?2:0);
        if(state==b.Drawn)return;
        b.Drawn=state;
        int rest=b.Kind==KindWell?WellEdge:b.Accent?LobbyUiRules.Mix(LobbyUiView.Accent,LobbyUiView.BtnEdge,0.45):LobbyUiView.BtnEdge;
        int edge=!b.Enabled?LobbyUiView.BtnOffEdge:b.Hover?LobbyUiView.Accent:rest;
        int face=b.Kind==KindWell?WellFace:b.Kind==KindTile?LobbyUiView.CardFace:LobbyUiView.BtnFace;
        b.G.clear();
        Frame(b.G,0,0,b.R.W,b.R.H,b.Radius,edge,face,Math.Max(1,Math.Round(_l.L(b.Kind==KindTile?2:1.5))));
        if(b.Mark!=null)
        {
            b.Mark.clear();
            int ink=!b.Enabled?LobbyUiView.BtnOffText:b.Hover?LobbyUiView.Accent:LobbyUiView.Muted;
            int cols=Math.Max(3,(int)Math.Round(b.MarkW/Math.Max(1,_l.L(1.2))));
            foreach(var r in LobbyUiRules.Arrow(0,0,b.MarkW,b.MarkH,b.Dir>=0,cols))Fill(b.Mark,r.X,r.Y,r.W,r.H,ink,1);
        }
    }

    void SetHint(string text)
    {
        text??="";
        if(text==_hintShown)return;
        _hintShown=null;
        _hintRoot.removeChildren();
        if(text.Length>0)
        {
            var t=Text(_hintRoot,text,LobbyUiView.HintColor,UiLayout.SizeHint,_l.L(UiLayout.StageW-40));
            Put(t,_l.X(UiLayout.StageW/2)-TW(t)/2,_l.Y(UiLayout.HintTop));
        }
        _hintShown=text;
    }
    void SetHover(object owner,string text){_hoverOwner=owner;_hoverText=text;}
    void ClearHover(object owner){if(ReferenceEquals(owner,_hoverOwner)){_hoverOwner=null;_hoverText=null;}}

    // ---- primitives (the lobby view's, LobbyUiView.Text / TW / TH / Put / Frame) -----------------------------------
    // makeText + customScale for a line box of size*1.2 mockup px, measured with the Haxe getters times scaleX/Y;
    // too wide: shrink to 80 %, then ellipsize (peer text: nick charset, ellipsize only).
    dc.ui.Text Text(dc.h2d.Object parent,string s,int color,double size,double maxW,bool nick=false)
    {
        s??="";
        var t=LobbyUiNative.MakeText(s,color,parent);
        double cs=size*UiLayout.LineFactor*_l.K/_unit;
        t.customScale=cs;
        if(nick)t.autoCharset(s.AsHaxeString());else t.onResize();
        if(maxW>0&&TW(t)>maxW)
        {
            if(!nick){double f=LobbyUiRules.FitFactor(TW(t),maxW,0.8);if(f<1){t.customScale=cs*f;t.onResize();}}
            if(TW(t)>maxW){var fit=LobbyUiRules.Ellipsize(s,c=>{t.set_text(c.AsHaxeString());return TW(t);},maxW);t.set_text(fit.AsHaxeString());}
        }
        return t;
    }
    static double TW(dc.ui.Text t)=>LobbyUiNative.TextWidth(t)*t.scaleX;
    static double TH(dc.ui.Text t)=>LobbyUiNative.TextHeight(t)*t.scaleY;
    static void Put(dc.h2d.Object o,double x,double y){o.x=x;o.y=y;o.posChanged=true;}
    static void Round(dc.h2d.Graphics g,double x,double y,double w,double h,double r,int c)=>LobbyUiNative.RoundRect(g,x,y,w,h,r,c,1.0);
    static void Frame(dc.h2d.Graphics g,double x,double y,double w,double h,double r,int edge,int face,double b)
    {
        Round(g,x,y,w,h,r,edge);
        Round(g,x+b,y+b,w-2*b,h-2*b,Math.Max(0,r-b),face);
    }
    static void Fill(dc.h2d.Graphics g,double x,double y,double w,double h,int color,double alpha)
    {
        int c=color;double a=alpha;
        g.beginFill(Ref<int>.From(ref c),Ref<double>.From(ref a));g.drawRect(x,y,w,h);g.endFill();
    }
    static void Keep(List<HlAction<dc.hxd.Event>> keep,dc.h2d.Interactive i,HlAction<dc.hxd.Event> over,HlAction<dc.hxd.Event> @out,HlAction<dc.hxd.Event> click)
    {
        if(over!=null){i.onOver=over;keep.Add(over);}
        if(@out!=null){i.onOut=@out;keep.Add(@out);}
        if(click!=null){i.onClick=click;keep.Add(click);}
    }
    void Retire(List<HlAction<dc.hxd.Event>> keep){_retired.AddRange(keep);keep.Clear();}
    static void Safe(Action a)=>LobbyUiPages.Guard(a);
}
