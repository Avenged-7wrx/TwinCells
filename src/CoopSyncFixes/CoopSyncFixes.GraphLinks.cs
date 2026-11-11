using System;
using System.Collections.Generic;
using dc.level;
using HarmonyLib;
using Hashlink.Virtuals;
using Newtonsoft.Json.Linq;

public sealed partial class CoopSyncFixes
{
    private static void InitializeGraphLinks(Harmony harmony)
    {
        if(!Disabled("ZLINKS"))Patch(harmony,WorldData,"ApplyLevelGraph",null,nameof(RestoreGraphLinkIds));
    }

    // addZChild allocates a fresh ID from the end-of-graph counter. The original
    // graph importer restores doorId but loses the serialized ID in both directions.
    private static void RestoreGraphLinkIds(LevelStruct target,object sync,bool __result)
    {
        if(!__result)return;
        var source=JObject.FromObject(sync);
        var expected=new Dictionary<string,Dictionary<string,int>>(StringComparer.Ordinal);
        var assignments=new List<(virtual_contentClue_dest_doorId_id_ Link,int Id)>();
        int expectedCount=0;
        foreach(var node in (JArray)source["Nodes"])
        {
            var links=new Dictionary<string,int>(StringComparer.Ordinal);
            foreach(var link in node["ZLinks"] as JArray ?? new JArray())
            {
                var destination=(string)link["DestUid"];
                if(!links.TryAdd(destination,(int)link["Id"]))
                    throw new InvalidOperationException("Ambiguous serialized Z-link destination");
            }
            expected.Add((string)node["Uid"],links);
            expectedCount+=links.Count;
        }
        for(int i=0;i<target.all.length;i++)
        {
            if(target.all.getDyn(i) is not RoomNode node || !expected.TryGetValue(node.uid.ToString(),out var links))continue;
            var restored=new HashSet<string>(StringComparer.Ordinal);
            if(node.zLinks!=null)for(int j=0;j<node.zLinks.length;j++)
            {
                if(node.zLinks.getDyn(j) is not virtual_contentClue_dest_doorId_id_ link || link.dest==null)continue;
                var destination=link.dest.uid.ToString();
                if(!links.TryGetValue(destination,out var id) || !restored.Add(destination))
                    throw new InvalidOperationException("Native Z-link does not match serialized graph");
                assignments.Add((link,id));
            }
            if(restored.Count!=links.Count)throw new InvalidOperationException("Missing native Z-link after graph import");
        }
        if(assignments.Count!=expectedCount)throw new InvalidOperationException("Missing source Z-link node after graph import");
        foreach(var assignment in assignments)assignment.Link.id=assignment.Id;
    }
}
