using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

// Run-time half of CoopDiagPinTable: resolves every pinned base-mod / HaxeProxy method and field and refuses to install
// unless each one is exactly the reviewed one (IL length + SHA-256, static-ness, field type). Read-only.
public static class CoopDiagPins
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance|BindingFlags.DeclaredOnly;
    public static readonly Dictionary<string,MethodBase> Methods=new(StringComparer.Ordinal);
    public static readonly Dictionary<string,FieldInfo> Fields=new(StringComparer.Ordinal);

    public static string FileSha256(string path)=>global::CoopPackRuntime.PackPins.Sha256(path);

    public static void Verify(Assembly baseMod,Assembly haxeProxy,string nativePath)
    {
        if(baseMod.GetName().Version!=new Version(0,5,1,0) || FileSha256(baseMod.Location)!=CoopDiagPinTable.BaseModSha256)
            throw new InvalidOperationException("CoopDiag requires the community 0.5.1 release DLL 378BE6B0");
        if(!File.Exists(nativePath) || FileSha256(nativePath)!=CoopDiagPinTable.NativeSha256)
            throw new InvalidOperationException("CoopDiag requires the native35.13.5 hlboot.dat A37869A3");
        Methods.Clear();Fields.Clear();
        foreach(var row in CoopDiagPinTable.Rows)
        {
            var asm=row.Assembly=="base"?baseMod:row.Assembly=="haxeproxy"?haxeProxy:throw new InvalidOperationException("Unknown pin assembly "+row.Assembly);
            var type=asm.GetType(row.Type,false)??throw new InvalidOperationException("Pinned type missing: "+row.Type);
            MethodBase[] found=row.Method==".ctor"
                ?type.GetConstructors(All).Where(c=>!c.IsStatic&&c.GetParameters().Length==row.Params).Cast<MethodBase>().ToArray()
                :type.GetMethods(All).Where(m=>m.Name==row.Method&&m.GetParameters().Length==row.Params).Cast<MethodBase>().ToArray();
            if(found.Length!=1)throw new InvalidOperationException("Pinned method not unique: "+row.Type+"."+row.Method+" ("+found.Length+")");
            var method=found[0];
            if(method.IsStatic!=row.Static)throw new InvalidOperationException("Pinned method static-ness changed: "+row.Key);
            var il=method.GetMethodBody()?.GetILAsByteArray();
            if(il==null||il.Length!=row.IlLength||Convert.ToHexString(SHA256.HashData(il))!=row.Sha256)
                throw new InvalidOperationException("Pinned body changed: "+row.Type+"."+row.Method);
            Methods.Add(row.Key,method);
        }
        foreach(var row in CoopDiagPinTable.Fields)
        {
            var type=baseMod.GetType(row.Type,false)??throw new InvalidOperationException("Pinned type missing: "+row.Type);
            var field=type.GetField(row.Name,All)??throw new InvalidOperationException("Pinned field missing: "+row.Type+"."+row.Name);
            if(field.IsStatic!=row.Static)throw new InvalidOperationException("Pinned field static-ness changed: "+row.Key);
            if(row.FieldType!=null&&field.FieldType.FullName!=row.FieldType)throw new InvalidOperationException("Pinned field type changed: "+row.Key);
            Fields.Add(row.Key,field);
        }
        // The generic field types (left null in the table).
        Require(typeof(IDictionary).IsAssignableFrom(Fields["remoteDowned"].FieldType)&&typeof(ICollection).IsAssignableFrom(Fields["remoteDowned"].FieldType),"remoteDowned");
        Require(Fields["bossLevels"].FieldType==typeof(HashSet<string>),"bossLevels");
        Require(Fields["tracked"].FieldType==typeof(List<dc.en.Mob>),"tracked");
        Require(Fields["scratch"].FieldType==typeof(List<dc.en.Mob>),"scratch");
        // Return types of the read-only calls this part makes through delegates.
        Require(((MethodInfo)Methods["isDowned"]).ReturnType==typeof(bool)&&((MethodInfo)Methods["inRun"]).ReturnType==typeof(bool),"bool queries");
        Require(((MethodInfo)Methods["isBossMob"]).ReturnType==typeof(bool)&&Methods["isBossMob"].GetParameters()[0].ParameterType==typeof(dc.en.Mob),"isBossMob");
        Require(((MethodInfo)Methods["showPerf"]).ReturnType==typeof(bool)&&((MethodInfo)Methods["bossTraceSetting"]).ReturnType==typeof(bool),"settings getters");
        Require(((MethodInfo)Methods["ready"]).ReturnType==typeof(bool)&&((MethodInfo)Methods["rx"]).ReturnType==typeof(bool),"patched bool methods");
        Require(Methods["slot"].GetParameters()[0].ParameterType==typeof(int)&&Methods["slot"].GetParameters()[0].Name=="slot","slot parameter");   // SlotPre binds it by name
        var pass=Methods["pass"].GetParameters();
        Require(pass[1].Name=="preferBoss"&&pass[2].Name=="preferActive"&&pass[3].Name=="includeAll"&&pass.Skip(1).All(p=>p.ParameterType==typeof(bool)),"pass parameters");
        var check=Methods["check"].GetParameters();
        Require(check[0].ParameterType==typeof(Type)&&check[0].Name=="type","check parameters");
    }
    static void Require(bool ok,string what){if(!ok)throw new InvalidOperationException("Pinned shape changed: "+what);}
    public static T Call<T>(string key) where T:Delegate=>(T)Delegate.CreateDelegate(typeof(T),(MethodInfo)Methods[key]);
}
