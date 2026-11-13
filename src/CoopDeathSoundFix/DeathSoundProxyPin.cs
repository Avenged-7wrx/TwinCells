using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;

// Symbolic pin of the GameProxy members CoopDeathSoundFix uses (PLAN-ALPHA2.md section 4 rule 2; lesson of issue 14:
// never pin the raw GameProxy file, which DCCM regenerates per install). Computed from the LOADED GameProxy assembly by
// reflection: for every member, its signature and its IL decoded with every metadata token resolved to a name (string
// literals, methods, fields, types, calli signatures). Nothing token-, MVID- or order-dependent enters the digest, so a
// regenerated proxy of the same generator and the same native game yields the same digest; a changed native function
// index, field name, delegate shape or helper call does not. The pin is compared at install (fail closed).
// Reflection only: no Cecil, no file read (the pack's PackProxyImage is not available to a standalone part; integration
// may switch to it). Pure function of the loaded proxy; offline the managed check computes it from the audit copy.
public static class DeathSoundProxyPin
{
    // Computed by Test-Offline.ps1 from the audit host GameProxy (raw 2A1803BA, DCCM 35.13.5 generator, hlboot A37869A3).
    public const string FingerprintPin="A39CFFB7E8E4463F0AE5B083B892911E4266FE995A975A70ED00CDBB8C977458";
    // (type, member kind, member name, parameter type names) - the members the part and nothing else relies on.
    static readonly (string Type,char Kind,string Name,string[] Args)[] Members=
    {
        ("dc.en.Hook_Mob",'E',"playDamageSounds",null),
        ("dc.en.Hook_Mob",'M',"add_playDamageSounds",new[]{"dc.en.Hook_Mob+hook_playDamageSounds"}),
        ("dc.en.Hook_Mob",'M',"remove_playDamageSounds",new[]{"dc.en.Hook_Mob+hook_playDamageSounds"}),
        ("dc.en.Hook_Mob+hook_playDamageSounds",'D',"Invoke",null),
        ("dc.en.Hook_Mob+orig_playDamageSounds",'D',"Invoke",null),
        ("dc.en.mob.Hook_AxeStatue",'E',"playDamageSounds",null),
        ("dc.en.mob.Hook_AxeStatue",'M',"add_playDamageSounds",new[]{"dc.en.mob.Hook_AxeStatue+hook_playDamageSounds"}),
        ("dc.en.mob.Hook_AxeStatue",'M',"remove_playDamageSounds",new[]{"dc.en.mob.Hook_AxeStatue+hook_playDamageSounds"}),
        ("dc.en.mob.Hook_AxeStatue+hook_playDamageSounds",'D',"Invoke",null),
        ("dc.en.mob.Hook_AxeStatue+orig_playDamageSounds",'D',"Invoke",null),
        ("dc.en.Mob",'M',"playDamageSounds",new[]{"dc.tool.atk.AttackData"}),
        ("dc.en.mob.AxeStatue",'M',"playDamageSounds",new[]{"dc.tool.atk.AttackData"}),
        ("dc.en.mob.AxeStatue",'T',null,null),
        ("dc.Entity",'M',"get_life",new string[0]),
        ("dc.Entity",'M',"get_destroyed",new string[0]),
        ("dc.Entity",'M',"get_onDieDone",new string[0]),
        ("dc.Entity",'M',"get_isOutOfGame",new string[0]),
        ("dc.Entity",'M',"get_isOnScreen",new string[0]),
        ("dc.Entity",'M',"get__level",new string[0]),
        ("dc.pr.Level",'M',"get_lAudio",new string[0]),
    };
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance|BindingFlags.DeclaredOnly;
    static Dictionary<short,OpCode> opcodes;

