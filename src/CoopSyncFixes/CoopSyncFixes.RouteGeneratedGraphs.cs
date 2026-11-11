using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using dc.level;
using dc.pr;
using DeadCellsMultiplayerMod;
using HarmonyLib;
using Serilog;

public sealed partial class CoopSyncFixes
{
    private sealed class GeneratedRouteLoadScope
    {
        public GeneratedRouteLoadScope Previous;public NetNode Net;public WorldJoin Join;public WorldHeader Header;public RouteBundle Bundle;
        public int SourceSlot;public IntPtr Game;public bool SourceImported;public LevelMap Parent;
    }
    private sealed class GeneratedRouteGraphScope
    {
        public NetNode Net;public WorldJoin Join;public WorldHeader Header;public RouteBundle Bundle;
        public int ParentSlot,ChildSlot;public LevelMap Parent;public IntPtr Game;
        public GeneratedRouteLoadScope Load;public RouteGeneratedGraphReceipt Receipt;
    }
    private sealed class GeneratedRouteGraphFrame
    {public GeneratedRouteGraphScope Previous,Entered;}
    [ThreadStatic] private static GeneratedRouteGraphScope GeneratedRouteGraph;
    [ThreadStatic] private static GeneratedRouteLoadScope GeneratedRouteLoad;
    private static Delegate GeneratedRouteGraphHook;

    private static GeneratedRouteLoadScope BeginGeneratedRouteLoad(Game game,dc.String id)
    {
        var scope=new GeneratedRouteLoadScope {Previous=GeneratedRouteLoad};GeneratedRouteLoad=scope;
        try
        {
            var net=ModEntry._net;
            if(Disabled("ROUTE_GENERATED_GRAPH") || !PendingWorld(net,out var state) || state.Route==null || !state.Routed || state.ActivationOnly ||
                game==null || game.destroyed || game.HashlinkPointer!=Game.Class.ME?.HashlinkPointer)return scope;
            var slot=state.RouteLoadingSlot;
            if(slot<0 || slot>=state.Route.Graphs.Count || id?.ToString()!=state.Route.Graphs[slot].Map.Level)return scope;
            scope.Net=net;scope.Join=state;scope.Header=state.Header;scope.Bundle=state.Route;scope.SourceSlot=slot;scope.Game=game.HashlinkPointer;
            return scope;
        }
        catch {GeneratedRouteLoad=scope.Previous;throw;}
    }
    private static void EndGeneratedRouteLoad(GeneratedRouteLoadScope scope)=>GeneratedRouteLoad=scope.Previous;
    private static void RecordGeneratedRouteImport(WorldJoin state,RouteGraph graph)
    {
        // Called only after the original importer succeeded with this exact
        // committed graph sequence. Do not use acknowledgments from an older load.
        var child=GeneratedRouteGraph;
        if(child!=null && ReferenceEquals(child.Join,state) && ReferenceEquals(child.Header,state.Header) && ReferenceEquals(child.Bundle,state.Route) &&
            ReferenceEquals(child.Bundle.Graphs[child.ChildSlot],graph))child.Receipt.Acknowledge(graph.Slot,graph.Sequence);
        var load=GeneratedRouteLoad;
        if(load?.Join!=null && ReferenceEquals(load.Join,state) && ReferenceEquals(load.Header,state.Header) && ReferenceEquals(load.Bundle,state.Route) &&
            load.SourceSlot==graph.Slot && ReferenceEquals(load.Bundle.Graphs[load.SourceSlot],graph))load.SourceImported=true;
    }

