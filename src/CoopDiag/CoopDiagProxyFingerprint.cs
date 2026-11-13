using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Symbolic GameProxy fingerprint of CoopDiag (PLAN-ALPHA2.md section 4 rule 2: new GameProxy members are pinned
// symbolically, never by the raw GameProxy file hash; lesson of issue 14). Cecil only, no game types, so Build.ps1 can
// compute the same digest offline from the build and the audit GameProxy.
//
// The algorithm is the reviewed standalone CorpseRotationProxyFingerprint
// (experiments\host-death-20261109\corpse-rotation\CorpseRotationProxyFingerprint.cs) with two changes only:
//   - the module label coop-diag-proxy-symbolic-v1 (every part has its own label);
//   - a root method is looked up in its declaring type first (same record, same digest; the original scanned every
//     GameProxy method per root), falling back to the original full scan when the declaring type is not unique.
// Roots: either extracted from the consumer DLL (the standalone part reads its own CoopDiag.dll, expressions :80-82 of
// the original), or given explicitly (frozen roots, as the pack does for the Alpha1 verifiers).
public sealed class CoopDiagProxyFingerprint
{
    public const string Label="coop-diag-proxy-symbolic-v1";
    public sealed class Result {public string Sha256,Canonical;public string[] Types,Methods,Fields,RootTypes,RootMethods;}
    readonly string[] RootMethods,RootTypes;
    readonly AssemblyDefinition assembly;
    readonly Dictionary<string,TypeDefinition[]> types;
    readonly Dictionary<string,List<CustomAttribute>> bindings=new(StringComparer.Ordinal);
    readonly SortedDictionary<string,string> records=new(StringComparer.Ordinal);
    readonly HashSet<string> seenTypes=new(StringComparer.Ordinal),seenMethods=new(StringComparer.Ordinal),seenFields=new(StringComparer.Ordinal);
    readonly Queue<Action> work=new();

    // Roots of a consumer assembly: its GameProxy-scoped method MemberReferences and TypeReferences, distinct, ordinal.
    public static string[][] ExtractRoots(string consumerPath)
    {
        using(var consumer=AssemblyDefinition.ReadAssembly(consumerPath))
        {
            bool Native(TypeReference type)
            {
                while(type is TypeSpecification spec)type=spec.ElementType;
                return type.Scope is AssemblyNameReference scope && scope.Name=="GameProxy";
            }
            var methods=consumer.MainModule.GetMemberReferences().OfType<MethodReference>().Where(m=>Native(m.DeclaringType))
                .Select(m=>m is GenericInstanceMethod gi?gi.ElementMethod.FullName:m.FullName).Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            var roots=consumer.MainModule.GetTypeReferences().Where(Native).Select(t=>t.FullName).Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            return new[]{roots,methods};
        }
    }
    CoopDiagProxyFingerprint(AssemblyDefinition value,string[] rootTypes,string[] rootMethods)
    {
        assembly=value;RootTypes=rootTypes;RootMethods=rootMethods;
        if(RootMethods==null || RootTypes==null || RootMethods.Length==0 || RootTypes.Length==0)throw new InvalidOperationException("Incomplete CoopDiag native roots");
        // This generator can emit duplicate names for unrelated private closure classes.
        // A referenced type must still resolve uniquely; unrelated closures are outside this contract.
        types=AllTypes(value.MainModule.Types).GroupBy(t=>t.FullName,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.Ordinal);
        foreach(var attr in value.CustomAttributes.Where(a=>a.AttributeType.FullName=="HaxeProxy.Runtime.Internals.HaxeProxyBindingAttribute"))
        {
            string target=BindingTarget(attr);
            if(!bindings.TryGetValue(target,out var list))bindings.Add(target,list=new List<CustomAttribute>());
            list.Add(attr);
        }
    }
    static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> source)
    {foreach(var t in source){yield return t;foreach(var n in AllTypes(t.NestedTypes))yield return n;}}
    static string N(object v)=>Convert.ToString(v,CultureInfo.InvariantCulture);
    static string Pack(params string[] values)
    {var s=new StringBuilder();foreach(var v in values)s.Append(v==null?"-1:":v.Length.ToString(CultureInfo.InvariantCulture)+":"+v);return s.ToString();}
    static string Seq(IEnumerable<string> values)=>Pack(values.ToArray());
    static string Sorted(IEnumerable<string> values)=>Seq(values.OrderBy(v=>v,StringComparer.Ordinal));
    public static Result Compute(byte[] proxyBytes,string consumerPath)
    {using(var s=new MemoryStream(proxyBytes,false))using(var a=AssemblyDefinition.ReadAssembly(s))return Compute(a,consumerPath);}
    public static Result Compute(AssemblyDefinition value,string consumerPath){var r=ExtractRoots(consumerPath);return Compute(value,r[0],r[1]);}
    public static Result Compute(AssemblyDefinition value,string[] rootTypes,string[] rootMethods)=>new CoopDiagProxyFingerprint(value,rootTypes,rootMethods).Run();
    Result Run()
    {
        if(assembly.Name.Name!="GameProxy" || assembly.Name.Version!=new Version(0,0,0,0) || assembly.Modules.Count!=1)
            throw new InvalidOperationException("Unknown proxy assembly shape");
        var m=assembly.MainModule;
        records.Add("module",Pack(Label,assembly.Name.FullName,m.Name,N(m.Architecture),N(m.Kind),N(m.Attributes),N(m.Characteristics)));
        records.Add("assembly-attributes",Attributes(assembly.CustomAttributes.Where(a=>a.AttributeType.FullName!="HaxeProxy.Runtime.Internals.HaxeProxyBindingAttribute")));
        foreach(var name in RootTypes)AddType(GetType(name));
        foreach(var name in RootMethods)AddMethod(FindRootMethod(name));
        while(work.Count>0)work.Dequeue()();
        string canonical=Seq(records.Select(p=>Pack(p.Key,p.Value)));
        return new Result{Sha256=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))),Canonical=canonical,
            Types=seenTypes.OrderBy(x=>x,StringComparer.Ordinal).ToArray(),Methods=seenMethods.OrderBy(x=>x,StringComparer.Ordinal).ToArray(),Fields=seenFields.OrderBy(x=>x,StringComparer.Ordinal).ToArray(),
            RootTypes=RootTypes,RootMethods=RootMethods};
    }
    // "<return> <Declaring>::<name>(<parameters>)": the declaring type is the token before "::".
    MethodDefinition FindRootMethod(string name)
    {
        int sep=name.IndexOf("::",StringComparison.Ordinal);
        if(sep>0)
        {
            int start=name.LastIndexOf(' ',sep)+1;
            string declaring=name.Substring(start,sep-start);
            if(types.TryGetValue(declaring,out var group) && group.Length==1)return group[0].Methods.Single(v=>v.FullName==name);
        }
        return types.Values.SelectMany(g=>g).SelectMany(t=>t.Methods).Single(v=>v.FullName==name);
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
            // Include all field/layout metadata, but only follow field-type dependencies when an actual body uses that field.
            int instanceOrder=0;foreach(var field in t.Fields){RecordField(field,t.IsSequentialLayout&&!field.IsStatic?instanceOrder:-1);if(!field.IsStatic)instanceOrder++;}
            foreach(var ctor in t.Methods.Where(x=>x.IsConstructor))AddMethod(ctor);
            // Cecil cannot parse some unrelated generated closure names as CLR type names.
            // This exact native binding attribute has a fixed int + SerString + zero named-argument ABI.
            // Its raw blob contains no metadata tokens; preserve every byte of matching bindings.
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
    // CoopPack S1 (PACK-DESIGN.md 4.1): the same walk over the shared PackProxyImage. Its TypesByFullName and Bindings are the
    // :56 and :57-62 expressions above, computed once per process; roots, records and digest are unchanged. Declared last, so
    // no member ordinal (and so no compiler-generated name) of the original members changes.
    CoopDiagProxyFingerprint(global::CoopPackRuntime.PackProxyImage image,string[] rootTypes,string[] rootMethods)
    {
        assembly=image.Definition;RootTypes=rootTypes;RootMethods=rootMethods;
        if(RootMethods==null || RootTypes==null || RootMethods.Length==0 || RootTypes.Length==0)throw new InvalidOperationException("Incomplete CoopDiag native roots");
        types=image.TypesByFullName;
        bindings=image.Bindings;
    }
    public static Result Compute(global::CoopPackRuntime.PackProxyImage image,string[] rootTypes,string[] rootMethods)=>new CoopDiagProxyFingerprint(image,rootTypes,rootMethods).Run();
    // The same walk over the pack's frozen copy of these roots (roots\CoopDiag-<sha8>.txt = FrozenRoots.DiagTypes/DiagMethods,
    // equal to CoopDiagPinTable.RootTypes/RootMethods, which CoopDiagProxyCheck keeps using): the G8c/G8d harness entry.
    public static Result Compute(global::CoopPackRuntime.PackProxyImage image)=>Compute(image,global::CoopPackRuntime.FrozenRoots.DiagTypes,global::CoopPackRuntime.FrozenRoots.DiagMethods);
}
