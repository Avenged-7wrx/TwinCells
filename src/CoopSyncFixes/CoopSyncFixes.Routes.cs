using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using dc.level;
using dc.pr;
using dc.en.inter;
using DeadCellsMultiplayerMod;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Hashlink.Virtuals;

public sealed partial class CoopSyncFixes
{
    private sealed class RouteMapIdentity {public string Level,Branch;public double Seed;}
    private sealed class RouteGraph {public int Slot;public RouteMapIdentity Map;public long Sequence;public string SeedPayload,Json,Hash;}
    private sealed class RouteStep
    {
        public string Kind,PortalType;public int Link,Room,X,Y;public bool IsExit;
        public RouteMapIdentity Source,Destination;
    }
    private sealed class RouteBundle
    {public int V=1;public RouteMapIdentity Root,Target;public List<RouteStep> Steps=new();public List<RouteGraph> Graphs=new();}
    private sealed class NativeRouteMap {public LevelMap Map;public RouteGraph Graph;}
    private sealed class NativeRouteEdge {public LevelMap Source,Destination;public RouteStep Step;}
    private sealed class PortalRouteIntent {public LevelMap Source,Destination;public Portal Portal;public string Type;public int Room,X,Y;public bool IsExit;public long Expires;}
    private sealed class NativeRouteWorld
    {
        public LevelMap Root;public string MainLoading;public NativeRouteEdge Pending;
        public bool Recovered;
        public PortalRouteIntent Portal;
        public readonly Dictionary<IntPtr,NativeRouteMap> Maps=new();public readonly List<NativeRouteEdge> Edges=new();
    }
    private static readonly ConditionalWeakTable<NetNode,NativeRouteWorld> NativeRoutes=new();
    private sealed class NativeGraphArchive {public IntPtr Game;public readonly Dictionary<IntPtr,NativeRouteMap> Maps=new();}
    private static readonly ConditionalWeakTable<NetNode,NativeGraphArchive> NativeRouteGraphs=new();
    private static readonly List<Delegate> RouteHooks=new();
    private static void InitializeRoutes()
    {
        InitializeSecretFamily();
        InitializeGeneratedRouteGraphs();
        RouteHook(typeof(Game).Assembly.GetType("dc.pr.Hook_Game",true).GetEvent("loadMainLevel"),nameof(BeforeRouteMain),new[]{3});
        RouteHook(typeof(Game).Assembly.GetType("dc.pr.Hook_Game",true).GetEvent("activateSubLevel"),nameof(BeforeRouteSub),new[]{2,3});
        RouteHook(typeof(Game).Assembly.GetType("dc.cine.Hook_LevelTransition",true).GetEvent("loadNewLevel"),nameof(BeforeRouteLoad),new[]{1});
        foreach(var type in typeof(Portal).Assembly.GetTypes().Where(t=>typeof(Portal).IsAssignableFrom(t)))
        {
            var hook=type.Assembly.GetType(type.Namespace+".Hook_"+type.Name)?.GetEvent("onActivate");
            if(hook!=null)RouteHook(hook,nameof(BeforeRoutePortal),new[]{1});
        }
    }
    private static void RouteHook(EventInfo hook,string before,int[] indices)
    {
        var signature=hook.EventHandlerType.GetMethod("Invoke");var types=signature.GetParameters().Select(p=>p.ParameterType).ToArray();
        var adapter=new DynamicMethod("CoopRoute_"+before+"_"+RouteHooks.Count,signature.ReturnType,types,typeof(CoopSyncFixes),true);var il=adapter.GetILGenerator();
        foreach(var i in indices)il.Emit(OpCodes.Ldarg,(short)i);
        il.Emit(OpCodes.Call,typeof(CoopSyncFixes).GetMethod(before,Flags));
        // Native Ref<bool> arguments remain on the IL stack; never box them.
        for(int i=0;i<types.Length;i++)il.Emit(OpCodes.Ldarg,(short)i);
        il.Emit(OpCodes.Callvirt,types[0].GetMethod("Invoke"));il.Emit(OpCodes.Ret);
        var value=adapter.CreateDelegate(hook.EventHandlerType);RuntimeHelpers.PrepareDelegate(value);RouteHooks.Add(value);hook.AddEventHandler(null,value);
    }
    private static void BeforeRouteMain(dc.String id)
    {
        var net=ModEntry._net;if(net?.IsHost!=true)return;
        var world=NativeRoutes.GetValue(net,_=>new NativeRouteWorld());world.MainLoading=id?.ToString();world.Pending=null;
    }
    private static void BeforeRoutePortal(Portal portal)
    {
        var net=ModEntry._net;var source=Game.Class.ME?.hero?._level?.map;
        if(net?.IsHost!=true || portal==null || source==null)return;
        NativeRoutes.GetValue(net,_=>new NativeRouteWorld()).Portal=new PortalRouteIntent {Source=source,Destination=portal.destMap,Portal=portal,Type=portal.GetType().FullName,
            X=portal.cx,Y=portal.cy,Room=source.getRoomAt(portal.cx,portal.cy)?.id??-1,IsExit=portal.isExit,Expires=Environment.TickCount64+30000};
    }
    private static void BeforeRouteSub(LevelMap destination,int? link)
    {
        var net=ModEntry._net;var level=Game.Class.ME?.hero?._level;var source=level?.map;
        if(net?.IsHost!=true || source==null || destination==null || source.HashlinkPointer==destination.HashlinkPointer)return;
        var world=NativeRoutes.GetValue(net,_=>new NativeRouteWorld());
        if(world.Root==null && !level.isSubLevel && !level.isSecret)world.Root=source;
        CacheRouteMap(net,world,source);
        var step=new RouteStep {Source=DescribeRouteMap(source),Destination=DescribeRouteMap(destination)};
        var intent=world.Portal;
        if(intent!=null && intent.Destination==null && intent.Expires>=Environment.TickCount64)intent.Destination=intent.Portal?.destMap;
        if(link.HasValue && FindZRouteMap(source,link.Value)?.HashlinkPointer==destination.HashlinkPointer){step.Kind="ZLink";step.Link=link.Value;}
        else if(intent!=null && intent.Expires>=Environment.TickCount64 && intent.Source.HashlinkPointer==source.HashlinkPointer && intent.Destination?.HashlinkPointer==destination.HashlinkPointer)
        {
            step.Kind="Portal";step.PortalType=intent.Type;step.X=intent.X;step.Y=intent.Y;step.Room=intent.Room;step.IsExit=intent.IsExit;
        }
        else if(world.Pending?.Step?.Kind=="Portal" && world.Pending.Source.HashlinkPointer==source.HashlinkPointer &&
            world.Pending.Destination.HashlinkPointer==destination.HashlinkPointer)return;
        else {world.Pending=null;return;}
        world.Pending=new NativeRouteEdge {Source=source,Destination=destination,Step=step};
    }
    private static void BeforeRouteLoad(dc.cine.LevelTransition transition)
    {if(transition?.map!=null){var link=transition.GetType().GetProperty("linkId").GetValue(transition);BeforeRouteSub(transition.map,link==null?null:Convert.ToInt32(link));}}
    private static RouteMapIdentity DescribeRouteMap(LevelMap map)
    {
        var ids=new SortedSet<int>();bool sub=false;
        if(map.rooms!=null)for(int i=0;i<map.rooms.length;i++)if(map.rooms.getDyn(i) is Room room && room.isZChild)
        {sub=true;if(room.zLinks!=null)for(int j=0;j<room.zLinks.length;j++)if(room.zLinks.getDyn(j) is virtual_contentClue_doorId_id_map_ link)ids.Add(link.id);}
        return new RouteMapIdentity {Level=map.id.ToString(),Seed=map.seed,Branch=sub?"SUB:"+string.Join(",",ids):"MAIN"};
    }
    private static bool SameRouteMap(RouteMapIdentity a,RouteMapIdentity b)=>a!=null && b!=null && a.Level==b.Level && a.Branch==b.Branch && BitConverter.DoubleToInt64Bits(a.Seed)==BitConverter.DoubleToInt64Bits(b.Seed);
    private static LevelMap FindZRouteMap(LevelMap source,int id)
    {
        LevelMap found=null;
        if(source?.rooms!=null)for(int i=0;i<source.rooms.length;i++)if(source.rooms.getDyn(i) is Room room && room.zLinks!=null)
            for(int j=0;j<room.zLinks.length;j++)if(room.zLinks.getDyn(j) is virtual_contentClue_doorId_id_map_ link && link.id==id && link.map!=null)
            {if(found!=null && found.HashlinkPointer!=link.map.HashlinkPointer)throw new InvalidOperationException("Ambiguous native route boundary");found=link.map;}
        return found;
    }
    private static bool CacheRouteMap(NetNode net,NativeRouteWorld world,LevelMap map)
    {
        if(world.Maps.TryGetValue(map.HashlinkPointer,out var prior) && prior.Graph!=null)return true;
        var game=Game.Class.ME;var archive=NativeRouteGraphs.GetValue(net,_=>new NativeGraphArchive());
        if(game==null || game.destroyed)return false;
        if(archive.Game!=game.HashlinkPointer){archive.Maps.Clear();archive.Game=game.HashlinkPointer;}
        if(archive.Maps.TryGetValue(map.HashlinkPointer,out var frozen)){world.Maps[map.HashlinkPointer]=frozen;return true;}
        // Only a native active-map observation may bind the current producer cache.
        // Recovery must use an already frozen binding, never latest-by-LevelId.
        if(game.hero?._level?.map?.HashlinkPointer!=map.HashlinkPointer)return false;
        string graph,seed;
        lock(NetField(net,"_hostCacheSync"))
        {graph=((IDictionary)NetField(net,"_cachedHostLevelGraphsByLevelId"))[map.id.ToString()] as string;seed=((IDictionary)NetField(net,"_cachedHostLevelSeedsByLevelId"))[map.id.ToString()] as string;}
        if(string.IsNullOrEmpty(graph) || string.IsNullOrEmpty(seed))return false;
        var info=JObject.Parse(graph);var parts=seed.Split('|');
        // The legacy seed payload is the level generator RNG state, not LevelMap.seed.
        if((string)info["LevelId"]!=map.id.ToString() || parts.Length!=3 || parts[0]!=map.id.ToString() || !double.TryParse(parts[2],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var n) || !double.IsFinite(n))return false;
        if(world.Maps.Count>=256)throw new InvalidOperationException("Route native-map capacity exceeded");
        var family=NativeMapFamily(map);
        if(!family.Keys.Any(archive.Maps.ContainsKey))archive.Maps.Clear();
        foreach(var member in family.Values.Where(m=>m.id.ToString()==map.id.ToString() && m.seed==map.seed))
        {
            if(archive.Maps.ContainsKey(member.HashlinkPointer))continue;
            if(archive.Maps.Count>=256)throw new InvalidOperationException("Frozen native graph capacity exceeded");
            archive.Maps[member.HashlinkPointer]=new NativeRouteMap {Map=member,Graph=new RouteGraph {Map=DescribeRouteMap(member),Sequence=(long)info["Seq"],SeedPayload=seed,Json=graph,Hash=HashText(graph)}};
        }
        world.Maps[map.HashlinkPointer]=archive.Maps[map.HashlinkPointer];
        return true;
    }
    private static void TickNativeRoutes(NetNode net)
    {
        if(net?.IsHost!=true)return;var game=Game.Class.ME;var level=game?.hero?._level;var map=level?.map;
        if(map==null || game.curCine!=null || !(bool)Field("s_levelIdentityReady") || (Field("currentLevel") as Level)?.HashlinkPointer!=level.HashlinkPointer)return;
        var world=NativeRoutes.GetValue(net,_=>new NativeRouteWorld());
        if(world.MainLoading!=null && world.MainLoading==map.id.ToString() && !level.isSubLevel && !level.isSecret)
        {world.Root=map;world.Maps.Clear();world.Edges.Clear();world.Pending=null;world.Portal=null;world.MainLoading=null;world.Recovered=false;NativeRouteGraphs.Remove(net);}
        if(world.Root==null && !level.isSubLevel && !level.isSecret)world.Root=map;
        if(world.Root==null && (level.isSubLevel || level.isSecret) && world.MainLoading==null)TryRecoverZRoute(net,world,level);
        CacheRouteMap(net,world,map);
        if(world.Pending is NativeRouteEdge edge && edge.Destination.HashlinkPointer==map.HashlinkPointer)
        {
            if(!world.Edges.Any(e=>e.Source.HashlinkPointer==edge.Source.HashlinkPointer && e.Destination.HashlinkPointer==edge.Destination.HashlinkPointer))world.Edges.Add(edge);
            world.Pending=null;world.Portal=null;
        }
    }
    private static bool TryRecoverZRoute(NetNode net,NativeRouteWorld world,Level active)
    {
        var game=Game.Class.ME;
        if(game==null || game.destroyed || active==null || active.destroyed || active.game?.HashlinkPointer!=game.HashlinkPointer || active.map==null)return false;
        // from is null on the pinned native Z-levels. Establish membership through
        // actual map links and live retained Level objects owned by this Game.
        Dictionary<IntPtr,LevelMap> family;
        try{family=NativeMapFamily(active.map);}catch(InvalidOperationException){return false;}
        var roots=new Dictionary<IntPtr,LevelMap>();
        void Candidate(object value)
        {
            if(value is not Level level)return;
            try
            {
                if(!level.destroyed && !level.isSubLevel && !level.isSecret && level.game?.HashlinkPointer==game.HashlinkPointer &&
                    level.map!=null && family.ContainsKey(level.map.HashlinkPointer) && DescribeRouteMap(level.map).Branch=="MAIN")roots[level.map.HashlinkPointer]=level.map;
            }
            catch { /* A disposed retained wrapper cannot establish a route root. */ }
        }
        if(game.subLevels!=null)for(int i=0;i<game.subLevels.length;i++)Candidate(game.subLevels.getDyn(i));
        if(game.children!=null)for(int i=0;i<game.children.length;i++)Candidate(game.children.getDyn(i));
        if(roots.Count!=1)return false;
        var root=roots.Values.Single();var pending=new Queue<(LevelMap Map,List<NativeRouteEdge> Path)>();
        var visited=new HashSet<IntPtr>{root.HashlinkPointer};pending.Enqueue((root,new()));
        while(pending.Count>0)
        {
            var at=pending.Dequeue();
            if(at.Map.HashlinkPointer==active.map.HashlinkPointer)
            {
                var maps=new List<LevelMap>{root};maps.AddRange(at.Path.Select(e=>e.Destination));
                if(!NativeRouteGraphs.TryGetValue(net,out var archive) || archive.Game!=game.HashlinkPointer || maps.Any(m=>!archive.Maps.ContainsKey(m.HashlinkPointer)))return false;
                foreach(var map in maps)world.Maps[map.HashlinkPointer]=archive.Maps[map.HashlinkPointer];
                world.Root=root;world.Edges.AddRange(at.Path);world.Recovered=true;return true;
            }
            if(at.Path.Count>=16)continue;
            var ids=new SortedSet<int>();
            if(at.Map.rooms!=null)for(int i=0;i<at.Map.rooms.length;i++)if(at.Map.rooms.getDyn(i) is Room room && room.zLinks!=null)
                for(int j=0;j<room.zLinks.length;j++)if(room.zLinks.getDyn(j) is virtual_contentClue_doorId_id_map_ link && link.id>=0 && link.map!=null)ids.Add(link.id);
            if(ids.Count>1024)return false;
            foreach(var id in ids)
            {
                LevelMap destination;try{destination=FindZRouteMap(at.Map,id);}catch(InvalidOperationException){return false;}
                if(destination==null || !family.ContainsKey(destination.HashlinkPointer) || !visited.Add(destination.HashlinkPointer))continue;
                var edge=new NativeRouteEdge {Source=at.Map,Destination=destination,Step=new RouteStep {Kind="ZLink",Link=id,Source=DescribeRouteMap(at.Map),Destination=DescribeRouteMap(destination)}};
                var path=new List<NativeRouteEdge>(at.Path){edge};pending.Enqueue((destination,path));
            }
        }
        return false;
    }
    private static string CaptureRouteBundle(NetNode net,Level target)
    {
        TickNativeRoutes(net);if(!NativeRoutes.TryGetValue(net,out var world) || world.Root==null)return null;
        var paths=new Queue<(LevelMap Map,List<NativeRouteEdge> Edges)>();var seen=new HashSet<IntPtr>();paths.Enqueue((world.Root,new()));
        while(paths.Count>0)
        {
            var at=paths.Dequeue();if(!seen.Add(at.Map.HashlinkPointer))continue;
            if(at.Map.HashlinkPointer==target.map.HashlinkPointer)
            {
                var bundle=new RouteBundle {Root=DescribeRouteMap(world.Root),Target=DescribeRouteMap(target.map)};
                var maps=new List<LevelMap> {world.Root};foreach(var edge in at.Edges){bundle.Steps.Add(edge.Step);maps.Add(edge.Destination);}
                for(int i=0;i<maps.Count;i++)
                {
                    if(!CacheRouteMap(net,world,maps[i]))return null;
                    var stored=world.Maps[maps[i].HashlinkPointer].Graph;
                    bundle.Graphs.Add(new RouteGraph {Slot=i,Map=DescribeRouteMap(maps[i]),Sequence=stored.Sequence,SeedPayload=stored.SeedPayload,Json=stored.Json,Hash=stored.Hash});
                }
                return JsonConvert.SerializeObject(bundle);
            }
            if(at.Edges.Count>=16)continue;
            foreach(var edge in world.Edges.Where(e=>e.Source.HashlinkPointer==at.Map.HashlinkPointer))
            {var next=new List<NativeRouteEdge>(at.Edges){edge};paths.Enqueue((edge.Destination,next));}
        }
        return null;
    }
    private static RouteBundle ValidateRouteBundle(string text,WorldHeader header)
    {
        var bundle=JsonConvert.DeserializeObject<RouteBundle>(text);
        bool Valid(RouteMapIdentity m)=>m!=null && !string.IsNullOrEmpty(m.Level) && m.Level.Length<=128 && !string.IsNullOrEmpty(m.Branch) && m.Branch.Length<=512 && double.IsFinite(m.Seed);
        if(bundle?.V!=1 || !Valid(bundle.Root) || !Valid(bundle.Target) || bundle.Target.Level!=header.Level || BitConverter.DoubleToInt64Bits(bundle.Target.Seed)!=BitConverter.DoubleToInt64Bits(header.Seed) ||
            bundle.Steps==null || bundle.Steps.Count<1 || bundle.Steps.Count>16 || bundle.Graphs==null || bundle.Graphs.Count!=bundle.Steps.Count+1 || HashText(JsonConvert.SerializeObject(bundle.Steps))!=header.RouteHash)throw new InvalidOperationException("Invalid world route identity");
        var current=bundle.Root;
        for(int i=0;i<bundle.Graphs.Count;i++)
        {
            var graph=bundle.Graphs[i];
            if(graph==null || graph.Slot!=i || !SameRouteMap(graph.Map,current) || graph.Sequence<=0 || graph.Json==null || graph.Json.Length>1000000 || HashText(graph.Json)!=graph.Hash)throw new InvalidOperationException("Invalid route graph slot");
            var json=JObject.Parse(graph.Json);var seed=graph.SeedPayload?.Split('|');
            if((string)json["LevelId"]!=current.Level || (long)json["Seq"]!=graph.Sequence || seed?.Length!=3 || seed[0]!=current.Level ||
                !long.TryParse(seed[1],System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out var seedSequence) || seedSequence<=0 ||
                !double.TryParse(seed[2],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var n) || !double.IsFinite(n))throw new InvalidOperationException("Route graph/seed mismatch");
            if(i==bundle.Steps.Count)break;
            var step=bundle.Steps[i];
            if(step==null || !SameRouteMap(step.Source,current) || !Valid(step.Destination) || step.Kind!="ZLink" && step.Kind!="Portal" ||
                step.Kind=="ZLink" && step.Link<0 || step.Kind=="Portal" && (string.IsNullOrEmpty(step.PortalType) || step.PortalType.Length>256 || step.Room<0))throw new InvalidOperationException("Invalid route step");
            current=step.Destination;
        }
        if(!SameRouteMap(current,bundle.Target) || bundle.Graphs[bundle.Graphs.Count-1].Sequence!=header.GraphSequence)throw new InvalidOperationException("Route does not reach target");
        return bundle;
    }
    private static void PinRouteSlot(WorldJoin state,int slot)
    {
        state.RouteLoadingSlot=slot;var graph=state.Route.Graphs[slot];PinOneWorldGraph(graph.Map.Level,graph.SeedPayload,graph.Json);
    }
    private static void PinRouteImport(string levelId)
    {
        if(TryGetGeneratedRouteGraph(levelId,out var generated))
        {PinOneWorldGraph(generated.Map.Level,generated.SeedPayload,generated.Json);return;}
        if(PendingWorld(ModEntry._net,out var state) && !state.ActivationOnly && state.Routed && state.Route!=null && state.Route.Graphs[state.RouteLoadingSlot].Map.Level==levelId)
            PinRouteSlot(state,state.RouteLoadingSlot);
    }
    private static void TickClientRoute(WorldJoin state)
    {
        var game=Game.Class.ME;var level=game?.hero?._level;
        // A native reveal/transition must finish; a no-op activation is not permission
        // to repeat its script or wait for the full world-transfer timeout.
        if(!Disabled("ROUTE_PORTAL"))state.RoutePortal?.CheckDeadline(Environment.TickCount64);
        if(state.Route==null || state.ActivationOnly || level?.map==null || game.curCine!=null || !state.Routed)return;
        if(state.RouteExpected!=IntPtr.Zero)
        {
            if(state.RoutePortal!=null && !Disabled("ROUTE_PORTAL"))
                state.RoutePortal.CheckMap(level.map.HashlinkPointer.ToInt64());
            if(level.map.HashlinkPointer!=state.RouteExpected)return;
            if(!SameRouteMap(DescribeRouteMap(level.map),state.Route.Graphs[state.RouteCursor+1].Map))throw new InvalidOperationException("Native route entered wrong map");
            var destination=state.Route.Graphs[state.RouteCursor+1];
            if(!state.RouteAppliedSlots.Contains(destination.Slot))
            {
                var inheritedStep=state.Route.Steps[state.RouteCursor];
                if(inheritedStep.Kind!="ZLink" || !state.RouteAppliedSlots.Any(i=>state.Route.Graphs[i].Hash==destination.Hash && state.Route.Graphs[i].Sequence==destination.Sequence && state.Route.Graphs[i].Map.Level==destination.Map.Level && state.Route.Graphs[i].Map.Seed==destination.Map.Seed))throw new InvalidOperationException("Destination route graph was not applied");
                state.RouteAppliedSlots.Add(destination.Slot);
            }
            state.RouteCursor++;state.RouteExpected=IntPtr.Zero;state.RoutePortal=null;
        }
        if(state.RouteCursor<0)
        {
            if(!state.RouteRootApplied || !SameRouteMap(DescribeRouteMap(level.map),state.Route.Root))return;
            state.RouteCursor=0;
        }
        if(state.RouteCursor==state.Route.Steps.Count){state.GraphApplied=true;return;}
        var step=state.Route.Steps[state.RouteCursor];
        if(!SameRouteMap(DescribeRouteMap(level.map),step.Source))throw new InvalidOperationException("Native route source changed");
        PinRouteSlot(state,state.RouteCursor+1);
        if(step.Kind=="ZLink")
        {
            var dest=FindZRouteMap(level.map,step.Link);
            if(dest==null || !SameRouteMap(DescribeRouteMap(dest),step.Destination))throw new InvalidOperationException("Missing exact native Z boundary");
            state.RouteExpected=dest.HashlinkPointer;
            var functions=typeof(dc.cine.LevelTransition).GetProperty("Class").GetValue(null);var call=functions.GetType().GetProperty("gotoSub").GetValue(functions);
            call.GetType().GetMethod("Invoke").Invoke(call,new object[]{dest,(int?)step.Link});
        }
        else
        {
            var portals=new List<Portal>();
            var triggers=level.GetType().GetProperty("triggers",InstanceFlags)?.GetValue(level) ?? level.GetType().GetField("triggers",InstanceFlags)?.GetValue(level);
            foreach(var list in new object[]{level.entities,triggers})if(list!=null)
            {
                var count=Convert.ToInt32(list.GetType().GetProperty("length").GetValue(list));var get=list.GetType().GetMethod("getDyn");
                for(int i=0;i<count;i++)if(get.Invoke(list,new object[]{i}) is Portal portal && !portal.destroyed && !portals.Any(p=>p.HashlinkPointer==portal.HashlinkPointer))portals.Add(portal);
            }
            var matches=portals.Where(p=>p.GetType().FullName==step.PortalType && p.cx==step.X && p.cy==step.Y && p.isExit==step.IsExit && level.map.getRoomAt(p.cx,p.cy)?.id==step.Room && p.destMap!=null && SameRouteMap(DescribeRouteMap(p.destMap),step.Destination)).ToArray();
            if(matches.Length!=1)throw new InvalidOperationException("Missing unique native portal boundary");
            var selectedPortal=matches[0];
            if(Disabled("ROUTE_PORTAL"))
            {
                state.RouteExpected=selectedPortal.destMap.HashlinkPointer;selectedPortal.onActivate(game.hero,false);
                return;
            }
            ReplayCommittedPortal(state,step,game,level,selectedPortal);
        }
    }

