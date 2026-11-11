using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModCore.Mods;
using dc.en;
using DeadCellsMultiplayerMod;
using Serilog;
using System.IO;
using System.Security.Cryptography;

// A separate, version-pinned repair package. Original DLL preserved; both peers need the extension.
public sealed partial class CoopSyncFixes : ModBase
{
    internal static readonly Type Mobs = typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization", true);
    internal static readonly Type Lobby = typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.LobbySession", true);
    internal static readonly Type Coordinator = typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.RunLaunchCoordinator", true);
    internal static readonly BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Dictionary<int, NetNode.MobDie> PendingDeaths = new();
    private static readonly HashSet<int> SeenStates = new();
    public CoopSyncFixes(ModInfo info) : base(info) { }
    public static bool RepairInitializationComplete {get;private set;}

    internal static object Field(string name) => Mobs.GetField(name, Flags).GetValue(null);
    internal static object Invoke(string name, params object[] arguments) => Mobs.GetMethod(name, Flags).Invoke(null, arguments);
    internal static object Gate => Field("Sync");
    internal static bool Current(int generation) => (bool)Invoke("IsPacketGenerationCurrentLocked", generation);
    private static bool Disabled(string name) => Environment.GetEnvironmentVariable("COOP_SYNC_DISABLE_" + name) == "1";
    private static void Patch(Harmony harmony, Type type, string name, string prefix = null, string postfix = null)
    {
        var method = type.GetMethod(name, Flags) ?? throw new MissingMethodException(type.FullName, name);
        harmony.Patch(method,
            prefix == null ? null : new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(prefix, Flags)),
            postfix == null ? null : new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(postfix, Flags)));
    }
    public override void Initialize()
    {
        RepairInitializationComplete=false;
        var original = typeof(ModEntry).Assembly;
        var hash = global::CoopPackRuntime.PackPins.Sha256(original.Location);
        if (original.GetName().Version != new Version(0, 5, 1, 0) ||
            hash != "378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28")
            throw new InvalidOperationException("CoopSyncFixes requires the pinned 0.5.1 multiplayer assembly.");
        var harmony = new Harmony("local.deadcells.syncfixes.1");
        if (!Disabled("IDENTITY")) Patch(harmony, Mobs, "AreLikelySameNativeMobProxy", nameof(NativeIdentity));
        if (!Disabled("HEALTH")) Patch(harmony, Mobs, "ApplyIncomingHostMobStates", null, nameof(MergeReliableHealth));
        if (!Disabled("DEATH"))
        {
            Patch(harmony, Mobs, "ApplyIncomingMobDies", nameof(RetainUnresolvedDeaths));
            Patch(harmony, Mobs, "FlushPendingClientAuthoritativeDeaths", nameof(RetryUnresolvedDeaths));
            Patch(harmony, Mobs, "ResetMobTrackingLocked", nameof(ResetDeaths));
        }
        if (!Disabled("READY")) Patch(harmony, Lobby, "ReceiveRunLevelReadyPayload", nameof(ValidateReadyIdentity));
        InitializeEnemyExtensions(harmony);
        InitializeRegistryBatches(harmony);
        InitializeCatchup(harmony);
        InitializeLifecycleFence(harmony);
        InitializeSleepingTransforms(harmony);
        InitializeSleepingMotion(harmony);
        InitializeJoinProtocol(harmony);
        InitializeWorldJoin(harmony);
        InitializeRoutes();
        InitializeGraphLinks(harmony);
        InitializeBranches(harmony);
        InitializeManifests(harmony);
        InitializeWireEpoch(harmony);
        InitializePlayerColors();
        InitializePlayerSafety(harmony);
        InitializeExitMembership(harmony);
        InitializeDoorAndDeathBoundaries(harmony);
        InitializeEliteDeathCompletion(harmony);
        InitializeRemoteWeaponIdentity(harmony);
        InitializeRemoteWeaponEquip(harmony);
        InitializeCinematicPresentation(harmony);
        InitializeExperimentalElevators(harmony);
        Log.Information("[CoopSyncFixes] v1.3.0 integration candidate loaded. identity={Identity} health={Health} death={Death} ready={Ready}",
            !Disabled("IDENTITY"), !Disabled("HEALTH"), !Disabled("DEATH"), !Disabled("READY"));
        RepairInitializationComplete=true;
    }

    // Proximity is useful for matching a host spawn to an unbound client replica, but is never
    // evidence that two managed wrappers refer to the same native object on one machine.
    private static bool NativeIdentity(Mob left, Mob right, ref bool __result)
    {
        __result = false;
        if (left == null || right == null) return false;
        if (ReferenceEquals(left, right)) { __result = true; return false; }
        try
        {
            if (left.destroyed || right.destroyed ||
                !(bool)Invoke("DoesLevelMatchCurrentIdentityLocked", left._level) ||
                !(bool)Invoke("DoesLevelMatchCurrentIdentityLocked", right._level)) return false;
            var pointer = left.HashlinkPointer;
            __result = pointer != IntPtr.Zero && pointer == right.HashlinkPointer;
        }
        catch { __result = false; } // Preserve the original dying-wrapper failure boundary.
        return false;
    }

    // A newer unreliable movement frame must not erase an older (but ordered/reliable) HP update.
    // Preserve every movement/animation field and merge only the already accepted HP/max HP.
    private static void MergeReliableHealth(IReadOnlyList<NetNode.MobStateSnapshot> states)
    {
        if (states == null || ModEntry._net == null || ModEntry._net.IsHost) return;
        lock (Gate)
        {
            var forward = (IDictionary)Field("IdToMob");
            var reverse = (IDictionary)Field("MobToId");
            var targets = (IDictionary)Field("clientMobTargets");
            var reported = (IDictionary)Field("clientLastReportedMobLife");
            SeenStates.Clear();
            for (var i = states.Count - 1; i >= 0; --i)
            {
                var state = states[i];
                if (!Current(state.Generation) || !SeenStates.Add(state.Index) || !forward.Contains(state.Index)) continue;
                var mob = forward[state.Index];
                if (mob == null || !reverse.Contains(mob) || (int)reverse[mob] != state.Index ||
                    !targets.Contains(mob) || !reported.Contains(mob) || (int)reported[mob] != state.Life) continue;
                var old = targets[mob];
                var type = old.GetType();
                object Get(string field) => type.GetField(field).GetValue(old);
                if ((int)Get("Life") == state.Life && (int)Get("MaxLife") == state.MaxLife) continue;
                targets[mob] = Activator.CreateInstance(type, new object[] {
                    Get("X"), Get("Y"), Get("Dir"), state.Life, state.MaxLife,
                    Get("AnimPayload"), Get("StatePayload"), Get("Time"), Get("Dx"), Get("Dy"),
                    Get("ReceivedFrame"), Get("ForcePositionSnap"), Get("ForceVerticalPositionSnap") });
            }
            SeenStates.Clear();
        }
    }

    private static void RetainUnresolvedDeaths(IReadOnlyList<NetNode.MobDie> dies)
    {
        if (dies == null || ModEntry._net == null || !ModEntry._net.IsAlive || ModEntry._net.IsHost) return;
        lock (Gate)
        {
            var forward = (IDictionary)Field("IdToMob");
            var reverse = (IDictionary)Field("MobToId");
            foreach (var die in dies)
            {
                if (die.MobIndex <= 0 || !Current(die.Generation)) continue;
                if (ConfirmedDead(die.Generation, die.MobIndex)) { PendingDeaths.Remove(die.MobIndex); continue; }
                var mob = forward.Contains(die.MobIndex) ? forward[die.MobIndex] as Mob : null;
                if (mob != null && !mob.destroyed && reverse.Contains(mob) && (int)reverse[mob] == die.MobIndex) continue;
                // Keep an authoritative identity until registration or level reset. No spatial guessing.
                // The cap is fail-visible; avoid silently turning an overflow into a false "fixed" claim.
                if (PendingDeaths.Count >= 8192 && !PendingDeaths.ContainsKey(die.MobIndex))
                    throw new InvalidOperationException("CoopSyncFixes pending death capacity exceeded.");
                PendingDeaths[die.MobIndex] = die;
            }
        }
    }
    private static bool ConfirmedDead(int generation, int id)
    {
        var ledger = Field("s_clientMobLifecycle");
        return (bool)ledger.GetType().GetMethod("IsDead").Invoke(ledger, new object[] { generation, id });
    }
    private static void ResetDeaths() { lock (Gate) PendingDeaths.Clear(); }
    private static void RetryUnresolvedDeaths()
    {
        if (ModEntry._net == null || !ModEntry._net.IsAlive || ModEntry._net.IsHost ||
            (bool)Invoke("IsSyncQuiescedForTransition")) return;
        List<NetNode.MobDie> retry;
        lock (Gate)
        {
            if (PendingDeaths.Count == 0) return;
            retry = new List<NetNode.MobDie>();
            var forward = (IDictionary)Field("IdToMob");
            var reverse = (IDictionary)Field("MobToId");
            foreach (var pair in PendingDeaths.ToArray())
            {
                var die = pair.Value;
                if (!Current(die.Generation) || ConfirmedDead(die.Generation, pair.Key)) { PendingDeaths.Remove(pair.Key); continue; }
                var mob = forward.Contains(pair.Key) ? forward[pair.Key] as Mob : null;
                if (mob == null || mob.destroyed || !reverse.Contains(mob) || (int)reverse[mob] != pair.Key ||
                    !(bool)Invoke("DoesLevelMatchCurrentIdentityLocked", mob._level)) continue;
                retry.Add(die);
                PendingDeaths.Remove(pair.Key);
            }
        }
        // Use the mod's original authority/deferred-death/idempotency path, outside the registry lock.
        if (retry.Count > 0) Invoke("ApplyIncomingMobDies", retry);
    }

    private static object Property(object value, string name) => value.GetType().GetProperty(name).GetValue(value);
    private static bool ValidateReadyIdentity(string payload)
    {
        var codec = typeof(ModEntry).Assembly.GetType("DeadCellsMultiplayerMod.RunLaunchWireCodec", true);
        var args = new object[] { payload, null, null };
        if (!(bool)codec.GetMethod("TryDecodeReady", Flags).Invoke(null, args) || args[1] == null) return true;
        var ready = args[1];
        var role = Coordinator.GetProperty("CurrentRole", Flags).GetValue(null).ToString();
        var getter = role == "Host" ? "GetCurrentHostDescriptor" : role == "Client" ? "GetCurrentRemoteDescriptor" : null;
        var descriptor = getter == null ? null : Coordinator.GetMethod(getter, Flags).Invoke(null, null);
        var valid = descriptor != null && Equals(Property(ready, "SessionId"), Property(descriptor, "SessionId")) &&
            Equals(Property(ready, "RunId"), Property(descriptor, "RunId")) &&
            Equals(Property(ready, "Sequence"), Property(descriptor, "Sequence"));
        if (!valid) return false;
        if (Disabled("JOIN")) return true;
        // Readiness is a completion acknowledgement, not evidence of late joining.
        Coordinator.GetMethod("ReceiveRemoteReady", Flags).Invoke(null, new object[] { ready });
        ArmExplicitJoinContext();
        return false;
    }
}
