using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

// Pins of CoopTransitionGate (PLAN-ALPHA2.md section 4 rules 1-2). Base mod: release DLL 378BE6B0 and every patched or
// invoked method by IL length + SHA-256 of its body (Get-BaseIlPins.ps1 of this folder). GameProxy: symbolic only (generated
// shape against the loaded proxy: hook add_ / native method bind the named native function index of hlboot A37869A3, field
// getters read the named Haxe field); never the raw GameProxy hash (issue 14).
public static class CoopTransitionGatePins
{
    public const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string NativePin="A37869A3C91443B50322B2CDF29595C9644AB8FC1280342F1F17C6F616A15534";
    const BindingFlags S=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
    const BindingFlags I=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;

    // patched
    public const int HandleLineIl=11632;     public const string HandleLineSha="DDBA7D561C9AFE056538D8473CE182DC1F9D3D6B0081A181F3FCAF087E9EEA3F";     // IL:9853 (prefix)
    public const int OnFrameUpdateIl=270;    public const string OnFrameUpdateSha="2246501C5B61327925F02715B4034722C6E9648434F7111AB114EBFED22717B3";  // IL:34916 (postfix)
    // invoked
    public const int RemoteDownedIl=83;      public const string RemoteDownedSha="089B2FFDA491DBD062BA0E81761D1FF35B85B1A93CF3D7A82EEF47B7964B95D6";   // IL:43371
    public const int ClientIndexIl=51;       public const string ClientIndexSha="A38BBF6F6139A9F953F4D7D39A0C3AA9619D4A3C642809843FC29BA84161437E";    // IL:36986
    public const int ClientLabelIl=37;       public const string ClientLabelSha="D00FA7ABD430FD5155A21FC2FD84EFFB483B150B188249FA82462486BBED99B6";    // IL:32863
    public const int TryGetRemoteIl=131;     public const string TryGetRemoteSha="374A04C8DF1BAC2D98E9AF97DC753F7681F0CAD7B90847A3E49D57646D97C896";   // IL:2588
    public const int RemotePositionIl=124;   public const string RemotePositionSha="1F10708E9589822FB6C5310CFBCECE7439F4A84903BBB37F1A50ADFDA399AF39"; // IL:2648
    public const int SendLineIl=178;         public const string SendLineSha="8AE4B5C5660FAF4ED37DBA938CE4ABB68302852FBC3B0638BFF98ED04C954C2A";      // IL:18608
    public const int SendClientIl=20;        public const string SendClientSha="B98304179E2A3D59E56F3D50FF7687735159DBB7AF51F12137CC1B3FCF739236";    // IL:20411
    public const int SendSteamIl=182;        public const string SendSteamSha="A8B948810E33CE56A30FA17F6CAB62AD3C45B395322AA038E56BAF2F6B6F2803";     // IL:18741
    public const int InActualRunIl=14;       public const string InActualRunSha="9163568D5920EEAD88209D8F9B938EA666E82A5D266A37FE3A64F6AFDFB89A1C";    // IL:75239
    public const int PushMessageIl=117;      public const string PushMessageSha="94BBE0BCEE2A1014093A1B88859A6021960C8D587C895974BF935340CA118A4F";    // IL:87617

    public static readonly (Type Owner,string Member,char Kind,string Expected)[] Symbolic=
    {
        (typeof(dc.en.inter.npc.Hook_PerkMaster),"add_setDoor",'N',"26022"),(typeof(dc.en.inter.npc.Hook_PerkMaster),"remove_setDoor",'N',"26022"),
        (typeof(dc.en.inter.npc.Hook_CollectorPanelNpc),"add_setDoor",'N',"14449"),(typeof(dc.en.inter.npc.Hook_CollectorPanelNpc),"remove_setDoor",'N',"14449"),
        (typeof(dc.en.inter.npc.PerkMaster),"setDoor",'N',"26022"),(typeof(dc.en.inter.npc.CollectorPanelNpc),"setDoor",'N',"14449"),
        (typeof(dc.level.LevelMap),"getRoomAt",'N',"3179"),(typeof(dc.level.LevelMap),"get_id",'F',"id"),
        (typeof(dc.pr.Level),"get_map",'F',"map"),(typeof(dc.pr.Level),"get_isSubLevel",'F',"isSubLevel"),(typeof(dc.pr.Level),"get_isSecret",'F',"isSecret"),
        (typeof(dc.pr.Level),"get_entities",'F',"entities"),
        (typeof(dc.Entity),"get_cx",'F',"cx"),(typeof(dc.Entity),"get_cy",'F',"cy"),(typeof(dc.Entity),"get__level",'F',"_level"),(typeof(dc.Entity),"get_destroyed",'F',"destroyed"),
        (typeof(dc.en.inter.Door),"get__closed",'F',"_closed"),(typeof(dc.en.inter.Door),"get_broken",'F',"broken"),
    };
    public static string SymbolicPin=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
        Symbolic.Select(s=>s.Owner.FullName+"::"+s.Member+"|"+s.Kind+"|"+s.Expected).OrderBy(x=>x,StringComparer.Ordinal)))));

    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);
    public static void RequireExactBinaries(Assembly original,Assembly proxy)
    {
        if(original.GetName().Version!=new Version(0,5,1,0)||Hash(original.Location)!=BasePin||
            Hash(Path.Combine(Path.GetDirectoryName(proxy.Location),"hlboot.dat"))!=NativePin)
            throw new InvalidOperationException("CoopTransitionGate requires exact community0.5.1/native35.13.5");
    }
    public static MethodInfo Base(Type owner,string name,bool isStatic,Type[] args,Type ret,int length,string sha)
    {
        var m=owner.GetMethod(name,(isStatic?S:I)|BindingFlags.DeclaredOnly,null,args,null);
        if(m==null||(ret!=null&&m.ReturnType!=ret))throw new InvalidOperationException("Pinned base method missing: "+owner.Name+"."+name);
        var il=m.GetMethodBody()?.GetILAsByteArray();
        if(il==null||il.Length!=length||Convert.ToHexString(SHA256.HashData(il))!=sha)throw new InvalidOperationException("Pinned base body changed: "+owner.Name+"."+name);
        return m;
    }
    const byte OpLdArg0=0x02,OpLdStr=0x72,OpLdcI4=0x20,OpLdSFldA=0x7F,OpCall=0x28,OpRet=0x2A;
    static int Int32At(byte[] il,int at)=>il[at]|(il[at+1]<<8)|(il[at+2]<<16)|(il[at+3]<<24);
    public static void VerifySymbolic()
    {
        foreach(var (owner,member,kind,expected) in Symbolic)
        {
            var candidates=owner.GetMethods(S|I).Where(m=>m.Name==member&&m.DeclaringType==owner).ToArray();
            if(candidates.Length==0)throw new InvalidOperationException("Proxy member missing: "+owner.Name+"."+member);
            bool ok=false;
            foreach(var m in candidates)
            {
                var il=m.GetMethodBody()?.GetILAsByteArray();if(il==null)continue;
                if(kind=='F')ok|=il.Length==17&&il[0]==OpLdArg0&&il[1]==OpLdStr&&il[6]==OpLdSFldA&&il[11]==OpCall&&il[16]==OpRet&&m.Module.ResolveString(Int32At(il,2))==expected;
                else if(kind=='N')ok|=il.Length>=5&&il[0]==OpLdcI4&&Int32At(il,1).ToString()==expected;
            }
            if(!ok)throw new InvalidOperationException("Proxy member changed: "+owner.Name+"."+member+" (expected "+kind+":"+expected+")");
        }
    }
}