    private static void ReplayCommittedPortal(WorldJoin state,RouteStep step,Game game,Level level,Portal portal)
    {
        // Route steps enter the bundle only after the host actually committed the
        // source -> destination native boundary. Catch-up may replay that entrance's
        // reveal, never an arbitrary hidden interaction elsewhere in the map.
        if(portal._level?.HashlinkPointer!=level.HashlinkPointer ||
            game.curLevel?.HashlinkPointer!=level.HashlinkPointer ||
            level.game?.HashlinkPointer!=game.HashlinkPointer)
            throw new InvalidOperationException("Portal route no longer owns its active native level");
        var hero=game.hero;
        bool live=hero!=null && !hero.destroyed && hero.life>0 &&
            hero._level?.HashlinkPointer==level.HashlinkPointer &&
            ModEntry.me?.HashlinkPointer==hero.HashlinkPointer &&
            !(bool)typeof(ModEntry).GetMethod("IsLocalPlayerDowned",Flags).Invoke(null,null);
        var now=Environment.TickCount64;
        state.RoutePortal??=new RoutePortalProgress(level.map.HashlinkPointer.ToInt64(),
            portal.destMap.HashlinkPointer.ToInt64(),portal.HashlinkPointer.ToInt64(),now);
        SecretLever reveal=null;
        if(!portal.visible && !state.RoutePortal.RevealRequested)
        {
            // Only the ordinary Challenge entrance has a proven native SecretLever
            // contract. Other portal classes/exit effects remain outside this repair.
            if(step.PortalType!=typeof(Portal).FullName || step.IsExit ||
                step.Destination.Level!="Challenge" || level.isSecret)
                throw new InvalidOperationException("Hidden portal has no supported committed reveal");
            var linked=new List<SecretLever>();
            if(level.entities!=null)for(int i=0;i<level.entities.length;i++)
                if(level.entities.getDyn(i) is SecretLever lever && !lever.destroyed &&
                    lever._level?.HashlinkPointer==level.HashlinkPointer &&
                    lever.portal?.HashlinkPointer==portal.HashlinkPointer &&
                    !linked.Any(p=>p.HashlinkPointer==lever.HashlinkPointer))linked.Add(lever);
            if(linked.Count!=1 || linked[0].isUsed)
                throw new InvalidOperationException("Missing unique unused SecretLever for committed portal");
            reveal=linked[0];
        }
        var action=state.RoutePortal.Advance(now,level.map.HashlinkPointer.ToInt64(),
            portal.destMap.HashlinkPointer.ToInt64(),portal.HashlinkPointer.ToInt64(),portal.visible,live,reveal!=null);
        if(action==RoutePortalAction.Reveal)
        {
            // Claim before entering native code: even a callback that throws after
            // a side effect can never be replayed by another frame.
            reveal.onActivate(hero,false);
            return; // SecretExitOpening owns the cinematic, isUsed and Portal.show.
        }
        if(action==RoutePortalAction.Activate)
        {
            state.RouteExpected=portal.destMap.HashlinkPointer;
            portal.onActivate(hero,false);
        }
    }
}
