using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

// Pins of CoopPartyHud (PLAN-ALPHA2.md section 4 rules 1-2). Base mod: release DLL 378BE6B0 and every patched, relied-on or
// invoked method by IL length + SHA-256 of its body (Get-BaseIlPins.ps1 of this folder; release IL dump line numbers in the
// comments). GameProxy: symbolic only (every GameProxy member the compiled part references, checked against the loaded
// proxy: native methods and class globals bind the named hlboot function / global index of A37869A3, constructors create
// the named type index, field accessors read or write the named Haxe field); never the raw GameProxy hash (issue 14).
// Test-Pins.ps1 proves offline that this list equals the set of GameProxy members the built DLL references and that it
// holds on both generated proxy images (audit 2A1803BA and fresh 928168A6).
public static class CoopPartyHudPins
{
    public const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string NativePin="A37869A3C91443B50322B2CDF29595C9644AB8FC1280342F1F17C6F616A15534";
    const BindingFlags S=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
    const BindingFlags I=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;

    // patched
    public const int InitPlateIl=892;        public const string InitPlateSha="9CA998D411DAA5A70AF5F3F3C314024A2133C1DB8CFE15EBD2CC37A7131AE417";       // MultiplayerUI.initkingLife IL:86638 (postfix)
    public const int SlotLabelIl=112;        public const string SlotLabelSha="098703A13D048A88E9EF22802DAA07DB6495C4B42E6E69F7C88E15307B85E18E";       // MultiplayerUI.UpdateSlotLabel IL:86984 (prefix, never skips)
    public const int LifeBarIl=354;          public const string LifeBarSha="468DDD0D1A8C6F78BB058CEE9BFD6361426CA02C2CE3233A113F71CBF5055CAF";         // MultiplayerUI.UpdateLifeBar IL:87020 (postfix)
    public const int HandleLineIl=11632;     public const string HandleLineSha="DDBA7D561C9AFE056538D8473CE182DC1F9D3D6B0081A181F3FCAF087E9EEA3F";      // NetNode.HandleLine IL:9853 (prefix)
    public const int OnFrameUpdateIl=270;    public const string OnFrameUpdateSha="2246501C5B61327925F02715B4034722C6E9648434F7111AB114EBFED22717B3";   // ModEntry.OnFrameUpdate IL:34916 (postfix)
    // relied on (not patched): plate geometry and call order
    public const int DrawChipIl=1457;        public const string DrawChipSha="0BE9CF68F25902CAC29FF15202796E498A20D317B3E629E8518C63C3E06017E1";        // MultiplayerUI.DrawPartyChip IL:87159
    public const int KingLifeIl=706;         public const string KingLifeSha="2BF43233FB53B1B6E83F587086CC5325CB4084A9DE473BC140EF740213782283";        // MultiplayerUI.KingLifeUpdate IL:86387
    public const int SlotIndexIl=7;          public const string SlotIndexSha="25043936898329C5AA0BE2BC9F734B6D0F77570F23343C8CEDD0996A66121FCE";       // LifeSlot.get_SlotIndex
    public const int ContainerIl=7;          public const string ContainerSha="B6738A4D4E4F7C94D8E626DA259FDB2BD943DFB283FB562505D301F332A3E04A";       // LifeSlot.get_Container
    public const int LabelBoxIl=7;           public const string LabelBoxSha="B8F4942336874CA5A7386085BB741AC466F7931256B6936A8ABC50A3C39F3CA8";        // LifeSlot.get_LabelBox
    public const int ChipIl=7;               public const string ChipSha="8D3AC88EC69D32BA5C1780A51EB00F9735AA2A74F3067E03B71178D18C977AAF";            // LifeSlot.get_ChipGraphics
    public const int LabelTextIl=7;          public const string LabelTextSha="5608B4F113C058CF912CA12FD4ED0606B026EF5BAF03EF1B18E814774B5B87C2";       // LifeSlot.get_LabelText
    public const int StatusTextIl=7;         public const string StatusTextSha="A3D6E74D3710E66184CF1B68D1FA3BB122A5A399DA083268B77B597AB95B1823";      // LifeSlot.get_StatusText
    // invoked
    public const int ClientIndexIl=51;       public const string ClientIndexSha="A38BBF6F6139A9F953F4D7D39A0C3AA9619D4A3C642809843FC29BA84161437E";     // ModEntry.TryGetClientIndex IL:36986
    public const int InActualRunIl=14;       public const string InActualRunSha="9163568D5920EEAD88209D8F9B938EA666E82A5D266A37FE3A64F6AFDFB89A1C";     // LobbySession.IsInActualRun IL:75239
    public const int SendLineIl=178;         public const string SendLineSha="8AE4B5C5660FAF4ED37DBA938CE4ABB68302852FBC3B0638BFF98ED04C954C2A";        // NetNode.SendLineSafe IL:18608
    public const int SendClientIl=20;        public const string SendClientSha="B98304179E2A3D59E56F3D50FF7687735159DBB7AF51F12137CC1B3FCF739236";      // NetNode.SendLineToClientSafe IL:20411
    public const int SendSteamIl=182;        public const string SendSteamSha="A8B948810E33CE56A30FA17F6CAB62AD3C45B395322AA038E56BAF2F6B6F2803";       // NetNode.SendLineToSteamClientSafe IL:18741
    // h2d.Text has TWO int get_textWidth() / get_textHeight(): the raw accessor of the never-written physical field and the
    // Haxe getter (f@3389 / f@3388); the measuring code binds the Haxe getter by this function index.
    public const int TextWidthFIndex=3389,TextHeightFIndex=3388;

