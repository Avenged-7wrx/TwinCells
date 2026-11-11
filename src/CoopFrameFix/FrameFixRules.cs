using System;

// Pure decisions of CoopFrameFix. No game types here; the same file is compiled into the managed test.
public static class FrameFixRules
{
    // ---- position gate ----
    // After a changed line the next TrailingRepeats identical calls are still sent (as the mod always did), so a
    // single lost packet cannot leave the partner on a stale position; after that an unchanged position is
    // refreshed every refresh period.
    public const int TrailingRepeats=2;
    // Refresh period of an unchanged position. It is load-bearing: the partner's game checks the stored position
    // of the other player for actions with a hold time (reviving a downed partner takes about 0.7 s), so several
    // refreshes must fit into such a hold. Keep it at 0.1 s; never above 0.2 s.
    public const double PositionRefreshSeconds=0.1;
    public enum Position{Skip=0,First=1,Forced=2,Changed=3,Trailing=4,Refresh=5}

    // Bit-for-bit doubles, same facing, same level id (ordinal). NaN never equals itself.
    public static bool SamePosition(double x,double y,int dir,string level,double lastX,double lastY,int lastDir,string lastLevel)
        => x==lastX&&y==lastY&&dir==lastDir&&string.Equals(level,lastLevel,StringComparison.Ordinal);

    // has: a line was let through before on this connection. force: the mod asked for a forced send.
    public static Position DecidePosition(bool has,bool force,bool same,int trailing,long now,long lastTicks,long refreshTicks)
    {
        if(!has)return Position.First;
        if(force)return Position.Forced;
        if(!same)return Position.Changed;
        if(trailing>0)return Position.Trailing;
        if(now<lastTicks)return Position.Refresh;                                  // clock went backwards: send
        return now-lastTicks>=refreshTicks?Position.Refresh:Position.Skip;
    }
    // Trailing counter after a decision.
    public static int NextTrailing(Position decision,int trailing)
    {
        switch(decision)
        {
            case Position.First:case Position.Forced:case Position.Changed:return TrailingRepeats;
            case Position.Trailing:return trailing>0?trailing-1:0;
            default:return trailing;
        }
    }

    // ---- player colours ----
    // CoopSyncFixes.Colors.cs of the installed build (community-integration, FFF00CA3...):
    //   PlayerPaletteId(id, pairProfile) => id in 1..4 ? (pairProfile && id != 1 ? 2 : id) : 0
    // pairProfile is its TwoPlayerColors switch (COOP_TWO_PLAYER_COLORS=1, set by the player launcher): the host is
    // orange and every other player cyan. Palette 0 = no tint.
    public static int PaletteId(int id,bool pairProfile)=>id>=1&&id<=4?(pairProfile&&id!=1?2:id):0;
    // The multipliers of that file's BeforePlayerDraw, per palette.
    public static void TintMultipliers(int palette,out double r,out double g,out double b)
    {
        r=1;g=1;b=1;
        switch(palette)
        {
            case 1:r=1;g=.68;b=.42;break;
            case 2:r=.38;g=.88;b=1;break;
            case 3:r=.84;g=.48;b=1;break;
            case 4:r=.48;g=1;b=.48;break;
        }
    }

    // ---- render nesting ----
    // Only the outermost render of a frame tints (enter) and restores (leave).
    public static bool EnterRender(ref int depth)=>depth++==0;
    public static bool LeaveRender(ref int depth)
    {
        if(depth<=0){depth=0;return true;}       // unbalanced leave: restore anyway, never leave colours multiplied
        return --depth==0;
    }
}
