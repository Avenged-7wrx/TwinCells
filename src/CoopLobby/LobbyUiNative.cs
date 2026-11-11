using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DeadCellsMultiplayerMod;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;
using dc.libs.heaps.slib;
using HaxeProxy.Runtime;
using ModCore.Utilities;

// Every mod / proxy member the UI part reflects on, resolved once at install with exact signatures (LobbyCore.Method /
// Field). Bind() throws on the first mismatch, so a changed build leaves the part off. Calling (not patching) private
// mod methods is plain CLR; nothing here patches ConnectionUI (a dc.ui.Process subclass, hard rule).
static class LobbyUiNative
{
    // ConnectionUI (release names, notes\ui\sig.txt)
    public static Func<ConnectionUI> LiveUi;                 // static private TryGetLiveInstance(): lifecycle + root + destroyed checked (IL 93536-93572)
    public static FieldInfo PanelRoot,PromptRoot,KeepLobby,Mode,PendingButtons,PendingInfos;
    public static FieldInfo BLabel,BHelp,BEnabled,BColor,BOnClick,IText;
    public static int ModeMenu;                              // UiMode.Menu = 1 (CommitMenu stores 1, IL 93729-93731; ShowLobbyMode stores Lobby = 0, IL 93769-93770)
    public static Func<ConnectionUI,string,int,double,HSprite> CreateBody;              // CreateLobbyBeheaded (IL 97402)
    public static Action<ConnectionUI,HSprite,string,string,int,string> ApplySkin;      // ApplyLobbyBeheadedSkin (IL 97984)
    public static Action<ConnectionUI,dc.h2d.Object,HSprite,string,double,string> PlaceHead; // PositionLobbyHeadOnBone (IL 99217)
    public static Func<string,double,dc.h2d.Object> CreateHead;                         // static CreateLobbyHead (IL 98654)
    public static Action<dc.h2d.Object> Silhouette;                                     // static ApplyLobbyBeheadedSilhouette (IL 97481)
    public static Func<HSprite,int> AnimCursor;                                         // static ReadLobbyAnimCursor (IL 97776)
    public static Action<HSprite,string> IdleLoop;                                      // static EnsureLobbyBodyIdleAnim: play(g).loop(), genSpeed 0.4 (IL 97830-97859)
    // LobbyHeadFx: private static List<Emitter> Active; Emitter.Tick(double) / Dispose() / Root (IL 102994-103092)
    public static FieldInfo FxActive,EmRoot;public static MethodInfo EmTick,EmDispose;
    // LobbySession
    public static MethodInfo ShowHost,ShowClient,FrameUpdate;
    public static FieldInfo InHost,InClient,RebuildDepth,Transport;
    public static int TransportSteam;
    public static Func<string> LobbyCode;public static Func<bool> CopyCode;
    public static Action<string,double,double> Toast;
    public static Action<dc.h2d.Graphics,double,double,double,double,double,int,double> RoundRect;   // UiChrome.FillRoundRect (IL 111138)
    // proxy members with Nullable<T> parameters from the runtime CoreLib: reflection only (SPEC hard rule)
    public static MethodInfo Play;                           // AnimManager.play(String, Nullable<int>, Nullable<bool>) findex 1182
    static PropertyInfo _makeText;static MethodInfo _makeTextInvoke;   // _Assets.makeText : HlFunc<ui.Text,String,int?,bool?,h2d.Object>
    // h2d.Text has TWO methods "int get_textWidth()" with the same signature: the raw field accessor of the physical
    // field of the Haxe property textWidth(get,null) and the Haxe getter (updateSize; return calcWidth). A compiled
    // call binds by name+signature to the first one, the accessor (notes\ui\calls.txt), so the getters are bound here by
    // MethodInfo with their pinned function index (GameProxy of the pinned hlboot.dat).
    public static Func<dc.h2d.Text,int> TextWidth,TextHeight;
    const int TextWidthFIndex=3389,TextHeightFIndex=3388;

