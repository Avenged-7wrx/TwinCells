using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

// Pins of CoopBranchVisibilityFix (PLAN-ALPHA2.md section 4 rules 1-2).
// Base mod: the release DLL 378BE6B0 by file hash, and every patched or invoked method by IL length + SHA-256 of its body
// (Get-BaseIlPins.ps1 reproduces the values from the audit copy's DLL; the lengths equal the release IL dump headers).
// GameProxy: symbolic only, never the raw file hash (issue 14): each proxy member this part reads or calls is checked by its
// generated shape against the loaded proxy: a field getter must read the named Haxe field, a method must bind the named
// native function index (f@N of hlboot A37869A3), a constructor the named native type index. The list itself is hashed
// into SymbolicPin so a changed list is visible in the log and the manifest.
public static class CoopBranchVisibilityPins
{
    public const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string NativePin="A37869A3C91443B50322B2CDF29595C9644AB8FC1280342F1F17C6F616A15534";
    const BindingFlags S=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
    const BindingFlags I=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;

    // Patched (postfix / transpiler) and invoked base-mod methods. Release IL dump line of each in the comment.
    public const int ComputeLevelBranchTokenIl=229;      public const string ComputeLevelBranchTokenSha="49041AB3D7417EE3F9CCDCFBD92FCCAE495854A81FD2FD9D290FA3FDDD66673F"; // IL:36748
    public const int ShouldKeepVisibleIl=73;              public const string ShouldKeepVisibleSha="8FF44CB61FBED583B19C0D442F6D5965498F902D67B27387A858C61986BB4A4F";      // IL:38061
    public const int ApplyDownedIl=779;                   public const string ApplyDownedSha="B1E2F65D6CD094018972CE6BDA246D1D1BBC9819D742606B73D33685A5E50DCE";             // IL:22046
    public const int FindReviveIl=361;                    public const string FindReviveSha="0905A6AB51495DF86BB4C1A75BA16F96450D3FED23AFD0F2B34B170DECC22D42";              // IL:25126
    public const int KingHeadFxIl=322;                    public const string KingHeadFxSha="C26C29447BF7F8893F0381E366C059D3FBFA5EE72A75BA404A547872FCD625A1";              // IL:155203
    public const int OnFrameUpdateIl=270;                 public const string OnFrameUpdateSha="2246501C5B61327925F02715B4034722C6E9648434F7111AB114EBFED22717B3";          // IL:34916
    public const int QueueDisposeIl=163;                  public const string QueueDisposeSha="1447BA24D69CA5E3BF92365FE114CDAC9F163B4A618F7676F82FAC505ACE5465";            // IL:38160
    public const int TryGetClientIndexIl=51;              public const string TryGetClientIndexSha="A38BBF6F6139A9F953F4D7D39A0C3AA9619D4A3C642809843FC29BA84161437E";      // IL:36986

