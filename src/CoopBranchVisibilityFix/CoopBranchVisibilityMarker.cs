using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using dc.pr;
using DeadCellsMultiplayerMod;
using HaxeProxy.Runtime;
using R=CoopBranchVisibilityRules;

// Minimap marker of a hidden partner (PLAN-ALPHA2.md issue 2 step 5; OWNER-DECISIONS OD3 "keep only his minimap marker", Q4).
// While the partner is in another room the base disposes his avatar, and with it the MiniMap tracker that
// GhostHero.CreateGhostKing gave it (track(king, 14888237, "minimapHero", null, true, ...); MiniMap.postUpdate f@3327 drops
// trackers of destroyed entities). When the branches start to differ (before the base moves the avatar to the partner's
// new coordinates), the tracker's dot and icons are copied: tile, colour, scale and their sprite batches, and the cell the
// avatar stood on, i.e. the door the partner used. While he stays hidden, plain h2d.BatchElements with those tiles sit in
// the same batches at that cell (dot on the small map, icons on the fullscreen map, as postUpdate places a tracker), with a
// steady alpha. They are removed when he is shown again, and dropped with the minimap on a level change (never touched
// after their minimap is gone). Native calls: BatchElement constructor (type 308), SpriteBatch.add (f@1412 / HSpriteBatch
// f@2226, which accepts any BatchElement and only notifies HSpriteBE), BatchElement.remove (f@1162). Kill switch:
// COOP_BRANCH_VISIBILITY_MARKER_DISABLE; own fault gate.
public static class CoopBranchVisibilityMarker
{
    internal static readonly R.Gate Gate=new();
    sealed class Visual{public dc.h2d.Tile Tile;public dc.h2d.SpriteBatch Batch;public double R,G,B,ScaleX,ScaleY;}
    sealed class Captured{public int RemoteId,Cx,Cy;public IntPtr Level,MiniMap,Ghost;public Visual Dot;public readonly List<Visual> Icons=new();public long At;}
    sealed class Live{public Captured Source;public IntPtr MiniMap;public dc.h2d.BatchElement Dot;public readonly List<dc.h2d.BatchElement> Icons=new();public bool Fullscreen;public double BaseScale;}
    static readonly Dictionary<int,Captured> Captures=new();
    static readonly Dictionary<int,Live> Markers=new();
    static long Created,Removed,Dropped,NoTracker,CaptureCount;
    static readonly R.LogBudget Budget=new(120,Stopwatch.Frequency);
    public static long CreatedCount=>Created;
    public static long RemovedCount=>Removed;

    public static void Bind()
    {
        // Proxy members are pinned symbolically in CoopBranchVisibilityPins.Symbolic; the minimap field is the base mod's.
        if(typeof(ModEntry).GetField("miniMap",System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Static)?.FieldType!=typeof(dc.ui.hud.MiniMap))
            throw new InvalidOperationException("Pinned ModEntry.miniMap changed");
    }
    static bool On=>CoopBranchVisibilityPatch.Active&&!CoopBranchVisibilityPatch.MarkerDisabled&&!Gate.Tripped;
    static void Info(string key,string template,params object[] values)
    {
        if(!Budget.Allow(key,Stopwatch.GetTimestamp()))return;
        try{Serilog.Log.Information("[CoopBranchVisibilityFix] "+template,values);}catch{}
    }
    static void Fault(string what,Exception error)
    {
        bool before=Gate.Tripped,tripped=Gate.Fail();
        if(Gate.Faults==1)try{Serilog.Log.Warning("[CoopBranchVisibilityFix] Minimap marker {What} failed: {Error}",what,error.GetBaseException().Message);}catch{}
        if(tripped&&!before)try{Serilog.Log.Warning("[CoopBranchVisibilityFix] Minimap marker switched off for this session after {Faults} faults",Gate.Faults);}catch{}
    }
    static Visual Copy(dc.h2d.BatchElement e)
    {
        if(e==null||e.HashlinkPointer==IntPtr.Zero)return null;
        var tile=e.t;var batch=e.batch;
        if(tile==null||batch==null||batch.HashlinkPointer==IntPtr.Zero)return null;
        return new Visual{Tile=tile,Batch=batch,R=e.r,G=e.g,B=e.b,ScaleX=e.scaleX,ScaleY=e.scaleY};
    }

