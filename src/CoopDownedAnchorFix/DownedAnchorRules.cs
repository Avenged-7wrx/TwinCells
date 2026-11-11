using System;
using System.Collections.Generic;

// Pure decision rules for CoopDownedAnchorFix. No game, proxy or Harmony types here,
// so the same file is compiled into the managed test without the game.
//
// Background (community mod 0.5.1, FakeDeath.Anchors): when a player goes down, the mod
// moves the body to a position recorded up to 12 s earlier ("safe revive anchor") and then
// pins the corpse there. The recorded position is never re-checked against the level, so
// if it was on a lift that has since left, on a ladder, or otherwise has no floor under it
// now, the body hangs in the air. These rules decide whether a position has a floor under
// it in the CURRENT collision grid and which replacement to use when it does not.
public static class DownedAnchorRules
{
    public const double CellPx = 24.0;
    // Observed in native runs: static floor cell = 3 (bits 1|2); cell written under a
    // stationary SimpleElevator = 291 (bits 1|2|32|256); the same cell after the lift
    // left = 0. Any of the two low bits counts as something to stand on.
    public const int SolidMask = 3;
    public const double PreferredAgeSeconds = 1.25;   // same as the mod's HazardRecoveryPreferredAgeSeconds
    public const double MaxAgeSeconds = 12.0;         // same as SafeReviveAnchorMaxAgeSeconds
    public const double MinDistancePx = 144.0;        // same as HazardRecoveryMinDistancePx
    public const double FeetAboveGroundTolerance = 0.34; // cells; about 8 px
    public const int MaxDropCells = 40;               // 960 px, far more than any lift shaft step

    public enum Support { Unknown = 0, Supported = 1, Unsupported = 2 }

    // Reads one cell of the level grid. Returns false when the cell is outside the grid or
    // the grid cannot be read. groundYr is the height of the floor top inside the cell
    // ABOVE a solid cell (1 = floor top is the bottom edge of that cell).
    public delegate bool CellReader(int cx, int cy, out int flags, out double groundYr);

    public readonly struct Sample
    {
        public readonly double X, Y, AgeSeconds;
        public readonly bool SameLevel;
        public Sample(double x, double y, double ageSeconds, bool sameLevel) { X = x; Y = y; AgeSeconds = ageSeconds; SameLevel = sameLevel; }
    }

    // Feet pixel position -> cell. A standing entity has yr = 1 in the cell above the floor,
    // which is the same physical y as yr = 0 in the floor cell; normalise to the former.
    public static bool TryFeetCell(double x, double y, out int cx, out int cy, out double yr)
    {
        cx = 0; cy = 0; yr = 0;
        if (!double.IsFinite(x) || !double.IsFinite(y)) return false;
        double fx = x / CellPx, fy = y / CellPx;
        if (Math.Abs(fx) > 1e6 || Math.Abs(fy) > 1e6) return false;
        cx = (int)Math.Floor(fx);
        cy = (int)Math.Floor(fy);
        yr = fy - cy;
        if (yr < 1e-6) { cy -= 1; yr = 1.0; }
        return true;
    }

    public static Support Classify(double x, double y, int width, int height, CellReader read)
    {
        if (read == null || width <= 0 || height <= 0) return Support.Unknown;
        if (!TryFeetCell(x, y, out int cx, out int cy, out double yr)) return Support.Unknown;
        if (cx < 0 || cx >= width || cy < 0) return Support.Unknown;
        if (cy + 1 >= height) return Support.Unsupported;           // nothing below the last row
        if (!read(cx, cy, out int self, out double ground)) return Support.Unknown;
        if ((self & SolidMask) != 0) return Support.Supported;       // feet inside a solid cell: not floating
        if (!read(cx, cy + 1, out int below, out _)) return Support.Unknown;
        if ((below & SolidMask) == 0) return Support.Unsupported;
        double top = double.IsFinite(ground) && ground > 0.0 && ground <= 1.0 ? ground : 1.0;
        return yr >= top - FeetAboveGroundTolerance ? Support.Supported : Support.Unsupported;
    }