    public static void Bind()
    {
        var ui=typeof(ConnectionUI);
        LiveUi=LobbyCore.Delegate<Func<ConnectionUI>>(LobbyCore.Method(ui,"TryGetLiveInstance",true,ui));
        PanelRoot=LobbyCore.Field(ui,"_panelRoot",typeof(dc.h2d.Object),false);
        PromptRoot=LobbyCore.Field(ui,"_promptRoot",typeof(dc.h2d.Object),false);
        KeepLobby=LobbyCore.Field(ui,"_keepLobbyVisible",typeof(bool),false);
        Mode=LobbyCore.Field(ui,"_mode",null,false);
        var mt=Mode.FieldType;
        if(!mt.IsEnum||mt.Name!="UiMode"||Enum.GetUnderlyingType(mt)!=typeof(int)||Convert.ToInt32(Enum.Parse(mt,"Menu"))!=1||Convert.ToInt32(Enum.Parse(mt,"Lobby"))!=0)
            throw new InvalidOperationException("Pinned field mismatch: ConnectionUI._mode");
        ModeMenu=1;
        var pb=ui.GetNestedType("PendingButton",BindingFlags.NonPublic|BindingFlags.Public)??throw new MissingMemberException("ConnectionUI","PendingButton");
        var pi=ui.GetNestedType("PendingInfo",BindingFlags.NonPublic|BindingFlags.Public)??throw new MissingMemberException("ConnectionUI","PendingInfo");
        PendingButtons=LobbyCore.Field(ui,"PendingButtons",typeof(List<>).MakeGenericType(pb),true);
        PendingInfos=LobbyCore.Field(ui,"PendingInfos",typeof(List<>).MakeGenericType(pi),true);
        BLabel=LobbyCore.Field(pb,"Label",typeof(string),false);BHelp=LobbyCore.Field(pb,"Help",typeof(string),false);
        BEnabled=LobbyCore.Field(pb,"Enabled",typeof(bool),false);BColor=LobbyCore.Field(pb,"Color",typeof(int),false);
        LobbyCore.Field(pb,"FieldStyle",typeof(bool),false);
        BOnClick=LobbyCore.Field(pb,"OnClick",typeof(Action),false);
        IText=LobbyCore.Field(pi,"Text",typeof(string),false);LobbyCore.Field(pi,"Color",typeof(int),false);
        CreateBody=LobbyCore.Delegate<Func<ConnectionUI,string,int,double,HSprite>>(LobbyCore.Method(ui,"CreateLobbyBeheaded",false,typeof(HSprite),typeof(string),typeof(int),typeof(double)));
        ApplySkin=LobbyCore.Delegate<Action<ConnectionUI,HSprite,string,string,int,string>>(LobbyCore.Method(ui,"ApplyLobbyBeheadedSkin",false,typeof(void),typeof(HSprite),typeof(string),typeof(string),typeof(int),typeof(string)));
        PlaceHead=LobbyCore.Delegate<Action<ConnectionUI,dc.h2d.Object,HSprite,string,double,string>>(LobbyCore.Method(ui,"PositionLobbyHeadOnBone",false,typeof(void),typeof(dc.h2d.Object),typeof(HSprite),typeof(string),typeof(double),typeof(string)));
        CreateHead=LobbyCore.Delegate<Func<string,double,dc.h2d.Object>>(LobbyCore.Method(ui,"CreateLobbyHead",true,typeof(dc.h2d.Object),typeof(string),typeof(double)));
        Silhouette=LobbyCore.Delegate<Action<dc.h2d.Object>>(LobbyCore.Method(ui,"ApplyLobbyBeheadedSilhouette",true,typeof(void),typeof(dc.h2d.Object)));
        AnimCursor=LobbyCore.Delegate<Func<HSprite,int>>(LobbyCore.Method(ui,"ReadLobbyAnimCursor",true,typeof(int),typeof(HSprite)));
        IdleLoop=LobbyCore.Delegate<Action<HSprite,string>>(LobbyCore.Method(ui,"EnsureLobbyBodyIdleAnim",true,typeof(void),typeof(HSprite),typeof(string)));

        var fx=LobbyCore.ModType("DeadCellsMultiplayerMod.MultiplayerModUI.Connection.LobbyHeadFx");
        var em=fx.GetNestedType("Emitter",BindingFlags.NonPublic|BindingFlags.Public)??throw new MissingMemberException("LobbyHeadFx","Emitter");
        FxActive=LobbyCore.Field(fx,"Active",typeof(List<>).MakeGenericType(em),true);
        EmRoot=LobbyCore.Field(em,"Root",typeof(dc.h2d.Object),false);
        EmTick=LobbyCore.Method(em,"Tick",false,typeof(void),typeof(double));
        EmDispose=LobbyCore.Method(em,"Dispose",false,typeof(void));

        var lobby=LobbyCore.ModType("DeadCellsMultiplayerMod.LobbySession");
        ShowHost=LobbyCore.Method(lobby,"ShowHostStatusMenu",true,typeof(void),typeof(dc.pr.TitleScreen));
        ShowClient=LobbyCore.Method(lobby,"ShowClientWaitingMenu",true,typeof(void),typeof(dc.pr.TitleScreen));
        // the snapshot's InHostStatusMenu / InClientWaitingMenu are these two statics read under the lock
        // (ReadSessionSnapshot IL 77863-77864); they are written on the main thread only (IL 71811-71813, 71993-71995)
        InHost=LobbyCore.Field(lobby,"_inHostStatusMenu",typeof(bool),true);
        InClient=LobbyCore.Field(lobby,"_inClientWaitingMenu",typeof(bool),true);
        RebuildDepth=LobbyCore.Field(lobby,"_menuRebuildDepth",typeof(int),true);
        Transport=LobbyCore.Field(lobby,"_menuTransport",null,true);
        if(!Transport.FieldType.IsEnum)throw new InvalidOperationException("Pinned field mismatch: LobbySession._menuTransport");
        TransportSteam=Convert.ToInt32(Enum.Parse(Transport.FieldType,"Steam"));
        if(TransportSteam!=1)throw new InvalidOperationException("Pinned field mismatch: ConnectionTransport.Steam");
        LobbyCode=LobbyCore.Delegate<Func<string>>(LobbyCore.Method(lobby,"GetSteamLobbyCodeForUi",true,typeof(string)));
        CopyCode=LobbyCore.Delegate<Func<bool>>(LobbyCore.Method(lobby,"TryCopySteamLobbyCodeFromUi",true,typeof(bool)));
        FrameUpdate=LobbyCore.Method(typeof(ModEntry),"OnFrameUpdate",false,typeof(void),typeof(double));
        var mui=LobbyCore.ModType("DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI.MultiplayerUI");
        Toast=LobbyCore.Delegate<Action<string,double,double>>(LobbyCore.Method(mui,"PushSystemMessage",true,typeof(void),typeof(string),typeof(double),typeof(double)));
        var chrome=LobbyCore.ModType("DeadCellsMultiplayerMod.Tools.UiChrome");
        RoundRect=LobbyCore.Delegate<Action<dc.h2d.Graphics,double,double,double,double,double,int,double>>(LobbyCore.Method(chrome,"FillRoundRect",true,typeof(void),
            typeof(dc.h2d.Graphics),typeof(double),typeof(double),typeof(double),typeof(double),typeof(double),typeof(int),typeof(double)));

        Play=typeof(AnimManager).GetMethod("play",BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly)??throw new MissingMethodException("AnimManager","play");
        var pp=Play.GetParameters();
        if(Play.ReturnType!=typeof(AnimManager)||pp.Length!=3||pp[0].ParameterType!=typeof(dc.String)||Nullable.GetUnderlyingType(pp[1].ParameterType)!=typeof(int)||Nullable.GetUnderlyingType(pp[2].ParameterType)!=typeof(bool))
            throw new InvalidOperationException("Pinned method mismatch: AnimManager.play");
        _makeText=typeof(dc._Assets).GetProperty("makeText",BindingFlags.Public|BindingFlags.Instance)??throw new MissingMemberException("Assets","makeText");
        _makeTextInvoke=_makeText.PropertyType.GetMethod("Invoke")??throw new MissingMethodException("makeText","Invoke");
        var mp=_makeTextInvoke.GetParameters();
        if(_makeTextInvoke.ReturnType!=typeof(dc.ui.Text)||mp.Length!=4||mp[0].ParameterType!=typeof(dc.String)||Nullable.GetUnderlyingType(mp[1].ParameterType)!=typeof(int)||
            Nullable.GetUnderlyingType(mp[2].ParameterType)!=typeof(bool)||mp[3].ParameterType!=typeof(dc.h2d.Object))
            throw new InvalidOperationException("Pinned method mismatch: Assets.makeText");
        TextWidth=HaxeGetter("get_textWidth",TextWidthFIndex);
        TextHeight=HaxeGetter("get_textHeight",TextHeightFIndex);
    }
    static Func<dc.h2d.Text,int> HaxeGetter(string name,int findex)
    {
        MethodInfo found=null;
        foreach(var m in typeof(dc.h2d.Text).GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly))
        {
            if(m.Name!=name||m.IsSpecialName||m.ReturnType!=typeof(int)||m.GetParameters().Length!=0||FIndex(m)!=findex)continue;
            if(found!=null)throw new InvalidOperationException("Pinned method mismatch: h2d.Text."+name+" (ambiguous)");
            found=m;
        }
        if(found==null)throw new InvalidOperationException("Pinned method mismatch: h2d.Text."+name+" findex "+findex);
        return (Func<dc.h2d.Text,int>)System.Delegate.CreateDelegate(typeof(Func<dc.h2d.Text,int>),found);
    }
    static int FIndex(MethodInfo m)
    {
        foreach(var a in m.CustomAttributes)
            if(a.AttributeType.Name=="HashlinkFIndexAttribute"&&a.ConstructorArguments.Count==1)return Convert.ToInt32(a.ConstructorArguments[0].Value);
        return -1;
    }

    // Assets.makeText(text, color, big=false, parent) exactly as the mod calls it (IL 94701-94718); the boxed int and
    // bool are accepted for the Nullable<T> parameters.
    public static dc.ui.Text MakeText(string text,int color,dc.h2d.Object parent)
    {
        var fn=_makeText.GetValue(dc.Assets.Class)??throw new InvalidOperationException("Assets.makeText unavailable");
        return (dc.ui.Text)_makeTextInvoke.Invoke(fn,new object[]{(text??"").AsHaxeString(),color,false,parent});
    }
    public static AnimManager PlayOnce(AnimManager anim,string group)=>(AnimManager)Play.Invoke(anim,new object[]{group.AsHaxeString(),null,null});

    public static ConnectionUI Live(){try{return LiveUi();}catch{return null;}}
    // The string the mod gets for one of its own msgids: it labels the lobby exit buttons with
    // GetText.Instance.GetString("Back") / ("Disconnect") (IL 71778-71780, 71960-71962). null when unavailable.
    public static string ModString(string msgid)
    {
        try{var s=ModCore.Modules.GetText.Instance.GetString(msgid);return string.IsNullOrEmpty(s)?null:s;}
        catch{return null;}
    }
    // Lobby gate (brief 4.2, confirmed in IL): CommitMenu(showLobby:true) sets _mode = Menu and _keepLobbyVisible = true
    // (IL 93729-93734) and the two lobby screens set their static flag right before it (IL 71811, 71993); every other
    // screen commits with showLobby:false or clears the flags. The Steam join "resolving" view is ShowLobbyMode:
    // _mode = Lobby, _keepLobbyVisible = false (IL 93769-93776), so it stays original.
    public static bool GateNow(ConnectionUI ui)
    {
        if(ui==null)return false;
        if(!(bool)KeepLobby.GetValue(ui))return false;
        if(Convert.ToInt32(Mode.GetValue(ui))!=ModeMenu)return false;
        return (bool)InHost.GetValue(null)||(bool)InClient.GetValue(null);
    }
    public static bool HostScreen=>(bool)InHost.GetValue(null);
    public static dc.h2d.Object Panel(ConnectionUI ui)=>ui==null?null:PanelRoot.GetValue(ui) as dc.h2d.Object;
    public static bool Steam=>Convert.ToInt32(Transport.GetValue(null))==TransportSteam;

    // Emitters CreateLobbyHead registered for this head in the global LobbyHeadFx.Active (AttachDefaultHeadFx adds
    // new Emitter(root = the head object) at IL 103014-103022). The hidden original calls LobbyHeadFx.ClearAll on
    // every rebuild (IL 96913 -> 103070-103091), which would dispose ours about once a second (brief 4.4): take them
    // out right after creation and tick / dispose them ourselves (option a).
    public static void TakeEmitters(int before,dc.h2d.Object head,List<object> into)
    {
        if(head==null||!(FxActive.GetValue(null) is IList active))return;
        for(int i=active.Count-1;i>=before&&i>=0;i--)
        {
            var e=active[i];
            if(e!=null&&EmRoot.GetValue(e) is dc.h2d.Object r&&r.HashlinkPointer==head.HashlinkPointer){active.RemoveAt(i);into.Add(e);}
        }
    }
    public static int ActiveCount()=>FxActive.GetValue(null) is IList a?a.Count:0;
    public static void TickEmitters(List<object> list,double dt){if(list.Count==0)return;var args=new object[]{dt};foreach(var e in list)EmTick.Invoke(e,args);}
    public static void DisposeEmitters(List<object> list){foreach(var e in list)try{EmDispose.Invoke(e,null);}catch{}list.Clear();}
}
