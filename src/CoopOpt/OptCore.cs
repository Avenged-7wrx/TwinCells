using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using DeadCellsMultiplayerMod;

// Shared plumbing of the CoopOpt add-on (experiments/opt-20261111); copy of LobbyCore, will merge into CoopPack.
// Pins, kill switches, reflection helpers with exact-signature checks, logging, and a per-part fault gate.
public static class OptCore
{
    public const string BasePin="378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string NativePin="A37869A3C91443B50322B2CDF29595C9644AB8FC1280342F1F17C6F616A15534";
    public const BindingFlags S=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.DeclaredOnly;
    public const BindingFlags I=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly;
    public static readonly Assembly Mod=typeof(ModEntry).Assembly;

    static string _pinError;static bool _pinsChecked;
    // Throws unless the community mod is exactly 0.5.1 (release SHA) and the native game is the pinned GL build.
    public static void CheckPins()
    {
        if(_pinsChecked){if(_pinError!=null)throw new InvalidOperationException(_pinError);return;}
        _pinsChecked=true;
        try
        {
            if(Mod.GetName().Version!=new Version(0,5,1,0)||Hash(Mod.Location)!=BasePin)throw new InvalidOperationException("CoopOpt requires DeadCellsMultiplayerMod 0.5.1 (pinned release)");
            var boot=Path.Combine(Path.GetDirectoryName(typeof(dc.en.Hero).Assembly.Location)??"","hlboot.dat");
            if(Hash(boot)!=NativePin)throw new InvalidOperationException("CoopOpt requires the pinned native game build");
        }
        catch(Exception e){_pinError=e.Message;throw;}
    }
    static string Hash(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);

    // COOP_OPT_DISABLE=1 switches everything off; COOP_OPT_<PART>_DISABLE=1 one part (HOST, CLIENT, MINIMAP).
    public static bool Off(string part)=>Environment.GetEnvironmentVariable("COOP_OPT_DISABLE")=="1"||Environment.GetEnvironmentVariable("COOP_OPT_"+part+"_DISABLE")=="1";

    public static Type ModType(string fullName)=>Mod.GetType(fullName,true);
    public static FieldInfo Field(Type type,string name,Type fieldType,bool isStatic)
    {
        var f=type.GetField(name,isStatic?S:I);
        if(f==null||(fieldType!=null&&f.FieldType!=fieldType))throw new InvalidOperationException("Pinned field mismatch: "+type.Name+"."+name);
        return f;
    }
    // Exact signature: static-ness, return type, parameter types. Pass ret=null to skip the return check.
    public static MethodInfo Method(Type type,string name,bool isStatic,Type ret,params Type[] args)
    {
        var m=type.GetMethod(name,isStatic?S:I,null,args,null);
        if(m==null||m.IsStatic!=isStatic||(ret!=null&&m.ReturnType!=ret))throw new InvalidOperationException("Pinned method mismatch: "+type.Name+"."+name);
        return m;
    }
    public static T Delegate<T>(MethodInfo m) where T:Delegate=>(T)System.Delegate.CreateDelegate(typeof(T),m);

    public static void Info(string template,params object[] args){try{Serilog.Log.Information("[CoopOpt] "+template,args);}catch{}}
    public static void Warn(string template,params object[] args){try{Serilog.Log.Warning("[CoopOpt] "+template,args);}catch{}}
}

// One per part. Fail() returns true when the part must switch itself off for the session
// (8 consecutive faults, or 64 in total). The first fault and the trip are logged.
public sealed class OptFaultGate
{
    readonly string _name;int _consecutive;long _total;
    public volatile bool Tripped;
    public OptFaultGate(string name){_name=name;}
    public long Faults=>Interlocked.Read(ref _total);
    public void Ok(){_consecutive=0;}
    public bool Fail(Exception e)
    {
        long n=Interlocked.Increment(ref _total);
        if(++_consecutive>=8||n>=64)Tripped=true;
        if(n==1||Tripped)OptCore.Warn("{Part} fault{Off}: {Error}",_name,Tripped?"; part switched off for this session":"",e.GetBaseException().Message);
        return Tripped;
    }
}
