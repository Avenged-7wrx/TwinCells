using System;
using System.Collections.Generic;

// Pure rules for CoopDialogPresenceFix: which cinematics are "the local player is busy in a
// dialog/menu, but the level goes on", so the partner's avatar should keep being updated.
// No game types here; the same file is compiled into the managed test.
//
// Background. Community mod 0.5.1 runs its whole per-frame co-op tick (apply the partner's
// position, facing and animation; send own position; ...) only while
// ModEntry.IsHeroRuntimeReadyForCoop is true, and that ends with Game.curCine == null.
// Every game cinematic sets Game.curCine, and a cinematic does not pause the level. So for
// the whole length of an NPC dialog the partner's avatar stands where it was and jumps to
// the right place when the dialog ends.
public static class DialogPresenceRules
{
    // Handled by the already installed CoopSyncFixes cinematic presenter. Never touched here,
    // so two writers can never drive one avatar.
    public static readonly string[] PresenterOwned =
    {
        "dc.cine.HealFountain", "dc.cine.MeetCollector", "dc.cine.MeetCollectorIntern", "dc.cine.MeetScribe",
        "dc.cine.MeetBlacksmith", "dc.cine.MeetSmallBS", "dc.cine.TailorDaughterCine",
    };

    // Cinematics during which the player stays in the level and only talks, reads or chooses.
    // "dc.GameCinematic" is the base class that inline NPC dialogs construct directly (Tailor,
    // Skinner, Gardener, knights, Maria, lore notes, signs ...). The base class is NOT dialog-only:
    // the game also builds it for levers, doors and props, trap-challenge endings, the pirate
    // spawn, the daily-run door walk-in and for boss death/phase scenes. The boss ones are kept
    // out by the boss-level test below, not by the type name. The named subclasses for level
    // changes, deaths, boss intros/outros and teleports are deliberately absent: the mod has its
    // own handling there.
    public static readonly string[] Admitted =
    {
        "dc.GameCinematic",
        "dc.cine.MajorItemFound", "dc.cine.TierItemFound", "dc.cine.TierItemFoundScoring", "dc.cine.TwitchUpItemFound",
        "dc.cine.TreasureOpening", "dc.cine.CursedTreasureOpening", "dc.cine.BRChestOpening",
        "dc.cine.LeaveCollector", "dc.cine.dlcp.MeetCollectorShanoa",
        "dc.cine.br.BrArchitectFirstMet", "dc.cine.br.BrNpcFirstMet", "dc.cine.br.BrNpcFirstRun",
        "dc.cine.BankAtm", "dc.cine.FirstCastleScreamerMeeting",
    };

    public enum Verdict { Present = 0, PresenterOwned = 1, NotAdmitted = 2, BossLevel = 3, Unknown = 4 }

    static readonly HashSet<string> Owned = new HashSet<string>(PresenterOwned, StringComparer.Ordinal);
    static readonly HashSet<string> Allowed = new HashSet<string>(Admitted, StringComparer.Ordinal);

    // cineType: runtime type name of Game.curCine. levelId: id of the hero's level.
    // isBossLevel: the mod's own boss-level list (it mirrors boss cinematics itself there).
    public static Verdict Classify(string cineType, string levelId, Func<string, bool> isBossLevel)
    {
        if (string.IsNullOrEmpty(cineType) || string.IsNullOrWhiteSpace(levelId) || isBossLevel == null) return Verdict.Unknown;
        if (Owned.Contains(cineType)) return Verdict.PresenterOwned;
        if (!Allowed.Contains(cineType)) return Verdict.NotAdmitted;
        bool boss;
        try { boss = isBossLevel(levelId); } catch { return Verdict.Unknown; }
        return boss ? Verdict.BossLevel : Verdict.Present;
    }

    // A cached verdict holds for exactly one cinematic object on one level. Game.curCine belongs to
    // the game, not to the level, so a cinematic that survives a level switch is judged again (the
    // boss-level answer may differ). Unknown is never kept: it is judged again on the next frame.
    public static bool NeedsJudgement(IntPtr cine, int uid, IntPtr level, IntPtr cachedCine, int cachedUid, IntPtr cachedLevel, Verdict cached)
        => cine != cachedCine || uid != cachedUid || level != cachedLevel || cached == Verdict.Unknown;

    // After this many failures in a row the module switches itself off for the session.
    public const int MaxConsecutiveFaults = 3;
    public static bool ShouldTrip(int consecutiveFaults) => consecutiveFaults >= MaxConsecutiveFaults;
}
