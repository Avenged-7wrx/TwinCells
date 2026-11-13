using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

// CoopOpt T3: the GameProxy check without the raw file pin (Alpha2 unit U12, PLAN-ALPHA2.md issue 14).
//
// Alpha1 T3 refused to install unless coremod\cache\GameProxy.dll was byte-identical to 2A1803BA. DCCM 35.13.5 writes a
// new GameProxy whenever it regenerates its cache (random MVID, PE timestamp, unsorted parallel type/binding emission;
// revive-portability-20261108\README.md), so a clean install on another PC never matched: the friend's copy (raw
// 90C794F5) logged "pinned file changed: GameProxy" and ran without T3, although every symbolic check of the pack passed.
// Now (OptHostTargets.cs BindT3 -> VerifyT3Proxy):
//   1. the raw file still equals 2A1803BA: accepted as in Alpha1 (no Cecil, no new log line);
//   2. otherwise (or with the QA switch COOP_QA_OPT_T3_SYMBOLIC=1, which only forces this stricter route) Verify:
//      a. the four generator files are the pinned DCCM 35.13.5 ones and are the loaded assemblies (MVID and name);
//      b. the checked image is the loaded GameProxy (one module, same MVID, same assembly name);
//      c. en.hero.Beheaded / en.hero.Richter derive from en.Hero and declare no canBeHitBy / canBeHit / canBeDetected
//         (T3's receiver whitelist relies on it; the loaded types are checked the same way in BindT3);
//      d. the symbolic fingerprint of every proxy member T3 reads, calls or dispatches through (RootTypes,
//         RootMethods, followed transitively) equals FingerprintPin.
//   Any failure throws, and T3 is "not installed" with that reason (the other host items are unaffected).
// The fingerprint walk is a verbatim copy of CoopCorpseRotationFix's CorpseRotationProxyFingerprint (the reviewed pack
// copy, S1) with two changes only: the label of the "module" record is a parameter, and the image data (definition, type
// index, native bindings, method index) are passed in. The managed suite (experiments\alpha2-perf-lobby) proves the copy
// with the corpse label and frozen corpse roots reproduces the pinned corpse digest B5940C22.. on 2A1803BA, and that the
// T3 digest is equal on 2A1803BA and on two independently regenerated images (8A1D097B, 22243744).
// The image: inside CoopPack the pack's shared CoopPackRuntime.PackProxyImage (found by name, so this file compiles
// standalone too; COOP_PACK_SHARED_PROXY_DISABLE=1 makes Acquire return a private image); standalone a private Cecil read
// of the loaded file, built with the same expressions as PackProxyImage. Initialization only; nothing is cached.
public static class OptHostT3Proxy
{
    public const string Label="opt-t3-proxy-symbolic-v1";
    // T3 digest of the validated raw 2A1803BA image; equal on the regenerated 8A1D097B and 22243744 (managed suite).
    public const string FingerprintPin="B1B753A44A1D9272BE80EF99819A4F2F76011F327A7AA0207D84FB7C3CD48A1C";
    public const string QaSymbolicSwitch="COOP_QA_OPT_T3_SYMBOLIC";
    // The proxy generator of DCCM 35.13.5 (same pins as CoopCorpseRotationFix / CoopReviveFix).
    public static readonly IReadOnlyDictionary<string,string> GeneratorPins=new ReadOnlyDictionary<string,string>(new Dictionary<string,string>(StringComparer.Ordinal){
        ["ModCore"]="630FBBCEDFE8B2E66C5F75234BE80C2EC42FB178C30706E7398583D9FA1A14AE",
        ["HashlinkNET.Compiler"]="4E6E19C786FF26475A1FEFC6D3A0F718EA429A901770F4FDD68A81F388602B62",
        ["Mono.Cecil"]="831DCA77470D85CB6FFBEA3072DAA7A3DF5B7C9FCFD9C3F43674A9BE99D4BFCF",
        ["HaxeProxy"]="0DF9697335FFDBA4461E599CEE063BC41E4B76C881CCAEDB08AE16DC4A27A8B2"
    });
    // What T3 touches in GameProxy (OptHostTargets.cs BindAcquirable / BindT3 / HitMode / PortHero / PortGame): the
    // receiver types, the four proxy calls (f@2050 en.Hero.canBeHitBy, f@921 Entity.canBeHitBy, f@920 Entity.canBeHit,
    // f@664 pr.Game.hasCinematic), the six field getters and ModalPopUp.Class (global g@2072), the two hook events with
    // their hook / orig delegate types (found by name in BindT3), GameCinematic (IsModFakeDeathCine) and en.Mob (the
    // parameter type T3's patched IsAcquirablePlayerCombatTargetForMob is bound by). Cecil names (nested types with '/'),
    // ordinal order. The managed suite checks that every GameProxy member the compiled T3 code names is inside the walk.
    public static readonly string[] RootTypes={
        "dc.Entity",
        "dc.GameCinematic",
        "dc.en.Hero",
        "dc.en.Hook_Hero/hook_canBeHitBy",
        "dc.en.Hook_Hero/orig_canBeHitBy",
        "dc.en.Mob",
        "dc.en.hero.Beheaded",
        "dc.en.hero.Richter",
        "dc.libs.Process",
        "dc.pr.Game",
        "dc.pr.Hook_Game/hook_hasCinematic",
        "dc.pr.Hook_Game/orig_hasCinematic",
        "dc.pr.Level",
        "dc.ui.ModalPopUp",
        "dc.ui._ModalPopUp",
    };
    public static readonly string[] RootMethods={
        "System.Boolean dc.Entity::canBeHit()",
        "System.Boolean dc.Entity::canBeHitBy(dc.Entity)",
        "System.Boolean dc.en.Hero::canBeHitBy(dc.Entity)",
        "System.Boolean dc.en.Hook_Hero/hook_canBeHitBy::Invoke(dc.en.Hook_Hero/orig_canBeHitBy,dc.en.Hero,dc.Entity)",
        "System.Boolean dc.en.Hook_Hero/orig_canBeHitBy::Invoke(dc.en.Hero,dc.Entity)",
        "System.Boolean dc.libs.Process::get_destroyed()",
        "System.Boolean dc.libs.Process::get_paused()",
        "System.Boolean dc.pr.Game::hasCinematic()",
        "System.Boolean dc.pr.Hook_Game/hook_hasCinematic::Invoke(dc.pr.Hook_Game/orig_hasCinematic,dc.pr.Game)",
        "System.Boolean dc.pr.Hook_Game/orig_hasCinematic::Invoke(dc.pr.Game)",
        "System.Void dc.en.Hook_Hero::add_canBeHitBy(dc.en.Hook_Hero/hook_canBeHitBy)",
        "System.Void dc.en.Hook_Hero::remove_canBeHitBy(dc.en.Hook_Hero/hook_canBeHitBy)",
        "System.Void dc.pr.Hook_Game::add_hasCinematic(dc.pr.Hook_Game/hook_hasCinematic)",
        "System.Void dc.pr.Hook_Game::remove_hasCinematic(dc.pr.Hook_Game/hook_hasCinematic)",
        "dc.GameCinematic dc.pr.Game::get_curCine()",
        "dc.pr.Game dc.pr.Level::get_game()",
        "dc.pr.Level dc.Entity::get__level()",
        "dc.ui.ModalPopUp dc.ui._ModalPopUp::get_ME()",
        "dc.ui._ModalPopUp dc.ui.ModalPopUp::get_Class()",
    };
    // T3's receiver whitelist (OptHostTargets.cs HitMode): the two concrete hero classes must land on en.Hero's own
    // canBeHitBy / canBeHit (no override in the proxy; the bytecode side is pinned with hlboot.dat).
    public static readonly string[] HeroSubclasses={"dc.en.hero.Beheaded","dc.en.hero.Richter"};
    public static readonly string[] HeroSlots={"canBeHitBy","canBeHit","canBeDetected"};
    public const string HeroBase="dc.en.Hero";

