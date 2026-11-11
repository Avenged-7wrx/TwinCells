using System;
using System.Collections.Generic;
using System.Reflection;

// Pure rules of the hook chain cache (no game or framework types). Checked without the game by ChainChecks.cs.
public static class ChainRules
{
    public const int WarmSamples=8;
    public const int SamplePeriod=2048;

    // Is the cached chain still built from exactly the handlers that are registered now?
    // Same count, same objects, same order. An equal but different delegate object does not count as the same:
    // the chain holds the objects it was built from.
    public static bool SameHandlers(List<Delegate> live,Delegate[] cached)
    {
        if(live==null||cached==null)return false;
        int n=live.Count;
        if(n!=cached.Length)return false;
        for(int i=0;i<n;i++)if(!ReferenceEquals(live[i],cached[i]))return false;
        return true;
    }

    // The framework stores, for a handler delegate with exactly one target and a target object, the current entry
    // address of the handler method (it reads it again on every call). That address is not constant: the runtime
    // gives one value before the method first runs and another afterwards. A cached chain is therefore valid only
    // while every such address is what it was when the chain was built. Other handlers (static method, combined
    // delegate) go through an address the framework itself keeps per delegate type for the whole session.
    public static bool HasOwnEntryAddress(bool singleTarget,bool hasTargetObject)=>singleTarget&&hasTargetObject;
    public static bool SameEntryAddresses(IntPtr[] built,IntPtr[] current)
    {
        if(built==null||current==null||built.Length!=current.Length)return false;
        for(int i=0;i<built.Length;i++)if(built[i]!=current[i])return false;
        return true;
    }

    // Which calls served by one cached chain are compared with a freshly built chain. call is 1-based.
    // The first WarmSamples calls, then every SamplePeriod-th; every call in QA mode.
    public static bool ShouldSample(long call,bool all)=>call>=1&&(all||call<=WarmSamples||call%SamplePeriod==0);

    // A handler whose first parameter is the closure object itself (not a delegate type) is handed the object the
    // dispatcher built for this call. At index 0 that is the hook manager's constant "original" closure, the same
    // object on every call; at a later index it is an object made for this one call. Such a list is never cached.
    public static bool Cacheable(int index,bool firstParameterIsClosure)=>index>=0&&(!firstParameterIsClosure||index==0);
    // The comparison walks a chain to a fixed depth (about four steps per handler). Lists longer than this are left
    // to the framework so that a comparison never silently covers only part of a chain.
    public const int MaxHandlers=8;
    public static bool CacheableCount(int handlers)=>handlers>=1&&handlers<=MaxHandlers;

    // Elements of a walked chain that are compared by value. Everything else must be the very same object.
    public static bool IsValueLike(object x)=>x is Type||x is MemberInfo||x is string||x is IntPtr||x is UIntPtr||(x!=null&&(x.GetType().IsPrimitive||x.GetType().IsEnum));

    // Two walks describe the same chain: same length and, position by position, the same object or equal values.
    public static bool SameFingerprint(IList<object> a,IList<object> b)
    {
        if(a==null||b==null||a.Count!=b.Count)return false;
        for(int i=0;i<a.Count;i++)
        {
            object x=a[i],y=b[i];
            if(ReferenceEquals(x,y))continue;
            if(x==null||y==null)return false;
            if(IsValueLike(x)&&IsValueLike(y)&&x.GetType()==y.GetType()&&x.Equals(y))continue;
            return false;
        }
        return true;
    }
}