    private static void InitializeGeneratedRouteGraphs()
    {
        var h=new Harmony("CoopSyncFixes.RouteGeneratedGraphs");
        h.Patch(WorldData.GetMethod("TryWaitApplyRemoteLevelSeed",Flags),
            prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(PinGeneratedRouteSeed),Flags)));
        var ev=typeof(Game).Assembly.GetType("dc.pr.Hook_Game",true).GetEvent("genSecretLevel");
        var signature=ev.EventHandlerType.GetMethod("Invoke");var types=signature.GetParameters().Select(p=>p.ParameterType).ToArray();
        if(types.Length!=4 || types[1]!=typeof(Game) || types[2]!=typeof(Level) || signature.ReturnType!=typeof(LevelMap))
            throw new InvalidOperationException("Pinned secret generator ABI changed");
        var dm=new DynamicMethod("CoopRouteGeneratedGraphScope",signature.ReturnType,types,typeof(CoopSyncFixes),true);var il=dm.GetILGenerator();
        var frame=il.DeclareLocal(typeof(GeneratedRouteGraphFrame));var result=il.DeclareLocal(typeof(LevelMap));
        il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldarg_2);il.Emit(OpCodes.Call,typeof(CoopSyncFixes).GetMethod(nameof(BeginGeneratedRouteGraph),Flags));il.Emit(OpCodes.Stloc,frame);
        il.BeginExceptionBlock();
        for(int i=0;i<types.Length;i++)il.Emit(OpCodes.Ldarg,(short)i);
        il.Emit(OpCodes.Callvirt,types[0].GetMethod("Invoke"));il.Emit(OpCodes.Stloc,result);
        il.Emit(OpCodes.Ldloc,frame);il.Emit(OpCodes.Ldloc,result);il.Emit(OpCodes.Call,typeof(CoopSyncFixes).GetMethod(nameof(ValidateGeneratedRouteGraph),Flags));
        il.BeginFinallyBlock();il.Emit(OpCodes.Ldloc,frame);il.Emit(OpCodes.Call,typeof(CoopSyncFixes).GetMethod(nameof(EndGeneratedRouteGraph),Flags));il.EndExceptionBlock();
        il.Emit(OpCodes.Ldloc,result);il.Emit(OpCodes.Ret);
        GeneratedRouteGraphHook=dm.CreateDelegate(ev.EventHandlerType);RuntimeHelpers.PrepareDelegate(GeneratedRouteGraphHook);ev.AddEventHandler(null,GeneratedRouteGraphHook);
    }
    private static GeneratedRouteGraphFrame BeginGeneratedRouteGraph(Game game,Level parent)
    {
        var frame=new GeneratedRouteGraphFrame {Previous=GeneratedRouteGraph};
        // Clear an outer scope for an unrelated nested generator. Its finally will
        // restore the exact previous scope even when native generation throws.
        GeneratedRouteGraph=null;
        try
        {
        var net=ModEntry._net;
        if(Disabled("ROUTE_GENERATED_GRAPH") || !PendingWorld(net,out var state) || state.Route==null ||
            !state.Routed || state.ActivationOnly || game==null || game.destroyed || game.HashlinkPointer!=Game.Class.ME?.HashlinkPointer)return frame;
        var source=SecretParentMap(game,parent);var slot=state.RouteLoadingSlot;
        if(source==null || slot<0 || slot>=state.Route.Steps.Count || slot+1>=state.Route.Graphs.Count)return frame;
        var load=GeneratedRouteLoad;
        var currentLoad=load!=null && ReferenceEquals(load.Net,net) && ReferenceEquals(load.Join,state) && ReferenceEquals(load.Header,state.Header) &&
            ReferenceEquals(load.Bundle,state.Route) && load.SourceSlot==slot && load.Game==game.HashlinkPointer && load.SourceImported &&
            (load.Parent==null || load.Parent.HashlinkPointer==source.HashlinkPointer);
        var expected=state.Route.Graphs[slot];var child=state.Route.Graphs[slot+1];var step=state.Route.Steps[slot];var actual=DescribeRouteMap(source);
        if(!RouteGeneratedGraphGuard.CanEnter(state.Routed,state.ActivationOnly,slot,state.Route.Steps.Count,state.RouteAppliedSlots.Contains(slot),currentLoad,
            step.Kind,step.IsExit,child.Map.Level,child.Map.Branch,child.Sequence,
            new RouteGraphIdentity(expected.Map.Level,expected.Map.Seed,expected.Map.Branch),new RouteGraphIdentity(actual.Level,actual.Seed,actual.Branch)) ||
            step.PortalType!=typeof(dc.en.inter.Portal).FullName || !SameRouteMap(step.Source,expected.Map) || !SameRouteMap(step.Destination,child.Map))return frame;
        load.Parent=source;
        frame.Entered=new GeneratedRouteGraphScope {Net=net,Join=state,Header=state.Header,Bundle=state.Route,ParentSlot=slot,ChildSlot=slot+1,Parent=source,Game=game.HashlinkPointer,
            Load=load,Receipt=new RouteGeneratedGraphReceipt(child.Slot,child.Sequence)};
        GeneratedRouteGraph=frame.Entered;
        Log.Information("[CoopSyncFixes] Scoped eager route graph source={Source} slot={Slot} level={Level} sequence={Sequence}",expected.Map.Level,child.Slot,child.Map.Level,child.Sequence);
        return frame;
        }
        catch
        {
            // Scope selection runs before the emitted native-call try/finally.
            // Restore here too if inspection or logging throws during entry.
            GeneratedRouteGraph=frame.Previous;throw;
        }
    }
    private static bool TryGetGeneratedRouteGraph(string levelId,out RouteGraph graph)
    {
        graph=null;var scope=GeneratedRouteGraph;
        if(scope==null || !ReferenceEquals(ModEntry._net,scope.Net) || !PendingWorld(scope.Net,out var current) ||
            !ReferenceEquals(GeneratedRouteLoad,scope.Load) || !scope.Load.SourceImported || scope.Load.Parent?.HashlinkPointer!=scope.Parent.HashlinkPointer ||
            !ReferenceEquals(current,scope.Join) || !ReferenceEquals(current.Header,scope.Header) || !ReferenceEquals(current.Route,scope.Bundle) ||
            !current.Routed || current.ActivationOnly || current.RouteLoadingSlot!=scope.ParentSlot || !current.RouteAppliedSlots.Contains(scope.ParentSlot) ||
            Game.Class.ME?.HashlinkPointer!=scope.Game)return false;
        var child=scope.Bundle.Graphs[scope.ChildSlot];
        if(levelId!=child.Map.Level || !SameRouteMap(DescribeRouteMap(scope.Parent),scope.Bundle.Graphs[scope.ParentSlot].Map))return false;
        graph=child;return true;
    }
    private static void PinGeneratedRouteSeed(string levelId)
    {
        if(TryGetGeneratedRouteGraph(levelId,out var graph))PinOneWorldGraph(graph.Map.Level,graph.SeedPayload,graph.Json);
    }
    private static void ValidateGeneratedRouteGraph(GeneratedRouteGraphFrame frame,LevelMap result)
    {
        var scope=frame.Entered;if(scope==null)return;
        var expected=scope.Bundle.Graphs[scope.ChildSlot];
        if(!ReferenceEquals(GeneratedRouteGraph,scope) || !TryGetGeneratedRouteGraph(expected.Map.Level,out var pinned) || !ReferenceEquals(pinned,expected) ||
            result==null || !SameRouteMap(DescribeRouteMap(result),expected.Map) || !scope.Receipt.Accepted || !scope.Join.RouteAppliedSlots.Contains(scope.ChildSlot))
            throw new InvalidOperationException("Committed eager Challenge graph identity/application was not verified");
        Log.Information("[CoopSyncFixes] Eager route graph applied slot={Slot} level={Level} sequence={Sequence}",expected.Slot,expected.Map.Level,expected.Sequence);
    }
    private static void EndGeneratedRouteGraph(GeneratedRouteGraphFrame frame)=>GeneratedRouteGraph=frame.Previous;
}