    // First floor straight below the position, as a feet position. False when none within
    // MaxDropCells or the grid cannot be read.
    public static bool TryDrop(double x, double y, int width, int height, CellReader read, out double dropX, out double dropY)
    {
        dropX = x; dropY = y;
        if (read == null || !TryFeetCell(x, y, out int cx, out int cy, out _)) return false;
        if (cx < 0 || cx >= width || cy < 0) return false;
        for (int row = cy, steps = 0; row + 1 < height && steps <= MaxDropCells; row++, steps++)
        {
            if (!read(cx, row, out int self, out double ground)) return false;
            if ((self & SolidMask) != 0) return false;               // started inside a wall: no clean drop
            if (!read(cx, row + 1, out int below, out _)) return false;
            if ((below & SolidMask) == 0) continue;
            double top = double.IsFinite(ground) && ground > 0.0 && ground <= 1.0 ? ground : 1.0;
            dropY = (row + top) * CellPx;
            return true;
        }
        return false;
    }

    // Same preference as the mod's TryGetHazardRecoveryAnchor, restricted to samples that
    // have a floor now: newest sample that is old enough and far enough from the death
    // point, otherwise the oldest supported sample. Returns -1 when there is none.
    public static int Choose(IReadOnlyList<Sample> newestFirst, double deathX, double deathY, Func<double, double, Support> classify)
    {
        if (newestFirst == null || classify == null) return -1;
        bool finiteDeath = double.IsFinite(deathX) && double.IsFinite(deathY);
        int oldestSupported = -1;
        for (int i = 0; i < newestFirst.Count; i++)
        {
            var s = newestFirst[i];
            if (!s.SameLevel || !double.IsFinite(s.AgeSeconds) || s.AgeSeconds < 0.0 || s.AgeSeconds > MaxAgeSeconds) continue;
            if (classify(s.X, s.Y) != Support.Supported) continue;
            oldestSupported = i;
            bool separated = true;
            if (finiteDeath)
            {
                double dx = s.X - deathX, dy = s.Y - deathY;
                separated = dx * dx + dy * dy >= MinDistancePx * MinDistancePx;
            }
            if (s.AgeSeconds >= PreferredAgeSeconds && separated) return i;
        }
        return oldestSupported;
    }

    public enum Action { Keep = 0, History = 1, DeathPosition = 2, Teammate = 3, TeammateDrop = 4, DeathDrop = 5 }

    public readonly struct Decision
    {
        public readonly Action Action; public readonly double X, Y; public readonly Support Original;
        public Decision(Action action, double x, double y, Support original) { Action = action; X = x; Y = y; Original = original; }
    }

    // Whole decision. The original anchor is kept unless the grid positively shows that
    // nothing is under it (Unknown keeps the original behaviour).
    public static Decision Decide(double anchorX, double anchorY, double deathX, double deathY, bool recentEnvironmentalDamage,
        IReadOnlyList<Sample> newestFirst, bool hasTeammate, double teammateX, double teammateY,
        int width, int height, CellReader read)
    {
        Func<double, double, Support> classify = (px, py) => Classify(px, py, width, height, read);
        var original = classify(anchorX, anchorY);
        if (original != Support.Unsupported) return new Decision(Action.Keep, anchorX, anchorY, original);

        int pick = Choose(newestFirst, deathX, deathY, classify);
        if (pick >= 0) return new Decision(Action.History, newestFirst[pick].X, newestFirst[pick].Y, original);

        // A hazard death must not leave the body on the hazard; everything else may stay
        // where the player actually stood.
        if (!recentEnvironmentalDamage && classify(deathX, deathY) == Support.Supported)
            return new Decision(Action.DeathPosition, deathX, deathY, original);

        if (hasTeammate)
        {
            if (classify(teammateX, teammateY) == Support.Supported)
                return new Decision(Action.Teammate, teammateX, teammateY, original);
            if (TryDrop(teammateX, teammateY, width, height, read, out double tx, out double ty))
                return new Decision(Action.TeammateDrop, tx, ty, original);
        }

        if (!recentEnvironmentalDamage && TryDrop(deathX, deathY, width, height, read, out double dx2, out double dy2))
            return new Decision(Action.DeathDrop, dx2, dy2, original);

        return new Decision(Action.Keep, anchorX, anchorY, original);
    }
}