    public static string Compute(Assembly proxy,out string canonical)
    {
        if(proxy==null)throw new ArgumentNullException(nameof(proxy));
        if(proxy.GetName().Name!="GameProxy")throw new InvalidOperationException("Not the GameProxy assembly: "+proxy.GetName().Name);
        var sb=new StringBuilder("deathsound-proxy-symbolic-v1\n");
        foreach(var m in Members)
        {
            var type=proxy.GetType(m.Type,false)??throw new InvalidOperationException("GameProxy type missing: "+m.Type);
            switch(m.Kind)
            {
                case 'T':
                    sb.Append("type|").Append(type.FullName).Append('|').Append(Name(type.BaseType)).Append('|').Append(((int)type.Attributes).ToString(CultureInfo.InvariantCulture)).Append('\n');
                    break;
                case 'E':
                {
                    var e=type.GetEvent(m.Name,All)??throw new InvalidOperationException("GameProxy event missing: "+m.Type+"."+m.Name);
                    sb.Append("event|").Append(type.FullName).Append('|').Append(e.Name).Append('|').Append(Name(e.EventHandlerType)).Append('|')
                      .Append(e.AddMethod?.Name).Append('|').Append(e.RemoveMethod?.Name).Append('\n');
                    break;
                }
                case 'D':
                {
                    if(!typeof(MulticastDelegate).IsAssignableFrom(type))throw new InvalidOperationException("Not a delegate: "+m.Type);
                    var invoke=type.GetMethod("Invoke",All)??throw new InvalidOperationException("Delegate without Invoke: "+m.Type);
                    sb.Append("delegate|").Append(type.FullName).Append('|').Append(Signature(invoke)).Append('\n');
                    break;
                }
                case 'M':
                {
                    var args=m.Args.Select(a=>proxy.GetType(a,false)??Type.GetType(a,false)??throw new InvalidOperationException("Parameter type missing: "+a)).ToArray();
                    var method=type.GetMethod(m.Name,All,null,args,null)??throw new InvalidOperationException("GameProxy method missing: "+m.Type+"."+m.Name);
                    sb.Append("method|").Append(Signature(method)).Append('|').Append(((int)(method.Attributes&~MethodAttributes.ReservedMask)).ToString(CultureInfo.InvariantCulture))
                      .Append('|').Append(Body(method)).Append('\n');
                    break;
                }
                default:throw new InvalidOperationException("Unknown member kind");
            }
        }
        canonical=sb.ToString();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    static string Name(Type t)
    {
        if(t==null)return "-";
        if(t.IsByRef)return Name(t.GetElementType())+"&";
        if(t.IsArray)return Name(t.GetElementType())+"[]";
        if(t.IsGenericParameter)return (t.DeclaringMethod!=null?"!!":"!")+t.GenericParameterPosition.ToString(CultureInfo.InvariantCulture);
        if(t.IsGenericType&&!t.IsGenericTypeDefinition)
            return Name(t.GetGenericTypeDefinition())+"<"+string.Join(",",t.GetGenericArguments().Select(Name))+">";
        return t.Assembly.GetName().Name+":"+(t.FullName??t.Name);
    }
    static string Signature(MethodBase m)
    {
        string ret=m is MethodInfo mi?Name(mi.ReturnType):"void";
        string generic=m.IsGenericMethod?"<"+string.Join(",",m.GetGenericArguments().Select(Name))+">":"";
        var def=m is MethodInfo g&&g.IsGenericMethod&&!g.IsGenericMethodDefinition?g.GetGenericMethodDefinition():m;
        return Name(m.DeclaringType)+"::"+m.Name+generic+"("+string.Join(",",def.GetParameters().Select(p=>Name(p.ParameterType)))+")->"+ret
            +(m.IsStatic?" static":" instance")+(m.IsVirtual?" virtual":"");
    }
    static string Body(MethodBase m)
    {
        var body=m.GetMethodBody();
        if(body==null)return "nobody";
        var il=body.GetILAsByteArray();var module=m.Module;
        Type[] typeArgs=m.DeclaringType!=null&&m.DeclaringType.IsGenericType?m.DeclaringType.GetGenericArguments():null;
        Type[] methodArgs=m.IsGenericMethod?m.GetGenericArguments():null;
        var sb=new StringBuilder();
        sb.Append("locals=").Append(string.Join(",",body.LocalVariables.Select(v=>Name(v.LocalType)))).Append(";init=").Append(body.InitLocals?1:0)
          .Append(";maxstack=").Append(body.MaxStackSize).Append(";eh=").Append(body.ExceptionHandlingClauses.Count).Append(";il=");
        int i=0;
        while(i<il.Length)
        {
            OpCode op;
            if(il[i]==0xFE){if(i+1>=il.Length)throw new InvalidOperationException("Truncated IL");op=Op((short)(0xFE00|il[i+1]));i+=2;}
            else{op=Op(il[i]);i+=1;}
            sb.Append(op.Name);
            switch(op.OperandType)
            {
                case OperandType.InlineNone:break;
                case OperandType.ShortInlineBrTarget:case OperandType.ShortInlineI:sb.Append(' ').Append(((sbyte)il[i]).ToString(CultureInfo.InvariantCulture));i+=1;break;
                case OperandType.ShortInlineVar:sb.Append(' ').Append(il[i].ToString(CultureInfo.InvariantCulture));i+=1;break;
                case OperandType.InlineVar:sb.Append(' ').Append(BitConverter.ToUInt16(il,i).ToString(CultureInfo.InvariantCulture));i+=2;break;
                case OperandType.InlineI:case OperandType.InlineBrTarget:sb.Append(' ').Append(BitConverter.ToInt32(il,i).ToString(CultureInfo.InvariantCulture));i+=4;break;
                case OperandType.InlineI8:sb.Append(' ').Append(BitConverter.ToInt64(il,i).ToString(CultureInfo.InvariantCulture));i+=8;break;
                case OperandType.ShortInlineR:sb.Append(' ').Append(BitConverter.ToInt32(il,i).ToString("X8",CultureInfo.InvariantCulture));i+=4;break;
                case OperandType.InlineR:sb.Append(' ').Append(BitConverter.ToInt64(il,i).ToString("X16",CultureInfo.InvariantCulture));i+=8;break;
                case OperandType.InlineString:sb.Append(" \"").Append(module.ResolveString(BitConverter.ToInt32(il,i))).Append('"');i+=4;break;
                case OperandType.InlineMethod:sb.Append(' ').Append(Signature(module.ResolveMethod(BitConverter.ToInt32(il,i),typeArgs,methodArgs)));i+=4;break;
                case OperandType.InlineField:
                {
                    var f=module.ResolveField(BitConverter.ToInt32(il,i),typeArgs,methodArgs);
                    sb.Append(' ').Append(Name(f.DeclaringType)).Append("::").Append(f.Name).Append(':').Append(Name(f.FieldType));i+=4;break;
                }
                case OperandType.InlineType:sb.Append(' ').Append(Name(module.ResolveType(BitConverter.ToInt32(il,i),typeArgs,methodArgs)));i+=4;break;
                case OperandType.InlineTok:
                {
                    var member=module.ResolveMember(BitConverter.ToInt32(il,i),typeArgs,methodArgs);
                    sb.Append(' ').Append(member is Type t?Name(t):member is MethodBase mb?Signature(mb):member is FieldInfo fi?Name(fi.DeclaringType)+"::"+fi.Name:member?.ToString());i+=4;break;
                }
                case OperandType.InlineSig:sb.Append(' ').Append(CallSite(module,module.ResolveSignature(BitConverter.ToInt32(il,i))));i+=4;break;
                case OperandType.InlineSwitch:
                {
                    int n=BitConverter.ToInt32(il,i);i+=4;sb.Append(' ').Append(n.ToString(CultureInfo.InvariantCulture));
                    for(int k=0;k<n;k++){sb.Append(',').Append(BitConverter.ToInt32(il,i).ToString(CultureInfo.InvariantCulture));i+=4;}
                    break;
                }
                default:throw new InvalidOperationException("Unreviewed IL operand type "+op.OperandType);
            }
            sb.Append(';');
        }
        return sb.ToString();
    }
    static OpCode Op(short value)
    {
        if(opcodes==null)
        {
            var map=new Dictionary<short,OpCode>();
            foreach(var f in typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static))if(f.GetValue(null) is OpCode o)map[o.Value]=o;
            opcodes=map;
        }
        return opcodes.TryGetValue(value,out var op)?op:throw new InvalidOperationException("Unknown IL opcode 0x"+value.ToString("X4",CultureInfo.InvariantCulture));
    }
    // A stand-alone method signature (calli): calling convention, return type and parameters, with every type token resolved.
    static string CallSite(Module module,byte[] blob)
    {
        int p=0;
        int ReadCompressed()
        {
            byte b=blob[p];
            if((b&0x80)==0){p+=1;return b;}
            if((b&0xC0)==0x80){int v=((b&0x3F)<<8)|blob[p+1];p+=2;return v;}
            if((b&0xE0)==0xC0){int v=((b&0x1F)<<24)|(blob[p+1]<<16)|(blob[p+2]<<8)|blob[p+3];p+=4;return v;}
            throw new InvalidOperationException("Bad compressed integer in signature");
        }
        string ReadType()
        {
            byte e=blob[p++];
            switch(e)
            {
                case 0x01:return "void";case 0x02:return "bool";case 0x03:return "char";case 0x04:return "i1";case 0x05:return "u1";
                case 0x06:return "i2";case 0x07:return "u2";case 0x08:return "i4";case 0x09:return "u4";case 0x0A:return "i8";case 0x0B:return "u8";
                case 0x0C:return "r4";case 0x0D:return "r8";case 0x0E:return "string";case 0x18:return "native int";case 0x19:return "native uint";case 0x1C:return "object";
                case 0x0F:return "ptr "+ReadType();
                case 0x10:return "byref "+ReadType();
                case 0x11:case 0x12:
                {
                    int coded=ReadCompressed();int table=(coded&3) switch{0=>0x02,1=>0x01,2=>0x1B,_=>throw new InvalidOperationException("Bad TypeDefOrRef tag")};
                    return (e==0x11?"valuetype ":"class ")+Name(module.ResolveType((table<<24)|(coded>>2)));
                }
                default:throw new InvalidOperationException("Unreviewed signature element 0x"+e.ToString("X2",CultureInfo.InvariantCulture));
            }
        }
        byte cc=blob[p++];
        if((cc&0x10)!=0)throw new InvalidOperationException("Generic call site");
        int count=ReadCompressed();
        var sb=new StringBuilder("calli cc=").Append(cc.ToString("X2",CultureInfo.InvariantCulture)).Append(' ').Append(ReadType()).Append('(');
        for(int k=0;k<count;k++){if(k>0)sb.Append(',');sb.Append(ReadType());}
        if(p!=blob.Length)throw new InvalidOperationException("Trailing bytes in call-site signature");
        return sb.Append(')').ToString();
    }
}
