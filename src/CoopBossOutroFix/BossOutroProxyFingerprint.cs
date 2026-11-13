using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Part CoopBossOutroFix: symbolic pin of the GameProxy members this part uses (PLAN-ALPHA2.md section 4 rule 2: never the
// raw GameProxy file hash alone - lesson of issue 14, the friend's DCCM regenerated GameProxy 90C794F5). Same model as the
// Alpha1 verifiers that installed on the regenerated proxy (CoopReviveFix ReviveProxyFingerprint.cs, symbolic 3344193555 on
// raw 90C794F5, client log C:129): the raw pin 2A1803BA is accepted as before; any other GameProxy must have the four
// generator pins, be the loaded image (MVID and name) and give exactly FingerprintPin, a SHA-256 over the canonical
// signatures, attributes, native bindings and IL of the root members and everything their bodies reach inside GameProxy.
//
// Standalone form: BossOutroProxyImage is this part's own read of the GameProxy bytes (the subset of the pack's
// CoopPackRuntime.PackProxyImage that the walk needs, same expressions). At pack integration (U15) the image may be swapped
// for the shared PackProxyImage like the pack edit S1 did for the six Alpha1 verifiers; the walk below only uses
// Definition, TypesByFullName and Bindings. Only initialization uses Cecil; nothing is written.
public sealed class BossOutroProxyImage : IDisposable
{
    readonly string _path;
    byte[] _bytes;
    string _raw;
    MemoryStream _stream;
    AssemblyDefinition _definition;
    Dictionary<string,TypeDefinition[]> _types;
    Dictionary<string,List<CustomAttribute>> _bindings;
    bool _disposed;
    BossOutroProxyImage(string path,byte[] bytes){_path=path;_bytes=bytes;}
    public static BossOutroProxyImage FromFile(string path)=>new(path??throw new ArgumentNullException(nameof(path)),null);
    public static BossOutroProxyImage FromBytes(byte[] bytes)=>new(null,bytes??throw new ArgumentNullException(nameof(bytes)));
    public byte[] Bytes{get{Live();return _bytes??=File.ReadAllBytes(_path);}}
    public string RawSha256=>_raw??=Convert.ToHexString(SHA256.HashData(Bytes));
    public AssemblyDefinition Definition
    {
        get
        {
            if(_definition==null)
            {
                var stream=new MemoryStream(Bytes,false);
                try{_definition=AssemblyDefinition.ReadAssembly(stream);_stream=stream;}
                catch{stream.Dispose();throw;}
            }
            return _definition;
        }
    }
    public Dictionary<string,TypeDefinition[]> TypesByFullName=>_types??=AllTypes(Definition.MainModule.Types).GroupBy(t=>t.FullName,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.Ordinal);
    public Dictionary<string,List<CustomAttribute>> Bindings
    {
        get
        {
            if(_bindings==null)
            {
                var bindings=new Dictionary<string,List<CustomAttribute>>(StringComparer.Ordinal);
                foreach(var attr in Definition.CustomAttributes.Where(a=>a.AttributeType.FullName=="HaxeProxy.Runtime.Internals.HaxeProxyBindingAttribute"))
                {
                    string target=BindingTarget(attr);
                    if(!bindings.TryGetValue(target,out var list))bindings.Add(target,list=new List<CustomAttribute>());
                    list.Add(attr);
                }
                _bindings=bindings;
            }
            return _bindings;
        }
    }
    public void Dispose()
    {
        if(_disposed)return;
        _disposed=true;
        try{_definition?.Dispose();}catch{}
        try{_stream?.Dispose();}catch{}
        _definition=null;_stream=null;_types=null;_bindings=null;_bytes=null;
    }
    void Live(){if(_disposed)throw new ObjectDisposedException(nameof(BossOutroProxyImage));}
    static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> source)
    {foreach(var t in source){yield return t;foreach(var n in AllTypes(t.NestedTypes))yield return n;}}
    internal static string BindingTarget(CustomAttribute a)
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

public static class BossOutroProxyCompatibility
{
    public const string RawPin="2A1803BA27DB053A21CD854CA4F720C5F5D874FE384F72603CA524F21BB98259";
    // Computed from the raw-pinned GameProxy 2A1803BA (the audit copies) by checks\BossOutroProxyChecks.cs.
    public const string FingerprintPin="AAD286AD5FBC06BDDDBFDAD90E64579367E2CB79258E9AB9DE428D8066C5489B";
    public static readonly IReadOnlyDictionary<string,string> GeneratorPins=new System.Collections.ObjectModel.ReadOnlyDictionary<string,string>(new Dictionary<string,string>(StringComparer.Ordinal) {
        ["ModCore"]="630FBBCEDFE8B2E66C5F75234BE80C2EC42FB178C30706E7398583D9FA1A14AE",
        ["HashlinkNET.Compiler"]="4E6E19C786FF26475A1FEFC6D3A0F718EA429A901770F4FDD68A81F388602B62",
        ["Mono.Cecil"]="831DCA77470D85CB6FFBEA3072DAA7A3DF5B7C9FCFD9C3F43674A9BE99D4BFCF",
        ["HaxeProxy"]="0DF9697335FFDBA4461E599CEE063BC41E4B76C881CCAEDB08AE16DC4A27A8B2"
    });
    // Returns "raw" when the raw pin matched, "symbolic" when the symbolic path accepted the image; throws otherwise.
    public static string ValidateImage(BossOutroProxyImage image,Guid loadedMvid,string loadedName,IReadOnlyDictionary<string,string> generators)
    {
        if(image.RawSha256==RawPin)return "raw";
        foreach(var pin in GeneratorPins)
            if(generators==null || !generators.TryGetValue(pin.Key,out var actual) || actual!=pin.Value)
                throw new InvalidOperationException("CoopBossOutroFix unsupported generator: "+pin.Key);
        var assembly=image.Definition;
        if(assembly.Modules.Count!=1 || assembly.MainModule.Mvid!=loadedMvid || assembly.Name.FullName!=loadedName)
            throw new InvalidOperationException("Loaded GameProxy identity differs from the checked image");
        if(BossOutroProxyFingerprint.Compute(image).Sha256!=FingerprintPin)
            throw new InvalidOperationException("CoopBossOutroFix unsupported symbolic proxy ABI/IL");
        return "symbolic";
    }
}

// The walk of ReviveProxyFingerprint.cs (Alpha1 CoopReviveFix, symbolic on the regenerated proxy) with this part's roots
// and label. Root methods are looked up in their declaring type (no all-methods index).
public sealed class BossOutroProxyFingerprint
{
    public sealed class Result {public string Sha256,Canonical;public string[] Types,Methods,Fields;}
    public const string HookType="dc.en.mob.boss.Hook_Behemoth";
    public const string HookEvent="tryToPreventDeath";
    // (declaring type, Cecil method FullName): every GameProxy member the part (BossOutroPatch.cs) calls.
    public static readonly IReadOnlyList<(string Type,string Method)> RootMethods=Array.AsReadOnly(new[]{
        ("dc.pr.Game","dc.pr._Game dc.pr.Game::get_Class()"),("dc.pr._Game","dc.pr.Game dc.pr._Game::get_ME()"),
        ("dc.pr.Game","dc.GameCinematic dc.pr.Game::get_curCine()"),("dc.pr.Game","System.Void dc.pr.Game::set_curCine(dc.GameCinematic)"),
        ("dc.libs.Process","System.Int32 dc.libs.Process::get_uniqId()"),("dc.libs.Process","System.Boolean dc.libs.Process::get_destroyed()"),
        ("dc.libs.Process","System.Void dc.libs.Process::set_destroyed(System.Boolean)"),
        ("dc.Entity","System.Int32 dc.Entity::get_life()"),("dc.Entity","System.Int32 dc.Entity::get_maxLife()"),
        ("dc.Entity","System.Boolean dc.Entity::get_destroyed()"),("dc.Entity","dc.pr.Level dc.Entity::get__level()"),
        ("dc.pr.Level","dc.level.LevelMap dc.pr.Level::get_map()"),("dc.level.LevelMap","dc.String dc.level.LevelMap::get_id()"),
        ("dc.en.Hero","System.Void dc.en.Hero::unlockControls()"),("dc.en.Hero","System.Void dc.en.Hero::cancelSkillControlLock()")});
    public static readonly IReadOnlyList<string> RootTypes=Array.AsReadOnly(new[]{"dc.en.mob.boss.Behemoth","dc.tool.atk.AttackData","dc.GameCinematic","dc.libs.Process",
        "dc.pr.Game","dc.pr._Game","dc.Entity","dc.en.Mob","dc.en.Hero","dc.pr.Level","dc.level.LevelMap"});
    readonly AssemblyDefinition assembly;
    readonly Dictionary<string,TypeDefinition[]> types;
    readonly Dictionary<string,List<CustomAttribute>> bindings;
    readonly SortedDictionary<string,string> records=new(StringComparer.Ordinal);
    readonly HashSet<string> seenTypes=new(StringComparer.Ordinal),seenMethods=new(StringComparer.Ordinal),seenFields=new(StringComparer.Ordinal);
    readonly Queue<Action> work=new();
    BossOutroProxyFingerprint(BossOutroProxyImage image)
    {
        assembly=image.Definition;
        // The generator can emit duplicate names for unrelated private closure classes; a referenced type must still
        // resolve uniquely, unrelated closures are outside this contract.
        types=image.TypesByFullName;
        bindings=image.Bindings;
    }
    static string N(object v)=>Convert.ToString(v,CultureInfo.InvariantCulture);
    static string Pack(params string[] values)
    {var s=new StringBuilder();foreach(var v in values)s.Append(v==null?"-1:":v.Length.ToString(CultureInfo.InvariantCulture)+":"+v);return s.ToString();}
    static string Seq(IEnumerable<string> values)=>Pack(values.ToArray());
    static string Sorted(IEnumerable<string> values)=>Seq(values.OrderBy(v=>v,StringComparer.Ordinal));
    public static Result Compute(byte[] bytes)
    {using(var image=BossOutroProxyImage.FromBytes(bytes))return Compute(image);}
    public static Result Compute(BossOutroProxyImage image)=>new BossOutroProxyFingerprint(image).Run();
    Result Run()
    {
        if(assembly.Name.Name!="GameProxy" || assembly.Name.Version!=new Version(0,0,0,0) || assembly.Modules.Count!=1)
            throw new InvalidOperationException("Unknown proxy assembly shape");
        var m=assembly.MainModule;
        records.Add("module",Pack("boss-outro-proxy-symbolic-v1",assembly.Name.FullName,m.Name,N(m.Architecture),N(m.Kind),N(m.Attributes),N(m.Characteristics)));
        records.Add("assembly-attributes",Attributes(assembly.CustomAttributes.Where(a=>a.AttributeType.FullName!="HaxeProxy.Runtime.Internals.HaxeProxyBindingAttribute")));
        foreach(var name in RootTypes)AddType(GetType(name));
        foreach(var (type,name) in RootMethods)AddMethod(GetType(type).Methods.Single(v=>v.FullName==name));
        var hook=GetType(HookType).Events.Single(e=>e.Name==HookEvent);
        AddType(hook.DeclaringType);AddTypeReference(hook.EventType);
        if(hook.AddMethod==null || hook.RemoveMethod==null)throw new InvalidOperationException("Missing native hook accessors");
        records.Add("event:"+hook.FullName,Pack(hook.Name,N(hook.Attributes),TypeSig(hook.EventType),Attributes(hook.CustomAttributes),MethodSig(hook.AddMethod),MethodSig(hook.RemoveMethod),hook.InvokeMethod==null?null:MethodSig(hook.InvokeMethod)));
        AddMethod(hook.AddMethod);AddMethod(hook.RemoveMethod);if(hook.InvokeMethod!=null)AddMethod(hook.InvokeMethod);
        // Both handler and original delegates need their exact runtime Invoke contracts.
        var handler=LocalType(hook.EventType)??throw new InvalidOperationException("Nonlocal hook delegate");
        var invoke=handler.Methods.Single(x=>x.Name=="Invoke");AddMethod(invoke);
        var original=LocalType(invoke.Parameters[0].ParameterType)??throw new InvalidOperationException("Nonlocal original delegate");
        AddType(original);AddMethod(original.Methods.Single(x=>x.Name=="Invoke"));
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
            // All field/layout metadata, but field-type dependencies are followed only when a body uses the field.
            int instanceOrder=0;foreach(var field in t.Fields){RecordField(field,t.IsSequentialLayout&&!field.IsStatic?instanceOrder:-1);if(!field.IsStatic)instanceOrder++;}
            foreach(var ctor in t.Methods.Where(x=>x.IsConstructor))AddMethod(ctor);
            // The native binding attribute has a fixed int + SerString + zero named-argument ABI; its raw blob holds no
            // metadata tokens, so every byte of the matching bindings is kept.
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