    // Kinds: F field getter (ldarg.0; ldstr <field>; ...), S field setter (ldarg.0; ldarg.1; ldstr <field>; ...),
    // N native method or class global (ldc.i4 <function/global index> first), C constructor (ldarg.0; ldc.i4 <type index>).
    public static readonly (Type Owner,string Member,char Kind,string Expected)[] Symbolic=
    {
        // HUD text: Assets.makeText (bound by shape, CoopPartyHudPatch.BindMakeText), ui.Text scale/resize/text, h2d.Text colour/size
        (typeof(dc.Assets),"get_Class",'N',"475"),(typeof(dc._Assets),"get_makeText",'F',"makeText"),
        (typeof(dc.ui.Text),"get_customScale",'F',"customScale"),(typeof(dc.ui.Text),"set_customScale",'S',"customScale"),(typeof(dc.ui.Text),"onResize",'N',"3413"),
        (typeof(dc.h2d.Text),"set_text",'N',"3383"),(typeof(dc.ui.Text),"set_text",'N',"3411"),   // the virtual call dispatches to the ui.Text override f@3411
        (typeof(dc.h2d.Text),"set_textColor",'N',"3392"),(typeof(dc.h2d.HtmlText),"set_textColor",'N',"3403"),   // ui.Text : HtmlText : h2d.Text, HtmlText overrides
        (typeof(dc.h2d.Text),"get_textWidth",'N',"3389"),(typeof(dc.h2d.Text),"get_textHeight",'N',"3388"),
        // scene objects and drawing
        (typeof(dc.h2d.Object),".ctor",'C',"125"),(typeof(dc.h2d.Object),"remove",'N',"1386"),(typeof(dc.h2d.Object),"set_visible",'N',"1377"),
        (typeof(dc.h2d.Object),"get_x",'F',"x"),(typeof(dc.h2d.Object),"get_y",'F',"y"),(typeof(dc.h2d.Object),"set_x",'S',"x"),(typeof(dc.h2d.Object),"set_y",'S',"y"),
        (typeof(dc.h2d.Object),"set_posChanged",'S',"posChanged"),(typeof(dc.h2d.Object),"get_scaleX",'F',"scaleX"),(typeof(dc.h2d.Object),"get_scaleY",'F',"scaleY"),
        (typeof(dc.h2d.Flow),"set_needReflow",'N',"3470"),
        (typeof(dc.h2d.Graphics),".ctor",'C',"298"),(typeof(dc.h2d.Graphics),"clear",'N',"3036"),(typeof(dc.h2d.Graphics),"beginFill",'N',"3042"),
        (typeof(dc.h2d.Graphics),"drawRect",'N',"3045"),(typeof(dc.h2d.Graphics),"endFill",'N',"3044"),
        // local hero stat levels, game stat colours
        (typeof(dc.pr.Game),"get_Class",'N',"429"),(typeof(dc.pr._Game),"get_ME",'F',"ME"),(typeof(dc.pr.Game),"get_hero",'F',"hero"),
        (typeof(dc.Entity),"get_brutalityTier",'F',"brutalityTier"),(typeof(dc.Entity),"get_tacticTier",'F',"tacticTier"),(typeof(dc.Entity),"get_survivalTier",'F',"survivalTier"),
        (typeof(dc.Main),"get_Class",'N',"458"),(typeof(dc._Main),"get_ME",'F',"ME"),(typeof(dc.Main),"get_options",'F',"options"),(typeof(dc.Options),"getTierColor",'N',"5791"),
    };
    public static string SymbolicPin=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
        Symbolic.Select(s=>s.Owner.FullName+"::"+s.Member+"|"+s.Kind+"|"+s.Expected).OrderBy(x=>x,StringComparer.Ordinal)))));

    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);
    public static void RequireExactBinaries(Assembly original,Assembly proxy)
    {
        if(original.GetName().Version!=new Version(0,5,1,0)||Hash(original.Location)!=BasePin||
            Hash(Path.Combine(Path.GetDirectoryName(proxy.Location)??"","hlboot.dat"))!=NativePin)
            throw new InvalidOperationException("CoopPartyHud requires exact community0.5.1/native35.13.5");
    }
    public static void Pin(MethodInfo m,int length,string sha)
    {
        var il=m.GetMethodBody()?.GetILAsByteArray();
        if(il==null||il.Length!=length||Convert.ToHexString(SHA256.HashData(il))!=sha)throw new InvalidOperationException("Pinned base body changed: "+m.DeclaringType?.Name+"."+m.Name);
    }
    public static MethodInfo Base(Type owner,string name,bool isStatic,Type[] args,Type ret,int length,string sha)
    {
        var m=owner.GetMethod(name,(isStatic?S:I)|BindingFlags.DeclaredOnly,null,args,null);
        if(m==null||(ret!=null&&m.ReturnType!=ret))throw new InvalidOperationException("Pinned base method missing: "+owner.Name+"."+name);
        Pin(m,length,sha);
        return m;
    }
    public static int FIndex(MethodInfo m)
    {
        foreach(var a in m.CustomAttributes)
            if(a.AttributeType.Name=="HashlinkFIndexAttribute"&&a.ConstructorArguments.Count==1)return System.Convert.ToInt32(a.ConstructorArguments[0].Value);
        return -1;
    }
    const byte OpLdArg0=0x02,OpLdArg1=0x03,OpLdStr=0x72,OpLdcI4=0x20,OpLdSFldA=0x7F,OpCall=0x28,OpRet=0x2A;
    static int Int32At(byte[] il,int at)=>il[at]|(il[at+1]<<8)|(il[at+2]<<16)|(il[at+3]<<24);
    public static bool Matches(MethodBase m,char kind,string expected)
    {
        var il=m.GetMethodBody()?.GetILAsByteArray();if(il==null)return false;
        switch(kind)
        {
            case 'F':return il.Length==17&&il[0]==OpLdArg0&&il[1]==OpLdStr&&il[6]==OpLdSFldA&&il[11]==OpCall&&il[16]==OpRet&&m.Module.ResolveString(Int32At(il,2))==expected;
            case 'S':return il.Length==18&&il[0]==OpLdArg0&&il[1]==OpLdArg1&&il[2]==OpLdStr&&il[7]==OpLdSFldA&&il[12]==OpCall&&il[17]==OpRet&&m.Module.ResolveString(Int32At(il,3))==expected;
            case 'N':return il.Length>=5&&il[0]==OpLdcI4&&Int32At(il,1).ToString()==expected;
            case 'C':return il.Length>=6&&il[0]==OpLdArg0&&il[1]==OpLdcI4&&Int32At(il,2).ToString()==expected;
            default:return false;
        }
    }
    public static void VerifySymbolic()
    {
        if(Symbolic.Length==0)throw new InvalidOperationException("CoopPartyHud symbolic GameProxy pin list is empty");
        foreach(var (owner,member,kind,expected) in Symbolic)
        {
            var candidates=member==".ctor"?owner.GetConstructors(I).Cast<MethodBase>().ToArray():owner.GetMethods(S|I).Where(m=>m.Name==member&&m.DeclaringType==owner).Cast<MethodBase>().ToArray();
            if(candidates.Length==0)throw new InvalidOperationException("Proxy member missing: "+owner.Name+"."+member);
            bool ok=false;foreach(var m in candidates)ok|=Matches(m,kind,expected);
            if(!ok)throw new InvalidOperationException("Proxy member changed: "+owner.Name+"."+member+" (expected "+kind+":"+expected+")");
        }
    }
}
