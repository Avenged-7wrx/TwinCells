using System;
using System.Globalization;
using System.Threading;
using ModCore.Mods;
using Serilog;

// Per-call overhead of hooked game functions in modded co-op play (see ChainCache.cs).
// Pinned to the exact framework files it was verified against; can be switched off before the game starts:
//   COOP_LOAD_CHAIN_DISABLE=1   keep the framework's own dispatch (the chain is rebuilt on every call)
//   COOP_LOAD_CHAIN_SHADOW=all  QA: compare the cached chain with a freshly built one on every call
//   COOP_LOAD_STATS=1           print counters every 5 s
public sealed class CoopLoadFix : ModBase
{
    public CoopLoadFix(ModInfo info):base(info){}
    static Timer StatsTimer;
    static long LastHits,LastBuilds,LastRefreshes,LastUncached,LastSamples;
    public override void Initialize()
    {
        try
        {
            ChainCache.Install();
            Log.Information("[CoopLoadFix] Hook chain cache ready; disabled={Disabled}",ChainCache.Disabled);
        }
        catch(Exception error){Log.Error("[CoopLoadFix] Hook chain cache failed to start, original behaviour kept: {Error}",ChainCache.InitializationError??error.Message);}
        if(Environment.GetEnvironmentVariable("COOP_LOAD_STATS")=="1")
            StatsTimer=new Timer(_=>Report(),null,5000,5000);
    }
    static string Delta(long now,ref long last)=>(now-Interlocked.Exchange(ref last,now)).ToString(CultureInfo.InvariantCulture);
    static void Report()
    {
        try
        {
            // The first five numbers are for the last 5 s; the three after them are totals since the start.
            Log.Information("[CoopLoadFix] chain hits={Hits} builds={Builds} refreshes={Refreshes} uncached={Uncached} samples={Samples} buildFaults={BuildFaults} mismatches={Mismatches} faults={Faults} off={Off} disabled={Disabled}",
                Delta(ChainCache.Hits,ref LastHits),Delta(ChainCache.Builds,ref LastBuilds),Delta(ChainCache.Refreshes,ref LastRefreshes),Delta(ChainCache.Uncached,ref LastUncached),Delta(ChainCache.Samples,ref LastSamples),
                ChainCache.BuildFaults,ChainCache.Mismatches,ChainCache.Faults,ChainCache.SwitchedOff,ChainCache.Disabled);
        }
        catch{}
    }
}
