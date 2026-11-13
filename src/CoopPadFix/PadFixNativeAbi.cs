using System;
using System.Reflection;
using System.Reflection.Emit;

// CoopPadFix glue for DCCM native hooks (dc.*.Hook_* events). The generated hook and orig delegate types of GameProxy
// cannot be named at compile time (they derive from System.Private.CoreLib's MulticastDelegate, which the reference-
// assembly build cannot reference: CS0012), so, as in CoopReviveFix (ReviveNativeAbi), the handler and the orig call
// are emitted. No game type is used here: the managed checks run the same emitters on model delegates of each shape.
public static class PadFixNativeAbi
{
    public sealed class Shape
    {
        public EventInfo Event;public Type Handler,Original,Return;public Type[] Args;public MethodInfo OriginalInvoke;
    }
    // hook: a static event whose handler is  R Invoke(orig, A1..An)  and whose orig is  R Invoke(A1..An).
    public static Shape Validate(EventInfo hook,Type ret,params Type[] args)
    {
        if(hook==null||hook.AddMethod==null||hook.RemoveMethod==null||!hook.AddMethod.IsStatic||!hook.RemoveMethod.IsStatic)
            throw new InvalidOperationException("Pinned native hook "+hook?.Name+" is absent");
        var handler=hook.EventHandlerType;var invoke=handler?.GetMethod("Invoke");var p=invoke?.GetParameters();
        if(invoke==null||invoke.ReturnType!=ret||p.Length!=args.Length+1||!typeof(Delegate).IsAssignableFrom(p[0].ParameterType))
            throw new InvalidOperationException("Pinned native "+hook.Name+" handler ABI changed");
        for(int i=0;i<args.Length;i++)if(p[i+1].ParameterType!=args[i])throw new InvalidOperationException("Pinned native "+hook.Name+" handler ABI changed");
        var original=p[0].ParameterType;var call=original.GetMethod("Invoke");var oa=call?.GetParameters();
        if(call==null||call.ReturnType!=ret||oa.Length!=args.Length)throw new InvalidOperationException("Pinned native "+hook.Name+" original ABI changed");
        for(int i=0;i<args.Length;i++)if(oa[i].ParameterType!=args[i])throw new InvalidOperationException("Pinned native "+hook.Name+" original ABI changed");
        if(original.GetConstructor(new[]{typeof(object),typeof(IntPtr)})==null)throw new InvalidOperationException("Pinned native "+hook.Name+" original is not a delegate");
        return new Shape{Event=hook,Handler=handler,Original=original,Return=ret,Args=(Type[])args.Clone(),OriginalInvoke=call};
    }
    // A delegate of type TForward = Func<object,A1..An,R> / Action<object,A1..An> that calls orig.Invoke(A1..An) on an orig
    // held as object (castclass + callvirt Invoke).
    public static TForward Forwarder<TForward>(Shape s) where TForward:Delegate
    {
        var fi=typeof(TForward).GetMethod("Invoke");var fp=fi.GetParameters();
        if(fi.ReturnType!=s.Return||fp.Length!=s.Args.Length+1||fp[0].ParameterType!=typeof(object))throw new InvalidOperationException("Forwarder shape differs from "+s.Event.Name);
        for(int i=0;i<s.Args.Length;i++)if(fp[i+1].ParameterType!=s.Args[i])throw new InvalidOperationException("Forwarder shape differs from "+s.Event.Name);
        var types=new Type[s.Args.Length+1];types[0]=typeof(object);Array.Copy(s.Args,0,types,1,s.Args.Length);
        var dm=new DynamicMethod("CoopPadFix_Original_"+s.Event.Name,s.Return,types,typeof(PadFixNativeAbi),true);var il=dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Castclass,s.Original);
        for(int i=1;i<types.Length;i++)il.Emit(OpCodes.Ldarg,(short)i);
        il.Emit(OpCodes.Callvirt,s.OriginalInvoke);il.Emit(OpCodes.Ret);
        return (TForward)dm.CreateDelegate(typeof(TForward));
    }
    // The hook handler: a delegate of the hook's own type, closed over target, that calls
    // target.<invoke>(object orig, A1..An) and returns its result.
    public static Delegate Handler(Shape s,object target,MethodInfo invoke)
    {
        if(target==null||invoke==null||invoke.IsStatic||!invoke.DeclaringType.IsInstanceOfType(target))throw new ArgumentException("Hook target method invalid");
        var ip=invoke.GetParameters();
        if(invoke.ReturnType!=s.Return||ip.Length!=s.Args.Length+1||ip[0].ParameterType!=typeof(object))throw new InvalidOperationException("Hook target shape differs from "+s.Event.Name);
        for(int i=0;i<s.Args.Length;i++)if(ip[i+1].ParameterType!=s.Args[i])throw new InvalidOperationException("Hook target shape differs from "+s.Event.Name);
        var types=new Type[s.Args.Length+2];types[0]=invoke.DeclaringType;types[1]=s.Original;Array.Copy(s.Args,0,types,2,s.Args.Length);
        var dm=new DynamicMethod("CoopPadFix_Hook_"+s.Event.Name,s.Return,types,typeof(PadFixNativeAbi),true);var il=dm.GetILGenerator();
        for(int i=0;i<types.Length;i++)il.Emit(OpCodes.Ldarg,(short)i);
        il.Emit(OpCodes.Callvirt,invoke);il.Emit(OpCodes.Ret);
        return dm.CreateDelegate(s.Handler,target);
    }
}