    public sealed class Outcome{public string Raw,Symbolic,Image;public long Ms;public int Types,Methods,Fields;}

    // ---- pure rules (managed suite) ----
    // The Alpha1 route: the raw file is the pinned one and the QA switch does not force the symbolic route.
    public static bool RawAccepted(string raw,string rawPin,bool qaForceSymbolic)=>!qaForceSymbolic&&raw!=null&&rawPin!=null&&string.Equals(raw,rawPin,StringComparison.Ordinal);
    // The symbolic route's verdict: null = accepted, else the refusal reason (first failing condition, fixed order).
    public static string Refusal(string generatorProblem,bool identityOk,string subclassProblem,string symbolic,string pin)
    {
        if(generatorProblem!=null)return "unsupported GameProxy generator: "+generatorProblem;
        if(!identityOk)return "the checked GameProxy image is not the loaded assembly (MVID or name differs)";
        if(subclassProblem!=null)return subclassProblem;
        if(symbolic==null||pin==null||!string.Equals(symbolic,pin,StringComparison.Ordinal))return "GameProxy differs from the pinned build and its T3 symbolic fingerprint differs ("+(symbolic??"none")+")";
        return null;
    }
    // Null when every generator matches its pin, else the first mismatching name (ordinal order of the pins).
    public static string GeneratorProblem(IReadOnlyDictionary<string,string> actual)
    {
        foreach(var pin in GeneratorPins.OrderBy(p=>p.Key,StringComparer.Ordinal))
            if(actual==null||!actual.TryGetValue(pin.Key,out var v)||!string.Equals(v,pin.Value,StringComparison.Ordinal))return pin.Key;
        return null;
    }
    // c. on the image: each subclass exists once, derives from en.Hero and declares none of the slots.
    public static string SubclassProblem(Dictionary<string,TypeDefinition[]> types)
    {
        foreach(var name in HeroSubclasses)
        {
            if(types==null||!types.TryGetValue(name,out var found)||found.Length!=1)return "GameProxy type "+name+" is missing or ambiguous";
            var t=found[0];
            if(t.BaseType==null||t.BaseType.FullName!=HeroBase)return "GameProxy "+name+" no longer derives from "+HeroBase;
            var declared=t.Methods.Where(m=>HeroSlots.Contains(m.Name,StringComparer.Ordinal)).Select(m=>m.Name).Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            if(declared.Length!=0)return "GameProxy "+name+" declares "+string.Join(", ",declared);
        }
        return null;
    }

