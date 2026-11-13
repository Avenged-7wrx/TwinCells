using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

// Pure rules of CoopBranchVisibilityFix (Alpha2 unit U9, PLAN-ALPHA2.md issue 2 and issue 6 R3; OWNER-DECISIONS OD3/Q4).
// No game types: compiled alone with CoopBranchVisibilityChecks.cs by Test-Managed.ps1.
//
// Branch key. A sub-level (Z-door branch) shares LevelMap.id with its parent map (C:1085, C:2381, C:2870), so the base mod's
// level-only visibility test (ShouldKeepRemoteKingVisibleInRoom, IL:38061-38093) draws a partner who is in a sub-level of the
// same map, at sub-level coordinates, inside the main map ("through the wall"). The per-player ROOM marker the base already
// sends (ZROOM|id|level|token, token = ComputeLevelBranchToken, IL:36748) is 0 on a main map and, on a sub-level, the
// index+1 of the level in Game.subLevels, which is not a stable cross-peer key (genSecretLevel order differs per peer).
// The part replaces that sub-level token by a stable hash of what both peers generate identically:
//   Z-door sub-level   id|seed|WxH|SUB:<sorted zLink ids of the isZChild rooms>   (CSF Routes.cs:99-105 DescribeRouteMap)
//   sub-level without z-children   id|seed|WxH|MAIN
//   secret level (Level.isSecret, built by Game.genSecretLevel f@652 with seed = gameSeed + getUniqId(), so the seed is
//   per process)   SECRET|id|WxH   (no seed)
// The token carries a tag in bits 24-30 (0x6A) so a peer can tell a stable key from the base's small index token: the
// capability is in-band, no new wire line is needed (the existing ROOM line carries it).
public static class CoopBranchVisibilityRules
{
    public const int StableTag=0x6A000000,StableMask=0x7F000000,HashMask=0x00FFFFFF;
    public const double HideAfterSeconds=0.4;          // PLAN-ALPHA2.md issue 2 step 2 (hysteresis: an avatar re-creation costs 29-38 ms)
    public const int MaxConsecutiveFaults=8,MaxTotalFaults=64;   // same thresholds as CoopPackRuntime.PartGate

    public enum Relation{Unknown,Same,Different}

    // ---- key and token ----
    public static string Key(string levelId,int seed,int wid,int hei,bool secretOnly,bool hasZChild,IEnumerable<int> zLinkIds)
    {
        if(string.IsNullOrWhiteSpace(levelId))return null;
        string id=levelId.Trim(),size=wid.ToString(CultureInfo.InvariantCulture)+"x"+hei.ToString(CultureInfo.InvariantCulture);
        if(secretOnly)return "SECRET|"+id+"|"+size;
        var ids=(zLinkIds??Enumerable.Empty<int>()).Distinct().OrderBy(v=>v).Select(v=>v.ToString(CultureInfo.InvariantCulture));
        return id+"|"+seed.ToString(CultureInfo.InvariantCulture)+"|"+size+"|"+(hasZChild?"SUB:"+string.Join(",",ids):"MAIN");
    }
    // A Z-door branch whose isZChild rooms have no zLinks yet is incompletely constructed (CSF Branches.cs:37-39): never cache it.
    public static bool KeyComplete(bool secretOnly,bool hasZChild,int zLinkCount)=>secretOnly||!hasZChild||zLinkCount>0;
    // FNV-1a 32 over the UTF-16 code units (the base's ComputeStablePositiveToken loop, IL:36880), then the tag.
    public static int Token(string key)
    {
        if(string.IsNullOrEmpty(key))return 0;
        uint hash=2166136261;
        unchecked{foreach(char c in key){hash^=c;hash*=16777619;}}
        return StableTag|(int)(hash&HashMask);
    }
    public static bool IsStable(int token)=>token>0&&(token&StableMask)==StableTag;

    // ---- relation of the partner's branch to mine ----
    // remote*: the partner's last ROOM marker (NetNode.RemoteSnapshot.HasRoom/RoomLevelId/RoomId, sticky until
    // NetNode.ClearRemoteRoomMarkers); localMarker*: what this peer last registered and sent (ModEntry._localLastDoorMarker*,
    // set by RegisterLocalDoorMarker from the same ComputeLevelBranchToken); localLevelId: the level the base compares.
    // Unknown means "no opinion": the base decides alone (it keeps the avatar).
    public static Relation Relate(bool remoteHasRoom,string remoteRoomLevel,int? remoteToken,string localMarkerLevel,int localToken,string localLevelId)
    {
        if(!remoteHasRoom||!remoteToken.HasValue||remoteToken.Value<0||string.IsNullOrWhiteSpace(remoteRoomLevel))return Relation.Unknown;
        if(string.IsNullOrWhiteSpace(localLevelId)||localToken<0||string.IsNullOrWhiteSpace(localMarkerLevel))return Relation.Unknown;
        string level=localLevelId.Trim();
        if(!string.Equals(remoteRoomLevel.Trim(),level,StringComparison.Ordinal)||!string.Equals(localMarkerLevel.Trim(),level,StringComparison.Ordinal))return Relation.Unknown;
        int r=remoteToken.Value,l=localToken;
        if(r==l)return Relation.Same;
        // 0 is the main map in every version (ComputeLevelBranchToken returns 0 when !isSubLevel), so main vs branch is
        // decidable even against a peer without this part.
        if(r==0||l==0)return Relation.Different;
        if(IsStable(r)&&IsStable(l))return Relation.Different;
        return Relation.Unknown;               // a base index token on one side: two sub-levels cannot be compared
    }