    // name -> (kind, expected): F = field getter of that Haxe field, N = native function index, T = native type index (ctor).
    public static readonly (Type Owner,string Member,char Kind,string Expected)[] Symbolic=
    {
        (typeof(dc.pr.Level),"get_isSubLevel",'F',"isSubLevel"),(typeof(dc.pr.Level),"get_isSecret",'F',"isSecret"),
        (typeof(dc.pr.Level),"get_map",'F',"map"),(typeof(dc.pr.Level),"get___uid",'F',"__uid"),
        (typeof(dc.level.LevelMap),"get_id",'F',"id"),(typeof(dc.level.LevelMap),"get_seed",'F',"seed"),
        (typeof(dc.level.LevelMap),"get_wid",'F',"wid"),(typeof(dc.level.LevelMap),"get_hei",'F',"hei"),
        (typeof(dc.level.LevelMap),"get_rooms",'F',"rooms"),
        (typeof(dc.level.Room),"get_isZChild",'F',"isZChild"),(typeof(dc.level.Room),"get_zLinks",'F',"zLinks"),
        (typeof(dc.ui.hud.MiniMap),"get_trackers",'F',"trackers"),(typeof(dc.ui.hud.MiniMap),"get_isFullscreen",'F',"isFullscreen"),
        (typeof(dc.ui.hud.MiniMap),"get_baseScale",'F',"baseScale"),
        (typeof(dc.ui.hud.MMTracker),"get_e",'F',"e"),(typeof(dc.ui.hud.MMTracker),"get_dot",'F',"dot"),(typeof(dc.ui.hud.MMTracker),"get_icons",'F',"icons"),
        (typeof(dc.h2d.BatchElement),"get_t",'F',"t"),(typeof(dc.h2d.BatchElement),"get_batch",'F',"batch"),
        (typeof(dc.h2d.BatchElement),"remove",'N',"1162"),(typeof(dc.h2d.BatchElement),".ctor",'T',"308"),
        (typeof(dc.h2d.SpriteBatch),"add",'N',"1412"),
        (typeof(dc.tool.HeroHead),"get_headNormalSb",'F',"headNormalSb"),(typeof(dc.tool.HeroHead),"get_headAddSb",'F',"headAddSb"),
        (typeof(dc.Entity),"get_cx",'F',"cx"),(typeof(dc.Entity),"get_cy",'F',"cy"),(typeof(dc.Entity),"get_visible",'F',"visible"),
        (typeof(dc.Entity),"get_destroyed",'F',"destroyed"),(typeof(dc.Entity),"get__level",'F',"_level"),
    };
    public static string SymbolicPin=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
        Symbolic.Select(s=>s.Owner.FullName+"::"+s.Member+"|"+s.Kind+"|"+s.Expected).OrderBy(x=>x,StringComparer.Ordinal)))));

    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);
    public static void RequireExactBinaries(Assembly original,Assembly proxy)
    {
        if(original.GetName().Version!=new Version(0,5,1,0)||Hash(original.Location)!=BasePin||
            Hash(Path.Combine(Path.GetDirectoryName(proxy.Location),"hlboot.dat"))!=NativePin)
            throw new InvalidOperationException("CoopBranchVisibilityFix requires exact community0.5.1/native35.13.5");
    }
    public static MethodInfo Base(Type owner,string name,bool isStatic,Type[] args,Type ret,int length,string sha)
    {
        var m=owner.GetMethod(name,(isStatic?S:I)|BindingFlags.DeclaredOnly,null,args,null);
        if(m==null||m.ReturnType!=ret)throw new InvalidOperationException("Pinned base method missing: "+owner.Name+"."+name);
        var il=m.GetMethodBody()?.GetILAsByteArray();
        if(il==null||il.Length!=length||Convert.ToHexString(SHA256.HashData(il))!=sha)throw new InvalidOperationException("Pinned base body changed: "+owner.Name+"."+name);
        return m;
    }
    public static FieldInfo BaseField(Type owner,string name,Type type,bool isStatic)
    {
        var f=owner.GetField(name,(isStatic?S:I)|BindingFlags.DeclaredOnly);
        if(f==null||(type!=null&&f.FieldType!=type))throw new InvalidOperationException("Pinned base field mismatch: "+owner.Name+"."+name);
        return f;
    }

    // ---- generated proxy shapes (HaxeProxy generator of GameProxy 2A1803BA; any regenerated proxy of the same game) ----
    const byte OpLdArg0=0x02,OpLdStr=0x72,OpLdcI4=0x20,OpLdSFldA=0x7F,OpCall=0x28,OpRet=0x2A;
    static int Int32At(byte[] il,int at)=>il[at]|(il[at+1]<<8)|(il[at+2]<<16)|(il[at+3]<<24);
    public static void VerifySymbolic()
    {
        foreach(var (owner,member,kind,expected) in Symbolic)
        {
            if(kind=='T')
            {
                var ctor=owner.GetConstructors(I).SingleOrDefault(c=>c.GetParameters().Length==1&&c.GetParameters()[0].ParameterType==typeof(dc.h2d.Tile))??throw new InvalidOperationException("Proxy ctor missing: "+owner.Name);
                var il=ctor.GetMethodBody()?.GetILAsByteArray();
                if(il==null||il.Length<6||il[0]!=OpLdArg0||il[1]!=OpLdcI4||Int32At(il,2).ToString()!=expected)throw new InvalidOperationException("Proxy ctor changed: "+owner.Name);
                continue;
            }
            var candidates=owner.GetMethods(I).Where(m=>m.Name==member&&m.DeclaringType==owner).ToArray();
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
