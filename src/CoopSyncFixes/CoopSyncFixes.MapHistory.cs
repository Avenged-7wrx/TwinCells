using System;
using System.Collections.Generic;
using dc.en;
using dc.level;
using Hashlink.Virtuals;
using System.Runtime.CompilerServices;
using DeadCellsMultiplayerMod;

public sealed partial class CoopSyncFixes
{
    private sealed class NativeBirth {public dc.en.Mob Native;public MobBirth Birth;public int Uid;}
    private sealed class MapHistory
    {
        public LevelMap Map;public MobManifest Manifest;
        public readonly Dictionary<IntPtr,NativeBirth> Births=new();
    }
    private static Dictionary<IntPtr,LevelMap> NativeMapFamily(LevelMap map)
    {
        // Room links are the native retained-map topology. A regenerated map with
        // the same id/seed is a different object and cannot inherit this history.
        var connected=new Dictionary<IntPtr,LevelMap>();var queue=new Queue<LevelMap>();queue.Enqueue(map);
        while(queue.Count>0)
        {
            var current=queue.Dequeue();if(current==null || connected.ContainsKey(current.HashlinkPointer))continue;
            if(connected.Count>=256)throw new InvalidOperationException("Native map-family capacity exceeded");
            connected.Add(current.HashlinkPointer,current);
            foreach(var secret in NativeSecretNeighbors(dc.pr.Game.Class.ME,current))queue.Enqueue(secret);
            if(current.rooms==null)continue;
            for(int i=0;i<current.rooms.length;i++)if(current.rooms.getDyn(i) is Room room && room.zLinks!=null)
                for(int j=0;j<room.zLinks.length;j++)
                    if(room.zLinks.getDyn(j) is virtual_contentClue_doorId_id_map_ link && link.map!=null)queue.Enqueue(link.map);
        }
        return connected;
    }
    private static MapHistory GetMapHistory(HostManifestStore store,LevelMap map)
    {
        var connected=NativeMapFamily(map);
        bool related=false;foreach(var pointer in connected.Keys)if(store.Maps.ContainsKey(pointer)){related=true;break;}
        // Crossing to a new native family discards old biome history, instead of
        // retaining one archive per epoch until the end of a long run.
        if(!related)store.Maps.Clear();
        foreach(var pair in connected)
            if(!store.Maps.ContainsKey(pair.Key))store.Maps.Add(pair.Key,new MapHistory {Map=pair.Value});
        if(store.Maps.Count>256)throw new InvalidOperationException("Retained map history capacity exceeded");
        int births=0;foreach(var item in store.Maps.Values)births+=item.Births.Count;
        if(births>65536)throw new InvalidOperationException("Retained enemy history capacity exceeded");
        return store.Maps[map.HashlinkPointer];
    }
    private sealed class ClientBirthStore
    {
        public string RunKey;
        public readonly Dictionary<IntPtr,ClientBirthMap> Maps=new();
    }
    private sealed class ClientBirthMap
    {
        public LevelMap Map;
        public readonly Dictionary<int,NativeBirth> Births=new();
    }
    private static readonly ConditionalWeakTable<NetNode,ClientBirthStore> ClientBirths=new();
    private static ClientBirthMap CurrentClientBirthMap()
    {
        var net=ModEntry._net;var level=Field("currentLevel") as dc.pr.Level;
        if(net==null || net.IsHost || level?.map==null)return null;
        var descriptor=Coordinator.GetMethod("GetCurrentRemoteDescriptor",Flags).Invoke(null,null);
        if(descriptor==null)return null;
        var run=Property(descriptor,"SessionId")+":"+Property(descriptor,"RunId")+":"+Property(descriptor,"Sequence");
        var store=ClientBirths.GetValue(net,_=>new ClientBirthStore());
        if(store.RunKey!=run){store.Maps.Clear();store.RunKey=run;}
        var family=NativeMapFamily(level.map);bool related=false;
        foreach(var pointer in family.Keys)if(store.Maps.ContainsKey(pointer)){related=true;break;}
        if(!related)store.Maps.Clear();
        foreach(var pair in family)if(!store.Maps.ContainsKey(pair.Key))store.Maps.Add(pair.Key,new ClientBirthMap {Map=pair.Value});
        return store.Maps[level.map.HashlinkPointer];
    }
    private static void SaveVerifiedClientBirths()
    {
        // Records are learned only from a verified full manifest. Never infer a
        // host birth identity from local spawn order or from a spatial fallback.
        var net=ModEntry._net;if(net==null || net.IsHost || !ClientBirths.TryGetValue(net,out var store))return;
        foreach(var map in store.Maps.Values)
        {
            var remove=new List<int>();
            foreach(var pair in map.Births)
                if(pair.Value.Native==null || pair.Value.Native.destroyed || pair.Value.Native.__uid!=pair.Value.Uid || pair.Value.Birth.Dead)remove.Add(pair.Key);
            foreach(var id in remove)map.Births.Remove(id);
        }
    }
    private static dc.en.Mob FindVerifiedClientBirth(MobBirth item,HashSet<dc.en.Mob> reserved)
    {
        if(Disabled("EXACTBIRTH") || item.Dead)return null;
        var map=CurrentClientBirthMap();if(map==null || !map.Births.TryGetValue(item.BirthId,out var record))return null;
        var mob=record.Native;var active=Field("currentLevel") as dc.pr.Level;
        if(mob==null || mob.destroyed || mob.life<=0 || mob.__uid!=record.Uid || reserved.Contains(mob) ||
            mob._level?.HashlinkPointer!=active?.HashlinkPointer || mob._level?.map?.HashlinkPointer!=map.Map.HashlinkPointer ||
            (string)Invoke("BuildMobStateTypeSignature",mob)!=item.Type)return null;
        return mob;
    }
    private static void RememberVerifiedClientBirth(MobBirth item,dc.en.Mob mob)
    {
        var map=CurrentClientBirthMap();if(map==null)return;
        if(item.Dead){map.Births.Remove(item.BirthId);return;}
        if(map.Births.Count>=8192 && !map.Births.ContainsKey(item.BirthId))throw new InvalidOperationException("Client birth history capacity exceeded");
        map.Births[item.BirthId]=new NativeBirth {Native=mob,Birth=item,Uid=mob.__uid};
    }
}