    // Called by the visibility decision on the frame the branches start to differ.
    public static void Capture(int remoteId)
    {
        if(!On)return;
        try
        {
            if(!CoopBranchVisibilityPatch.SlotOf(remoteId,out int slot))return;
            var ghost=ModEntry.clients[slot];
            if(ghost==null||ghost.HashlinkPointer==IntPtr.Zero||ghost.destroyed)return;
            var mini=ModEntry.miniMap;
            var cap=new Captured{RemoteId=remoteId,Cx=ghost.cx,Cy=ghost.cy,Level=ghost._level?.HashlinkPointer??IntPtr.Zero,Ghost=ghost.HashlinkPointer,
                MiniMap=mini!=null&&!mini.destroyed?mini.HashlinkPointer:IntPtr.Zero,At=Stopwatch.GetTimestamp()};
            if(cap.MiniMap!=IntPtr.Zero)
            {
                var trackers=mini.trackers;
                if(trackers!=null)for(int i=0;i<trackers.length;i++)
                {
                    if(!(trackers.getDyn(i) is dc.ui.hud.MMTracker tracker)||tracker.e==null||tracker.e.HashlinkPointer!=ghost.HashlinkPointer)continue;
                    cap.Dot=Copy(tracker.dot);
                    var icons=tracker.icons;
                    if(icons!=null)for(int j=0;j<icons.length;j++){var v=Copy(icons.getDyn(j) as dc.h2d.BatchElement);if(v!=null)cap.Icons.Add(v);}
                    break;
                }
            }
            if(cap.Dot==null){NoTracker++;Info("notracker:"+remoteId,"No minimap tracker to copy for partner {Id}: no marker this time",remoteId);}
            Release(remoteId,"new capture");
            Captures[remoteId]=cap;CaptureCount++;
            Gate.Ok();
        }
        catch(Exception error){Fault("capture",error);}
    }

