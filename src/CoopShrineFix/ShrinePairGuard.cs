using System;

// Scalar part of the source-to-wire proof. No proximity rebind of live actors.
public static class ShrinePairGuard
{
    public const int MaximumPending = 16;
    public const long LifetimeMilliseconds = 120000;
    // Pinned native MobsGen #31858 ops313-326 (EliteShrine branch):
    // source = room origin + marker origin + (width>>1, height-1).
    public static bool MarkerSource(int sourceX,int sourceY,int roomX,int roomY,int markerX,int markerY,int width,int height) =>
        width>0 && width<=1024 && height>0 && height<=1024 &&
        (long)roomX+markerX+(width>>1)==sourceX && (long)roomY+markerY+height-1==sourceY;
    public static bool BirthCell(int sourceX, int sourceY, int roomX, int roomY,
        int sourceOffsetX, int sourceOffsetY, double wireX, double wireY)
    {
        if (sourceX < 0 || sourceY < 0 || sourceX > 100000 || sourceY > 100000 ||
            (long)roomX + sourceOffsetX != sourceX || (long)roomY + sourceOffsetY != sourceY ||
            !double.IsFinite(wireX) || !double.IsFinite(wireY)) return false;
        // Native Mob.create begins in this cell; Y is the cell's bottom edge.
        // The first published X can already contain a subcell AI movement.
        return wireX >= sourceX * 24d && wireX < (sourceX + 1d) * 24d &&
            Math.Abs(wireY - (sourceY + 1d) * 24d) <= .01d;
    }
    public static bool Current(long created, long now, int generation, int build,
        int currentGeneration, int currentBuild) => now >= created &&
        now - created <= LifetimeMilliseconds && generation > 0 && build > 0 &&
        generation == currentGeneration && build == currentBuild;
    public static bool Packet(int id, int generation, string type, int expectedId, int expectedGeneration) =>
        id > 0 && id == expectedId && generation == expectedGeneration && type == "Archer|Archer";
}