    // ---- the check (initialization, game thread) ----
    public static Outcome Verify(Assembly proxy,string raw)
    {
        if(proxy==null)throw new ArgumentNullException(nameof(proxy));
        var watch=Stopwatch.StartNew();
        var image=Image.Open(proxy);
        try
        {
            var o=VerifyImage(image,proxy.ManifestModule.ModuleVersionId,proxy.FullName,raw);
            o.Ms=watch.ElapsedMilliseconds;
            return o;
        }
        finally{image.Release();}
    }
    // a-d on an opened image against the loaded assembly's identity (the managed suite injects mismatches here).
    public static Outcome VerifyImage(Image image,Guid loadedMvid,string loadedName,string raw)
    {
        string generators=GeneratorProblem(image.Generators);
        var def=image.Definition;
        bool identity=def.Modules.Count==1&&def.MainModule.Mvid==loadedMvid&&def.Name.FullName==loadedName;
        string subclass=generators==null&&identity?SubclassProblem(image.TypesByFullName):null;
        OptHostT3ProxyFingerprint.Result r=null;string walk=null;
        if(generators==null&&identity&&subclass==null)
        {
            // a root member that is missing or ambiguous, or unreviewed metadata, makes the walk throw: refused, with why
            try{r=OptHostT3ProxyFingerprint.Compute(def,image.TypesByFullName,image.Bindings,image.MethodsByFullName,RootTypes,RootMethods,Label);}
            catch(Exception e)when(!(e is OutOfMemoryException)){walk=e.GetBaseException().Message;}
        }
        var refusal=Refusal(generators,identity,subclass,r?.Sha256,FingerprintPin);
        if(refusal!=null)throw new InvalidOperationException(refusal+(walk!=null?" [walk: "+walk+"]":"")+" (raw "+raw+")");
        return new Outcome{Raw=raw,Symbolic=r.Sha256,Image=image.Kind,Types=r.Types.Length,Methods=r.Methods.Length,Fields=r.Fields.Length};
    }