    static dc.h2d.BatchElement Add(Visual v,int x,int y,double sx,double sy,bool visible)
    {
        var e=new dc.h2d.BatchElement(v.Tile);
        e.x=x;e.y=y;e.scaleX=sx;e.scaleY=sy;e.r=v.R;e.g=v.G;e.b=v.B;e.a=R.MarkerAlpha;e.visible=visible;
        v.Batch.add(e,Ref<bool>.Null);
        return e;
    }
    // Frame tick (ModEntry.OnFrameUpdate postfix): create, maintain and remove markers.
    public static void Tick()
    {
        if(Captures.Count==0&&Markers.Count==0)return;
        if(!On){Forget("off");return;}
        try
        {
            var level=Game.Class.ME?.hero?._level;var mini=ModEntry.miniMap;
            IntPtr levelPtr=level?.HashlinkPointer??IntPtr.Zero;
            IntPtr miniPtr=mini!=null&&mini.HashlinkPointer!=IntPtr.Zero&&!mini.destroyed?mini.HashlinkPointer:IntPtr.Zero;
            foreach(var remote in Captures.Keys.ToList())
            {
                var cap=Captures[remote];
                if(!CoopBranchVisibilityPatch.IsPendingOrHidden(remote)){Release(remote,"shown");continue;}
                bool want=R.MarkerWanted(CoopBranchVisibilityPatch.IsHidden(remote),cap.Dot!=null,levelPtr!=IntPtr.Zero&&cap.Level==levelPtr,miniPtr!=IntPtr.Zero&&cap.MiniMap==miniPtr);
                bool live=Markers.ContainsKey(remote);
                if(want&&!live)
                {
                    bool full=mini.isFullscreen;double scale=mini.baseScale;
                    var marker=new Live{Source=cap,MiniMap=miniPtr,Fullscreen=full,BaseScale=scale};
                    marker.Dot=Add(cap.Dot,cap.Cx,cap.Cy,cap.Dot.ScaleX,cap.Dot.ScaleY,R.DotVisible(full));
                    foreach(var icon in cap.Icons)marker.Icons.Add(Add(icon,cap.Cx,cap.Cy+1,R.IconScale(scale),R.IconScale(scale),R.IconVisible(full)));
                    Markers[remote]=marker;Created++;
                    Info("marker:"+remote+":"+cap.Cx+":"+cap.Cy,"Minimap marker for partner {Id} at cell {Cx},{Cy} (the room he left)",remote,cap.Cx,cap.Cy);
                }
                else if(!want&&live)Remove(remote,"not wanted");
            }
            foreach(var marker in Markers.Values)
            {
                if(marker.MiniMap!=miniPtr)continue;
                bool full=mini.isFullscreen;double scale=mini.baseScale;
                if(full==marker.Fullscreen&&scale.Equals(marker.BaseScale))continue;
                marker.Fullscreen=full;marker.BaseScale=scale;
                if(marker.Dot!=null)marker.Dot.visible=R.DotVisible(full);
                foreach(var e in marker.Icons){e.visible=R.IconVisible(full);e.scaleX=e.scaleY=R.IconScale(scale);}
            }
            Gate.Ok();
        }
        catch(Exception error){Fault("update",error);}
    }
    static void Remove(int remote,string why)
    {
        if(!Markers.TryGetValue(remote,out var marker))return;
        Markers.Remove(remote);
        var mini=ModEntry.miniMap;
        bool same=mini!=null&&mini.HashlinkPointer==marker.MiniMap&&!mini.destroyed;
        if(same)
        {
            try{marker.Dot?.remove();foreach(var e in marker.Icons)e.remove();Removed++;}
            catch(Exception error){Fault("remove",error);}
        }
        else Dropped++;                       // its minimap is gone with the level: nothing to touch
        Info("unmarker:"+remote+":"+why,"Minimap marker for partner {Id} removed ({Why})",remote,why);
    }
    public static void Release(int remoteId,string why)
    {
        Remove(remoteId,why);Captures.Remove(remoteId);
    }
    public static void Forget(string why)
    {
        foreach(var remote in Markers.Keys.ToList())Remove(remote,why);
        Captures.Clear();
    }
    public static Dictionary<string,object> Snapshot()
    {
        var rows=new List<object>();
        foreach(var cap in Captures.Values)
        {
            Markers.TryGetValue(cap.RemoteId,out var live);
            rows.Add(new Dictionary<string,object>{["remote"]=cap.RemoteId,["cx"]=cap.Cx,["cy"]=cap.Cy,["hasTracker"]=cap.Dot!=null,["icons"]=cap.Icons.Count,
                ["levelPointer"]=cap.Level.ToInt64().ToString("X"),["miniMapPointer"]=cap.MiniMap.ToInt64().ToString("X"),["live"]=live!=null,
                ["dotX"]=live?.Dot?.x,["dotY"]=live?.Dot?.y,["dotVisible"]=live?.Dot?.visible,["dotAlpha"]=live?.Dot?.a,
                ["dotInBatch"]=live?.Dot?.batch!=null&&live.Dot.batch.HashlinkPointer==cap.Dot?.Batch?.HashlinkPointer});
        }
        return new Dictionary<string,object>{["captures"]=rows,["live"]=Markers.Count,["created"]=Created,["removed"]=Removed,["dropped"]=Dropped,["noTracker"]=NoTracker,
            ["captureCount"]=CaptureCount,["tripped"]=Gate.Tripped,["faults"]=Gate.Faults};
    }
}
