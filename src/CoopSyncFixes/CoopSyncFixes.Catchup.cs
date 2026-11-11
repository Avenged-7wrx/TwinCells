using System;
using System.Collections.Generic;
using dc.en;
using dc.pr;
using HarmonyLib;

public sealed partial class CoopSyncFixes
{
    private sealed class CatchupWindow {public List<Mob> Scratch,Original;}
    private static int CatchupNext,CatchupBuild=-1;
    private static IntPtr CatchupLevel;
    private static void InitializeCatchup(Harmony harmony)
    {
        if(Disabled("CATCHUP"))return;
        // Pinned release has four parameters, unlike the newer upstream source.
        harmony.Patch(Mobs.GetMethod("FlushHostPriorityResyncPass",Flags),
            prefix:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(SelectCatchupWindow),Flags)),
            finalizer:new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(RestoreCatchupWindow),Flags)));
    }
    private static void SelectCatchupWindow(bool includeAll,out CatchupWindow __state)
    {
        __state=null;if(!includeAll)return;
        lock(Gate)
        {
            var scratch=(List<Mob>)Field("s_batchMobsScratch");if(scratch.Count==0)return;
            var build=(int)Field("s_levelIdentityGeneration");var pointer=(Field("currentLevel") as Level)?.HashlinkPointer ?? IntPtr.Zero;
            if(build!=CatchupBuild || pointer!=CatchupLevel){CatchupNext=0;CatchupBuild=build;CatchupLevel=pointer;}
            var original=new List<Mob>(scratch);var start=CatchupNext%original.Count;var count=Math.Min(48,original.Count);
            __state=new CatchupWindow {Scratch=scratch,Original=original};
            scratch.Clear();for(int i=0;i<count;i++)scratch.Add(original[(start+i)%original.Count]);
            CatchupNext=(start+count)%original.Count;
            // A peer can finish a cached-map return after the initial MOBREG burst.
            // Re-advertise identity at the native catch-up cadence, not every frame.
            var remaining=Mobs.GetField("s_hostMobRegistryResendsRemaining",Flags);
            remaining.SetValue(null,Math.Max(1,(int)remaining.GetValue(null)));
            // At most 48 candidates also prevents the native builder caching a 49th
            // snapshot as sent before its late budget check discards that snapshot.
        }
    }
    private static Exception RestoreCatchupWindow(Exception __exception,CatchupWindow __state)
    {
        if(__state!=null)lock(Gate){__state.Scratch.Clear();__state.Scratch.AddRange(__state.Original);}
        return __exception;
    }
}