    // The GameProxy image: the pack's shared PackProxyImage (reflection, exact member types) or a private Cecil read.
    public sealed class Image
    {
        object _pack;MethodInfo _packRelease,_packMethods;
        MemoryStream _stream;
        public string Kind{get;private set;}
        public AssemblyDefinition Definition{get;private set;}
        public Dictionary<string,TypeDefinition[]> TypesByFullName{get;private set;}
        public Dictionary<string,List<CustomAttribute>> Bindings{get;private set;}
        public IReadOnlyDictionary<string,string> Generators{get;set;}     // set by Open; a FromBytes image gets them from its caller
        Dictionary<string,MethodDefinition[]> _methods;
        Image(){}
        public MethodDefinition[] MethodsByFullName(string name)
        {
            if(_pack!=null)return (MethodDefinition[])_packMethods.Invoke(_pack,new object[]{name});
            if(_methods==null)
            {
                var index=new Dictionary<string,List<MethodDefinition>>(StringComparer.Ordinal);
                foreach(var method in TypesByFullName.Values.SelectMany(group=>group).SelectMany(t=>t.Methods))
                {
                    var key=method.FullName;
                    if(!index.TryGetValue(key,out var list))index.Add(key,list=new List<MethodDefinition>(1));
                    list.Add(method);
                }
                _methods=index.ToDictionary(p=>p.Key,p=>p.Value.ToArray(),StringComparer.Ordinal);
            }
            return _methods.TryGetValue(name,out var found)?found:Array.Empty<MethodDefinition>();
        }
        public static Image Open(Assembly proxy)
        {
            var packType=typeof(OptHostT3Proxy).Assembly.GetType("CoopPackRuntime.PackProxyImage",false);
            if(packType!=null)return FromPack(packType,proxy);
            if(string.IsNullOrEmpty(proxy.Location))throw new InvalidOperationException("GameProxy has no file location");
            var image=FromBytes(File.ReadAllBytes(proxy.Location));
            image.Generators=LoadedGenerators();
            image.Kind="private";
            return image;
        }
        static Image FromPack(Type t,Assembly proxy)
        {
            const BindingFlags P=BindingFlags.Public|BindingFlags.Instance;
            var acquire=t.GetMethod("Acquire",BindingFlags.Public|BindingFlags.Static,null,new[]{typeof(Assembly)},null);
            var release=t.GetMethod("Release",P,null,Type.EmptyTypes,null);
            var methods=t.GetMethod("MethodsByFullName",P,null,new[]{typeof(string)},null);
            PropertyInfo Prop(string n,Type type){var p=t.GetProperty(n,P);if(p==null||p.PropertyType!=type||p.GetMethod==null)throw new InvalidOperationException("CoopPack proxy image member changed: "+n);return p;}
            var def=Prop("Definition",typeof(AssemblyDefinition));var types=Prop("TypesByFullName",typeof(Dictionary<string,TypeDefinition[]>));
            var bindings=Prop("Bindings",typeof(Dictionary<string,List<CustomAttribute>>));var gens=Prop("Generators",typeof(IReadOnlyDictionary<string,string>));
            var shared=Prop("Shared",typeof(bool));
            if(acquire==null||acquire.ReturnType!=t||release==null||methods==null||methods.ReturnType!=typeof(MethodDefinition[]))throw new InvalidOperationException("CoopPack proxy image API changed");
            object pack;
            try{pack=acquire.Invoke(null,new object[]{proxy});}
            catch(TargetInvocationException e)when(e.InnerException!=null){throw e.InnerException;}
            var image=new Image{_pack=pack,_packRelease=release,_packMethods=methods};
            try
            {
                image.Generators=(IReadOnlyDictionary<string,string>)gens.GetValue(pack);
                image.Definition=(AssemblyDefinition)def.GetValue(pack);
                image.TypesByFullName=(Dictionary<string,TypeDefinition[]>)types.GetValue(pack);
                image.Bindings=(Dictionary<string,List<CustomAttribute>>)bindings.GetValue(pack);
                image.Kind=(bool)shared.GetValue(pack)?"pack-shared":"pack-private";
            }
            catch(TargetInvocationException e)when(e.InnerException!=null){image.Release();throw e.InnerException;}
            catch{image.Release();throw;}
            return image;
        }
        // A private image over the given bytes (the managed suite reads the proxy files this way). Same expressions as
        // CoopPackRuntime.PackProxyImage (Definition, TypesByFullName, Bindings). Generators stay null (Open sets them;
        // a null set is refused by GeneratorProblem).
        public static Image FromBytes(byte[] bytes)
        {
            var stream=new MemoryStream(bytes??throw new ArgumentNullException(nameof(bytes)),false);
            AssemblyDefinition def;
            try{def=AssemblyDefinition.ReadAssembly(stream);}
            catch{stream.Dispose();throw;}
            var image=new Image{_stream=stream,Definition=def,Kind="bytes"};
            image.TypesByFullName=AllTypes(def.MainModule.Types).GroupBy(t=>t.FullName,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.Ordinal);
            var map=new Dictionary<string,List<CustomAttribute>>(StringComparer.Ordinal);
            foreach(var attr in def.CustomAttributes.Where(a=>a.AttributeType.FullName=="HaxeProxy.Runtime.Internals.HaxeProxyBindingAttribute"))
            {
                string target=BindingTarget(attr);
                if(!map.TryGetValue(target,out var list))map.Add(target,list=new List<CustomAttribute>());
                list.Add(attr);
            }
            image.Bindings=map;
            return image;
        }
        // PackProxyImage.Generators (verbatim): the loaded generator assemblies, each file read, hashed and checked to
        // be the loaded assembly (MVID and name).
        public static IReadOnlyDictionary<string,string> LoadedGenerators()
        {
            var pins=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var assembly in new[]{typeof(ModCore.Mods.ModBase).Assembly,typeof(HashlinkNET.Compiler.HashlinkCompiler).Assembly,
                typeof(ModuleDefinition).Assembly,typeof(HaxeProxy.Runtime.HaxeProxyBase).Assembly})
            {
                byte[] image=File.ReadAllBytes(assembly.Location);
                using(var stream=new MemoryStream(image,false))using(var parsed=AssemblyDefinition.ReadAssembly(stream))
                    if(parsed.MainModule.Mvid!=assembly.ManifestModule.ModuleVersionId || parsed.Name.FullName!=assembly.FullName)
                        throw new InvalidOperationException("Loaded generator identity differs from its file");
                pins.Add(assembly.GetName().Name,Convert.ToHexString(SHA256.HashData(image)));
            }
            return new ReadOnlyDictionary<string,string>(pins);
        }
        public void Release()
        {
            if(_pack!=null){var p=_pack;_pack=null;try{_packRelease.Invoke(p,null);}catch{}return;}
            try{Definition?.Dispose();}catch{}
            try{_stream?.Dispose();}catch{}
            Definition=null;_stream=null;TypesByFullName=null;Bindings=null;_methods=null;
        }
        static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> source)
        {foreach(var t in source){yield return t;foreach(var n in AllTypes(t.NestedTypes))yield return n;}}
        // PackProxyImage.BindingTarget (verbatim).
        static string BindingTarget(CustomAttribute a)
        {
            if(a.Constructor.FullName!="System.Void HaxeProxy.Runtime.Internals.HaxeProxyBindingAttribute::.ctor(System.Int32,System.Type)")
                throw new InvalidOperationException("Unknown native binding constructor");
            var b=a.GetBlob();int p=6;
            if(b.Length<9 || b[0]!=1 || b[1]!=0)throw new InvalidOperationException("Malformed native binding");
            int head=b[p++],length;
            if((head&0x80)==0)length=head;
            else if((head&0xc0)==0x80){if(p>=b.Length)throw new InvalidOperationException("Truncated binding");length=((head&0x3f)<<8)|b[p++];}
            else if((head&0xe0)==0xc0){if(p+3>b.Length)throw new InvalidOperationException("Truncated binding");length=((head&0x1f)<<24)|(b[p++]<<16)|(b[p++]<<8)|b[p++];}
            else throw new InvalidOperationException("Unknown binding string prefix");
            if(length>b.Length-p-2 || p+length+2!=b.Length || b[b.Length-1]!=0 || b[b.Length-2]!=0)
                throw new InvalidOperationException("Unknown binding payload");
            return new UTF8Encoding(false,true).GetString(b,p,length);
        }
    }
}

