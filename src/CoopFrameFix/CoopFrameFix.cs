using System;
using System.Globalization;
using System.Threading;
using ModCore.Mods;
using Serilog;

// Frame-pacing and traffic fixes for modded co-op play (see CallbackFinalization.cs, PlayerTint.cs, HotPaths.cs,
// PositionGate.cs). Each part is independent, pinned to the exact files it was verified against, and can be
// switched off before the game starts:
//   COOP_FRAME_CALLBACK_DISABLE=1   keep the framework's original callback finalization
//   COOP_FRAME_TINT_DISABLE=1       keep the owner's per-sprite colour hook
//   COOP_FRAME_BOSSPART_DISABLE=1   keep the mod's per-frame boss-part type test
//   COOP_FRAME_AFFECT_DISABLE=1     keep the mod's dynamic affect payload builder
//   COOP_FRAME_POSGATE_DISABLE=1    send the position line on every hero update as the mod does
//   COOP_FRAME_STATS=1              print counters every 5 s (and compare the affect payload with the original)
public sealed class CoopFrameFix : ModBase
{
    public CoopFrameFix(ModInfo info):base(info){}
    static Timer StatsTimer;
    static long LastSuppressed,LastReRegistered,LastTintFrames,LastTintSprites,LastPosPassed,LastPosSkipped;
    public override void Initialize()
    {
        try
        {
            CallbackFinalization.Install();
            Log.Information("[CoopFrameFix] Lazy callback finalization ready; disabled={Disabled}",CallbackFinalization.Disabled);
        }
        catch(Exception error){Log.Error("[CoopFrameFix] Lazy callback finalization failed to start, original behaviour kept: {Error}",error.Message);}
        try
        {
            PlayerTint.Install();
            Log.Information("[CoopFrameFix] Per-frame player colours ready; mode={Mode}",PlayerTint.Current);
        }
        catch(Exception error){Log.Error("[CoopFrameFix] Per-frame player colours failed to start, original behaviour kept: {Error}",error.Message);}
        try
        {
            HotPaths.Install();
            Log.Information("[CoopFrameFix] Mob hot paths ready; bossPartDisabled={BossPart} affectDisabled={Affect}",HotPaths.BossPartDisabled,HotPaths.AffectDisabled);
        }
        catch(Exception error){Log.Error("[CoopFrameFix] Mob hot paths failed to start (a part that is not listed as failed is active): {Error}",error.Message);}
        try
        {
            PositionGate.Install();
            Log.Information("[CoopFrameFix] Position send gate ready; disabled={Disabled}",PositionGate.Disabled);
        }
        catch(Exception error){Log.Error("[CoopFrameFix] Position send gate failed to start, original behaviour kept: {Error}",error.Message);}
        if(Environment.GetEnvironmentVariable("COOP_FRAME_STATS")=="1")
            StatsTimer=new Timer(_=>Report(),null,5000,5000);
    }
    static string Delta(long now,ref long last)=>(now-Interlocked.Exchange(ref last,now)).ToString(CultureInfo.InvariantCulture);
    static void Report()
    {
        try
        {
            Log.Information("[CoopFrameFix] hot {Line}",HotPaths.StatsLine);
            Log.Information("[CoopFrameFix] pos passed={Passed} skipped={Skipped} notConnected={Idle} posErrors={Errors} posDisabled={Disabled}",
                Delta(PositionGate.Passed,ref LastPosPassed),Delta(PositionGate.Skipped,ref LastPosSkipped),PositionGate.NotConnected,PositionGate.Faults,PositionGate.Disabled);
            Log.Information("[CoopFrameFix] stats suppressed={Suppressed} reRegistered={ReRegistered} callbackFaults={CallbackFaults} callbackDisabled={CallbackDisabled} tintMode={TintMode} tintFrames={TintFrames} tintSprites={TintSprites} tintErrors={TintErrors} twoPlayerProfile={Pair}",
                Delta(CallbackFinalization.Suppressed,ref LastSuppressed),Delta(CallbackFinalization.ReRegistered,ref LastReRegistered),
                CallbackFinalization.Faults,CallbackFinalization.Disabled,PlayerTint.Current,
                Delta(PlayerTint.TintedFrames,ref LastTintFrames),Delta(PlayerTint.TintedSpriteCount,ref LastTintSprites),
                PlayerTint.Errors,PlayerTint.TwoPlayerProfile);
        }
        catch{}
    }
}
