using System;
using System.Collections.Generic;

// Published branch-key API of CoopBranchVisibilityFix (U9), for CoopTransitionGate (U10) and the CSF cinematic presenter.
// Companions find this type by name ("CoopBranchVisibilityApi"): standalone in the CoopBranchVisibilityFix assembly, in the
// pack in CoopPack.dll (integration may use PackParts.FindType("CoopBranchVisibilityFix","CoopBranchVisibilityApi")). Every
// member is safe to call when the part is absent from the session state: it answers "inactive"/"not hidden". Game thread only.
// Token semantics: 0 = main map (same value as the base mod and every peer version); a value with
// CoopBranchVisibilityRules.StableTag = a stable key of a sub-level or secret level, equal on both peers for the same room.
public static class CoopBranchVisibilityApi
{
    public const int Version=1;
    // True while the part is installed, not switched off and its visibility gate has not tripped.
    public static bool Active=>CoopBranchVisibilityPatch.Active;
    // The local player's current branch: level id (LevelMap.id) and token (0 main, stable token otherwise).
    public static bool TryGetLocalBranch(out string levelId,out int token)
    {
        levelId=null;token=-1;
        if(!Active)return false;
        try
        {
            var level=dc.pr.Game.Class.ME?.hero?._level;
            if(level?.map==null)return false;
            levelId=level.map.id?.ToString();token=CoopBranchVisibilityPatch.TokenFor(level,out _);
            return !string.IsNullOrWhiteSpace(levelId)&&token>=0;
        }
        catch{levelId=null;token=-1;return false;}
    }
    public static bool IsStableToken(int token)=>CoopBranchVisibilityRules.IsStable(token);
    // The partner (net id) is hidden because he is in another room of this map.
    public static bool IsRemoteHidden(int remoteId)=>CoopBranchVisibilityPatch.IsHidden(remoteId);
    // CSF presenter hook: decide for a snapshot's ROOM marker; true = do not present this partner (the base disposal runs).
    public static bool PresentationHide(int remoteId,string roomLevelId,int roomToken,bool hasRoom)=>CoopBranchVisibilityPatch.PresentationHide(remoteId,roomLevelId,roomToken,hasRoom);
    public static Dictionary<string,object> Describe()=>CoopBranchVisibilityPatch.Snapshot();
}