// Verbatim copy of CorpseRotationProxyFingerprint (CoopPack source, S1 form); changes: the module-record label is a
// parameter, and the image data are passed in (definition, type index, bindings, method index).
public sealed class OptHostT3ProxyFingerprint
{
    public sealed class Result {public string Sha256,Canonical;public string[] Types,Methods,Fields;}
    readonly string[] RootMethods,RootTypes;
    readonly string ModuleLabel;
    readonly AssemblyDefinition assembly;
    readonly Dictionary<string,TypeDefinition[]> types;
    readonly Dictionary<string,List<CustomAttribute>> bindings;
    readonly Func<string,MethodDefinition[]> methodsByFullName;
    readonly SortedDictionary<string,string> records=new(StringComparer.Ordinal);
    readonly HashSet<string> seenTypes=new(StringComparer.Ordinal),seenMethods=new(StringComparer.Ordinal),seenFields=new(StringComparer.Ordinal);
    readonly Queue<Action> work=new();
    OptHostT3ProxyFingerprint(AssemblyDefinition definition,Dictionary<string,TypeDefinition[]> typeIndex,Dictionary<string,List<CustomAttribute>> bindingIndex,
        Func<string,MethodDefinition[]> methodIndex,string[] rootTypes,string[] rootMethods,string label)
    {
        assembly=definition??throw new ArgumentNullException(nameof(definition));
        RootMethods=rootMethods;
        RootTypes=rootTypes;
        ModuleLabel=label;
        if(RootMethods==null || RootTypes==null || RootMethods.Length==0 || RootTypes.Length==0 || string.IsNullOrEmpty(ModuleLabel))throw new InvalidOperationException("Incomplete T3 native roots");
        types=typeIndex??throw new ArgumentNullException(nameof(typeIndex));
        bindings=bindingIndex??throw new ArgumentNullException(nameof(bindingIndex));
        methodsByFullName=methodIndex??throw new ArgumentNullException(nameof(methodIndex));
    }
    static string N(object v)=>Convert.ToString(v,CultureInfo.InvariantCulture);
    static string Pack(params string[] values)
    {var s=new StringBuilder();foreach(var v in values)s.Append(v==null?"-1:":v.Length.ToString(CultureInfo.InvariantCulture)+":"+v);return s.ToString();}
    static string Seq(IEnumerable<string> values)=>Pack(values.ToArray());
    static string Sorted(IEnumerable<string> values)=>Seq(values.OrderBy(v=>v,StringComparer.Ordinal));
    public static Result Compute(AssemblyDefinition definition,Dictionary<string,TypeDefinition[]> typeIndex,Dictionary<string,List<CustomAttribute>> bindingIndex,
        Func<string,MethodDefinition[]> methodIndex,string[] rootTypes,string[] rootMethods,string label)
        =>new OptHostT3ProxyFingerprint(definition,typeIndex,bindingIndex,methodIndex,rootTypes,rootMethods,label).Run();
    Result Run()
    {
        if(assembly.Name.Name!="GameProxy" || assembly.Name.Version!=new Version(0,0,0,0) || assembly.Modules.Count!=1)
            throw new InvalidOperationException("Unknown proxy assembly shape");
        var m=assembly.MainModule;
        records.Add("module",Pack(ModuleLabel,assembly.Name.FullName,m.Name,N(m.Architecture),N(m.Kind),N(m.Attributes),N(m.Characteristics)));
        records.Add("assembly-attributes",Attributes(assembly.CustomAttributes.Where(a=>a.AttributeType.FullName!="HaxeProxy.Runtime.Internals.HaxeProxyBindingAttribute")));
        foreach(var name in RootTypes)AddType(GetType(name));
        foreach(var name in RootMethods)AddMethod(methodsByFullName(name).Single(v=>v.FullName==name));
        while(work.Count>0)work.Dequeue()();
        string canonical=Seq(records.Select(p=>Pack(p.Key,p.Value)));
        return new Result{Sha256=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))),Canonical=canonical,
            Types=seenTypes.OrderBy(x=>x,StringComparer.Ordinal).ToArray(),Methods=seenMethods.OrderBy(x=>x,StringComparer.Ordinal).ToArray(),Fields=seenFields.OrderBy(x=>x,StringComparer.Ordinal).ToArray()};
    }
    TypeDefinition GetType(string name)=>types.TryGetValue(name,out var t)?t.Single():throw new InvalidOperationException("Missing proxy type "+name);
    TypeDefinition LocalType(TypeReference reference)
    {
        if(reference==null || reference is GenericParameter)return null;
        while(reference is TypeSpecification spec)reference=spec.ElementType;
        if(reference.Scope is ModuleDefinition md && md==assembly.MainModule || reference.Scope is AssemblyNameReference ar && ar.Name=="GameProxy")return GetType(reference.FullName);
        return null;
    }
    void AddTypeReference(TypeReference reference)
    {
        if(reference==null || reference is GenericParameter)return;
        if(reference is GenericInstanceType gi)foreach(var arg in gi.GenericArguments)AddTypeReference(arg);
        if(reference is IModifierType mod)AddTypeReference(mod.ModifierType);
        if(reference is FunctionPointerType fp){AddTypeReference(fp.ReturnType);foreach(var p in fp.Parameters)AddTypeReference(p.ParameterType);return;}
        var local=LocalType(reference);if(local!=null)AddType(local);
    }
    void AddType(TypeDefinition t)
    {
        if(!seenTypes.Add(t.FullName))return;
        work.Enqueue(()=>{
            AddTypeReference(t.BaseType);if(t.DeclaringType!=null)AddType(t.DeclaringType);
            foreach(var i in t.Interfaces)AddTypeReference(i.InterfaceType);
            records.Add("type:"+t.FullName,Pack(TypeSig(t),N(t.Attributes),TypeSig(t.BaseType),N(t.PackingSize),N(t.ClassSize),
                Sorted(t.Interfaces.Select(i=>Pack(TypeSig(i.InterfaceType),Attributes(i.CustomAttributes)))),GenericParameters(t.GenericParameters),Attributes(t.CustomAttributes)));
            int instanceOrder=0;foreach(var field in t.Fields){RecordField(field,t.IsSequentialLayout&&!field.IsStatic?instanceOrder:-1);if(!field.IsStatic)instanceOrder++;}
            foreach(var ctor in t.Methods.Where(x=>x.IsConstructor))AddMethod(ctor);
            records.Add("binding:"+t.FullName,bindings.TryGetValue(t.FullName.Replace('/','+'),out var list)?
                Sorted(list.Select(a=>Pack(MethodSig(a.Constructor),Convert.ToHexString(a.GetBlob())))):Seq(Array.Empty<string>()));
        });
    }
    void RecordField(FieldDefinition f,int order)
    {
        if(!seenFields.Add(f.FullName))return;
        if(f.HasMarshalInfo)throw new InvalidOperationException("Unreviewed proxy marshal field");
        records.Add("field:"+f.FullName,Pack(FieldSig(f),N(f.Attributes),N(f.Offset),N(order),f.HasConstant?Value(f.Constant):null,
            f.InitialValue==null?null:Convert.ToHexString(f.InitialValue),Attributes(f.CustomAttributes)));
    }
    MethodDefinition ResolveMethod(MethodReference reference)
    {
        var element=reference is GenericInstanceMethod gi?gi.ElementMethod:reference;
        var type=LocalType(element.DeclaringType);if(type==null)return null;
        return type.Methods.Single(x=>x.FullName==element.FullName);
    }
    void AddMethod(MethodDefinition m)
    {
        if(!seenMethods.Add(m.FullName))return;
        work.Enqueue(()=>{
            AddType(m.DeclaringType);AddTypeReference(m.ReturnType);foreach(var p in m.Parameters)AddTypeReference(p.ParameterType);
            if(m.MethodReturnType.HasMarshalInfo || m.Parameters.Any(p=>p.HasMarshalInfo))throw new InvalidOperationException("Unreviewed proxy marshal signature");
            foreach(var over in m.Overrides){var local=ResolveMethod(over);if(local!=null)AddMethod(local);}
            string signature=Pack(MethodSig(m),N(m.Attributes),N(m.ImplAttributes),N(m.SemanticsAttributes),
                GenericParameters(m.GenericParameters),Attributes(m.CustomAttributes),Attributes(m.MethodReturnType.CustomAttributes),
                Seq(m.Parameters.Select(p=>Pack(p.Name,N(p.Attributes),p.HasConstant?Value(p.Constant):null,Attributes(p.CustomAttributes)))),
                Sorted(m.Overrides.Select(MethodSig)),m.HasPInvokeInfo?Pack(m.PInvokeInfo.Module.Name,m.PInvokeInfo.EntryPoint,N(m.PInvokeInfo.Attributes)):null);
            string body=null;
            if(m.HasBody)
            {
                var b=m.Body;var positions=b.Instructions.Select((v,i)=>(v,i)).ToDictionary(x=>x.v,x=>x.i);
                string Index(Instruction i)=>i==null?N(b.Instructions.Count):N(positions[i]);
                foreach(var v in b.Variables)AddTypeReference(v.VariableType);
                body=Pack(N(b.InitLocals),N(b.MaxStackSize),Seq(b.Variables.Select(v=>TypeSig(v.VariableType))),
                    Seq(b.Instructions.Select(i=>Pack(i.OpCode.Name,Operand(i.Operand,Index)))),
                    Seq(b.ExceptionHandlers.Select(e=>Pack(N(e.HandlerType),Index(e.TryStart),Index(e.TryEnd),Index(e.HandlerStart),Index(e.HandlerEnd),Index(e.FilterStart),TypeSig(e.CatchType)))));
                foreach(var e in b.ExceptionHandlers)AddTypeReference(e.CatchType);
            }
            records.Add("method:"+m.FullName,Pack(signature,body));
            foreach(var p in m.DeclaringType.Properties.Where(p=>p.GetMethod==m || p.SetMethod==m))
                records["property:"+p.FullName]=Pack(p.Name,N(p.Attributes),TypeSig(p.PropertyType),Seq(p.Parameters.Select(x=>TypeSig(x.ParameterType))),Attributes(p.CustomAttributes),p.GetMethod==null?null:MethodSig(p.GetMethod),p.SetMethod==null?null:MethodSig(p.SetMethod));
        });
    }
    string Operand(object operand,Func<Instruction,string> index)
    {
        if(operand==null)return null;
        if(operand is Instruction branch)return Pack("branch",index(branch));
        if(operand is Instruction[] branches)return Pack("switch",Seq(branches.Select(index)));
        if(operand is VariableDefinition variable)return Pack("local",N(variable.Index),TypeSig(variable.VariableType));
        if(operand is ParameterDefinition parameter)return Pack("arg",N(parameter.Index),TypeSig(parameter.ParameterType));
        if(operand is MethodReference method){AddTypeReference(method.DeclaringType);AddTypeReference(method.ReturnType);foreach(var p in method.Parameters)AddTypeReference(p.ParameterType);var local=ResolveMethod(method);if(local!=null)AddMethod(local);return Pack("method",MethodSig(method));}
        if(operand is FieldReference field){AddTypeReference(field.DeclaringType);AddTypeReference(field.FieldType);var type=LocalType(field.DeclaringType);if(type!=null){var found=type.Fields.Single(x=>x.FullName==field.FullName);if(!seenFields.Contains(found.FullName))RecordField(found,type.IsSequentialLayout&&!found.IsStatic?type.Fields.TakeWhile(f=>f!=found).Count(f=>!f.IsStatic):-1);}return Pack("field",FieldSig(field));}
        if(operand is TypeReference tr){AddTypeReference(tr);return Pack("type",TypeSig(tr));}
        if(operand is CallSite call){AddTypeReference(call.ReturnType);foreach(var p in call.Parameters)AddTypeReference(p.ParameterType);return Pack("calli",CallSignature(call));}
        return Value(operand);
    }
    string GenericParameters(IEnumerable<GenericParameter> parameters)=>Seq(parameters.Select(p=>Pack(p.Name,N(p.Position),N(p.Attributes),Attributes(p.CustomAttributes),
        Sorted(p.Constraints.Select(c=>Pack(TypeSig(c.ConstraintType),Attributes(c.CustomAttributes)))))));
    string TypeSig(TypeReference t)
    {
        if(t==null)return null;
        if(t is GenericParameter gp)return Pack(gp.Type==GenericParameterType.Type?"!":"!!",N(gp.Position));
        if(t is GenericInstanceType gi)return Pack("generic",TypeSig(gi.ElementType),Seq(gi.GenericArguments.Select(TypeSig)));
        if(t is ArrayType a)return Pack("array",TypeSig(a.ElementType),N(a.Rank),Seq(a.Dimensions.Select(d=>Pack(N(d.LowerBound),N(d.UpperBound)))));
        if(t is OptionalModifierType om)return Pack("modopt",TypeSig(om.ModifierType),TypeSig(om.ElementType));
        if(t is RequiredModifierType rm)return Pack("modreq",TypeSig(rm.ModifierType),TypeSig(rm.ElementType));
        if(t is FunctionPointerType fp)return Pack("fnptr",CallSignature(fp));
        if(t is ByReferenceType br)return Pack("byref",TypeSig(br.ElementType));
        if(t is PointerType ptr)return Pack("pointer",TypeSig(ptr.ElementType));
        if(t is PinnedType pin)return Pack("pinned",TypeSig(pin.ElementType));
        if(t is SentinelType sn)return Pack("sentinel",TypeSig(sn.ElementType));
        if(t is TypeSpecification)throw new InvalidOperationException("Unknown type specification "+t.GetType().Name);
        string scope=t.Scope is AssemblyNameReference ar?ar.FullName:t.Scope is ModuleDefinition md?md.Assembly.Name.FullName:t.Scope is ModuleReference mr?"module:"+mr.Name:throw new InvalidOperationException("Unknown type scope");
        return Pack("type",scope,t.FullName,N(t.IsValueType));
    }
    string CallSignature(IMethodSignature m)=>Pack(N(m.HasThis),N(m.ExplicitThis),N(m.CallingConvention),TypeSig(m.ReturnType),Seq(m.Parameters.Select(p=>TypeSig(p.ParameterType))));
    string MethodSig(MethodReference m)=>Pack(TypeSig(m.DeclaringType),m.Name,N(m.GenericParameters.Count),CallSignature(m),
        m is GenericInstanceMethod gi?Seq(gi.GenericArguments.Select(TypeSig)):null);
    string FieldSig(FieldReference f)=>Pack(TypeSig(f.DeclaringType),f.Name,TypeSig(f.FieldType));
    string Attributes(IEnumerable<CustomAttribute> attributes)=>Sorted(attributes.Select(a=>Pack(MethodSig(a.Constructor),
        Seq(a.ConstructorArguments.Select(Argument)),Sorted(a.Fields.Select(p=>Pack(p.Name,Argument(p.Argument)))),Sorted(a.Properties.Select(p=>Pack(p.Name,Argument(p.Argument)))))));
    string Argument(CustomAttributeArgument a)=>Pack(TypeSig(a.Type),a.Value is CustomAttributeArgument[] list?Seq(list.Select(Argument)):a.Value is CustomAttributeArgument nested?Argument(nested):Value(a.Value));
    string Value(object v)
    {
        if(v==null)return null;
        if(v is TypeReference tr)return Pack("type-value",TypeSig(tr));
        if(v is string s)return Pack("string",s);
        if(v is float f)return Pack("single-bits",N(BitConverter.SingleToInt32Bits(f)));
        if(v is double d)return Pack("double-bits",N(BitConverter.DoubleToInt64Bits(d)));
        if(v is bool || v is char || v is byte || v is sbyte || v is short || v is ushort || v is int || v is uint || v is long || v is ulong)return Pack(v.GetType().FullName,N(v));
        throw new InvalidOperationException("Unreviewed symbolic value "+v.GetType().FullName);
    }
}