    // ---- hysteresis per remote player ----
    public sealed class Track
    {
        public Relation Last=Relation.Unknown;public long DifferentSince;public bool Hidden;public long Changes;
        public void Reset(){Last=Relation.Unknown;DifferentSince=0;Hidden=false;}
    }
    // Hide only after the branches have differed for HideAfterSeconds; show again at once when they match or become unknown.
    // started: this step began a mismatch (the moment to remember where the partner left the shared map).
    public static bool Step(Track t,Relation r,long now,long holdTicks,out bool changed,out bool started)
    {
        bool before=t.Hidden;started=false;
        if(r==Relation.Different)
        {
            if(t.DifferentSince==0){t.DifferentSince=now==0?1:now;started=true;}
            if(now-t.DifferentSince>=holdTicks)t.Hidden=true;
        }
        else{t.DifferentSince=0;t.Hidden=false;}
        t.Last=r;changed=before!=t.Hidden;if(changed)t.Changes++;
        return t.Hidden;
    }
    public static long HoldTicks(long frequency)=>(long)(frequency*HideAfterSeconds);

    // ---- what the hide applies to ----
    // ShouldKeepRemoteKingVisibleInRoom postfix: only ever turns true into false (the base's own false stays).
    public static bool KeepVisible(bool original,bool hidden)=>original&&!hidden;
    // Downed corpse (ApplyRemoteDownedGhostPositions) and revive target (FindNearestReviveTarget): the base's level test
    // AND "not hidden" (OWNER-DECISIONS Q4: a partner downed in another room is hidden and cannot be revived from there).
    public static bool CorpseInScope(bool levelEquals,bool hidden)=>levelEquals&&!hidden;
    // R3 (issue 6): Kinghead hides its flame batches while the ghost is invisible and native code never shows them again
    // (HeroHead.updateHeadFx sets visible only on customHeadSpr, eye, customBackSpr). Show them again once the ghost is visible.
    public static bool RestoreHeadBatches(bool ghostVisible,bool normalVisible,bool addVisible)=>ghostVisible&&(!normalVisible||!addVisible);

    // ---- minimap marker ----
    // The partner's avatar (and with it the MiniMap tracker GhostHero.CreateGhostKing gave it) is disposed while he is in
    // another branch; a marker copied from that tracker stays at the last cell he occupied on my map (the door he used).
    public static bool MarkerWanted(bool hidden,bool hasCapture,bool sameLevel,bool sameMinimap)=>hidden&&hasCapture&&sameLevel&&sameMinimap;
    // MiniMap.postUpdate (f@3327): the dot shows on the small map (visible = !isFullscreen, at cx,cy), the icons on the
    // fullscreen map (visible = isFullscreen, at cx,cy+1, scale 0.5/baseScale).
    public static bool DotVisible(bool fullscreen)=>!fullscreen;
    public static bool IconVisible(bool fullscreen)=>fullscreen;
    public static double IconScale(double baseScale)=>baseScale>0&&double.IsFinite(baseScale)?0.5/baseScale:0.5;
    public const double MarkerAlpha=0.7;            // steady (the live tracker blinks): a last known position

    // ---- fault gate (same thresholds as CoopPackRuntime.PartGate) ----
    public sealed class Gate
    {
        int consecutive;long total;public bool Tripped;public long Faults=>total;
        public void Ok(){consecutive=0;}
        public bool Fail(){total++;if(++consecutive>=MaxConsecutiveFaults||total>=MaxTotalFaults)Tripped=true;return Tripped;}
    }

    // ---- rate-limited log keys ----
    public sealed class LogBudget
    {
        readonly Dictionary<string,long> last=new(StringComparer.Ordinal);readonly int cap;readonly long cooldown;int used;
        public LogBudget(int cap,long cooldownTicks){this.cap=cap;cooldown=cooldownTicks;}
        public int Used=>used;
        public bool Allow(string key,long now)
        {
            if(used>=cap)return false;
            if(last.TryGetValue(key,out var at)&&now-at<cooldown)return false;
            last[key]=now;used++;return true;
        }
    }

    public static string Describe(int token)=>token==0?"main":IsStable(token)?"branch:"+(token&HashMask).ToString("X6",CultureInfo.InvariantCulture):token<0?"none":"index:"+token.ToString(CultureInfo.InvariantCulture);
}
