using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using dc.level;
using dc.pr;
using Serilog;

public sealed partial class CoopSyncFixes
{
    private sealed class NativeSecretEdge {public LevelMap Parent,Child;}
    private sealed class NativeSecretFamily
    {
        public IntPtr Main;public bool Incomplete,AllUnsafe;
        public readonly Dictionary<IntPtr,NativeSecretEdge> Edges=new();
        public readonly Dictionary<IntPtr,LevelMap> UnsafeMaps=new();
    }
    private static readonly ConditionalWeakTable<Game,NativeSecretFamily> SecretFamilies=new();
    private static Delegate SecretFamilyHook;
    private static LevelMap SecretParentMap(Game game,Level parent)
    {
        try{return game!=null && !game.destroyed && parent!=null && !parent.destroyed && parent.game?.HashlinkPointer==game.HashlinkPointer?parent.map:null;}
        catch(Exception error){Log.Warning(error,"[CoopSyncFixes] Cannot observe native secret parent");return null;}
    }
    private static void RejectSecretProvenance(Game game)
    {
        // A successful native generation with missing provenance must never
        // certify a partial world snapshot. Keep the native result unchanged.
        try{if(game!=null){var family=SecretFamilies.GetValue(game,_=>new NativeSecretFamily());family.Incomplete=family.AllUnsafe=true;}}
        catch(Exception error){Log.Warning(error,"[CoopSyncFixes] Cannot invalidate secret provenance");}
    }
    private static void MarkUnsafeSecretMap(NativeSecretFamily family,LevelMap map)
    {
        family.Incomplete=true;
        var pointer=map.HashlinkPointer;
        if(!family.UnsafeMaps.ContainsKey(pointer) && family.UnsafeMaps.Count>=1024){family.AllUnsafe=true;return;}
        family.UnsafeMaps[pointer]=map;
    }
    private static void ObserveNativeSecretMap(Game game,LevelMap parent,LevelMap child)
    {
        // Observation must not turn a successful native generation into a failure.
        try
        {
            if(game==null || child==null)return;
            if(game.destroyed || parent==null || parent.HashlinkPointer==child.HashlinkPointer){RejectSecretProvenance(game);return;}
            var family=SecretFamilies.GetValue(game,_=>new NativeSecretFamily());
            if(family.UnsafeMaps.ContainsKey(child.HashlinkPointer))
            {MarkUnsafeSecretMap(family,parent);return;}
            if(family.Edges.TryGetValue(child.HashlinkPointer,out var prior))
            {
                if(prior.Parent.HashlinkPointer!=parent.HashlinkPointer)
                {
                    family.Incomplete=true;family.Edges.Remove(child.HashlinkPointer);
                    MarkUnsafeSecretMap(family,prior.Parent);MarkUnsafeSecretMap(family,parent);MarkUnsafeSecretMap(family,child);
                }
                return;
            }
            if(family.Edges.Count>=1024){family.Incomplete=family.AllUnsafe=true;Log.Warning("[CoopSyncFixes] Secret provenance capacity exceeded");return;}
            family.Edges[child.HashlinkPointer]=new NativeSecretEdge {Parent=parent,Child=child};
        }
        catch(Exception error){RejectSecretProvenance(game);Log.Warning(error,"[CoopSyncFixes] Cannot retain native secret map provenance");}
    }
    private static void InitializeSecretFamily()
    {
        var ev=typeof(Game).Assembly.GetType("dc.pr.Hook_Game",true).GetEvent("genSecretLevel");var signature=ev.EventHandlerType.GetMethod("Invoke");
        var types=signature.GetParameters().Select(p=>p.ParameterType).ToArray();
        if(types.Length!=4 || signature.ReturnType!=typeof(LevelMap))throw new InvalidOperationException("Unexpected native secret generator signature");
        var method=new DynamicMethod("CoopNativeSecretFamily",signature.ReturnType,types,typeof(CoopSyncFixes),true);var il=method.GetILGenerator();
        var source=il.DeclareLocal(typeof(LevelMap));var result=il.DeclareLocal(typeof(LevelMap));
        il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldarg_2);il.Emit(OpCodes.Call,typeof(CoopSyncFixes).GetMethod(nameof(SecretParentMap),Flags));il.Emit(OpCodes.Stloc,source);
        for(int i=0;i<types.Length;i++)il.Emit(OpCodes.Ldarg,(short)i);
        il.Emit(OpCodes.Callvirt,types[0].GetMethod("Invoke"));il.Emit(OpCodes.Stloc,result);
        il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldloc,source);il.Emit(OpCodes.Ldloc,result);il.Emit(OpCodes.Call,typeof(CoopSyncFixes).GetMethod(nameof(ObserveNativeSecretMap),Flags));
        il.Emit(OpCodes.Ldloc,result);il.Emit(OpCodes.Ret);
        SecretFamilyHook=method.CreateDelegate(ev.EventHandlerType);RuntimeHelpers.PrepareDelegate(SecretFamilyHook);ev.AddEventHandler(null,SecretFamilyHook);
    }
    private static IEnumerable<LevelMap> NativeSecretNeighbors(Game game,LevelMap map)
    {
        if(Disabled("SECRET_HISTORY") || game==null || game.destroyed || !SecretFamilies.TryGetValue(game,out var family))yield break;
        foreach(var edge in family.Edges.Values)
        {
            if(edge.Parent.HashlinkPointer==map.HashlinkPointer)yield return edge.Child;
            if(edge.Child.HashlinkPointer==map.HashlinkPointer)yield return edge.Parent;
        }
    }
    private static bool SecretSnapshotSafe(Game game,LevelMap map)
    {
        if(game==null || map==null)return false;
        if(!SecretFamilies.TryGetValue(game,out var family) || !family.Incomplete)return true;
        if(family.AllUnsafe)return false;
        try{return !NativeMapFamily(map).Keys.Any(family.UnsafeMaps.ContainsKey);}catch(InvalidOperationException){return false;}
    }
    private static void ObserveCommittedSecretRoot()
    {
        var game=Game.Class.ME;var active=game?.hero?._level;
        if(game==null || game.destroyed || game.curCine!=null || active==null || active.destroyed || active.isSecret || active.isSubLevel || active.map==null ||
            (Field("currentLevel") as Level)?.HashlinkPointer!=active.HashlinkPointer || !SecretFamilies.TryGetValue(game,out var family) || family.Main==active.map.HashlinkPointer)return;
        // Preserve secret maps already generated during this main load. A lease,
        // cancelled cine or a return to the same root does not sever the family.
        Dictionary<IntPtr,LevelMap> connected;
        try{connected=NativeMapFamily(active.map);}
        catch(InvalidOperationException error){family.Incomplete=family.AllUnsafe=true;family.Main=active.map.HashlinkPointer;Log.Warning(error,"[CoopSyncFixes] Cannot verify committed secret family");return;}
        foreach(var key in family.Edges.Where(p=>!connected.ContainsKey(p.Value.Parent.HashlinkPointer) || !connected.ContainsKey(p.Value.Child.HashlinkPointer)).Select(p=>p.Key).ToArray())family.Edges.Remove(key);
        foreach(var key in family.UnsafeMaps.Keys.Where(p=>!connected.ContainsKey(p)).ToArray())family.UnsafeMaps.Remove(key);
        family.Incomplete=family.AllUnsafe || family.UnsafeMaps.Count>0;
        family.Main=active.map.HashlinkPointer;
    }
}
