using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using DeadCellsMultiplayerMod;
using Serilog;

public sealed partial class CoopSyncFixes
{
    private const string Capability = "+csf1.3a";
    private const string ContextTag = "CSFJOIN1";
    private static readonly ConditionalWeakTable<object, JoinContext> HostConnections = new();
    private static readonly ConditionalWeakTable<NetNode, JoinContext> ClientConnections = new();
    private static readonly ConditionalWeakTable<NetNode, object> RejectedConnections = new();
    private sealed class JoinContext
    {
        public string Nonce;
        public bool LateJoin;
        public Guid SessionId;
        public long RunId;
        public int Sequence;
        public bool Armed;
        public bool Matches(object descriptor) => SameRun(SessionId, RunId, Sequence, descriptor);
        // The run test of Matches for any three values (gc-opt call sites): the descriptor's three record properties are read
        // through typed getters (CsfRun), in the same order and with the same short-circuit, so nothing is boxed.
        internal static bool SameRun(Guid sessionId, long runId, int sequence, object descriptor) => descriptor != null &&
            sessionId == CsfRun.SessionId(descriptor) && runId == CsfRun.RunId(descriptor) && sequence == CsfRun.Sequence(descriptor);
        // ActivationContext(connection, request).Matches(descriptor) without the temporary context: connection is read first,
        // as the object initializer read connection.Nonce first (a missing connection throws there, as before).
        internal static bool RequestMatches(JoinContext connection, ActivationRequest request, object descriptor)
        {
            _ = connection.Nonce;
            return SameRun(request.SessionId, request.RunId, request.Sequence, descriptor);
        }
    }
    private static void InitializeJoinProtocol(Harmony harmony)
    {
        Patch(harmony, typeof(NetNode), "BuildHelloLine", null, nameof(AdvertiseCapability));
        Patch(harmony, typeof(NetNode), "BuildWelcomeLine", null, nameof(AdvertiseCapability));
        Patch(harmony, typeof(NetNode), "TryValidateHandshakeLine", nameof(RequireCapability));
        var instance = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (var method in new[] { "SendLineToClientSafe", "SendLineToSteamClientSafe" })
            harmony.Patch(typeof(NetNode).GetMethod(method, instance) ?? throw new MissingMethodException(method),
                prefix: new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(StampConnectionId), Flags)));
        harmony.Patch(typeof(NetNode).GetMethod("HandleLine", instance),
            prefix: new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(ReadConnectionId), Flags)));
        if (!Disabled("JOIN")) Patch(harmony, Lobby, "ReceiveRunLaunchCommitPayload", null, nameof(ArmExplicitJoinContext));
    }
    private static void AdvertiseCapability(ref string __result)
        => __result = __result.TrimEnd('\r', '\n') + Capability + "\n";
    private static bool RequireCapability(ref string line, out int remoteProtocol, out string remoteBuild,
        out string failure, ref bool __result)
    {
        remoteProtocol = 0; remoteBuild = "unknown"; failure = string.Empty;
        var parts = (line ?? string.Empty).TrimEnd('\r', '\n').Split('|');
        if (parts.Length != 3 || !parts[2].EndsWith(Capability, StringComparison.Ordinal))
        {
            __result = false; failure = "Both players require CoopSyncFixes 1.3.0 integration candidate";
            if (parts.Length >= 3) remoteBuild = parts[2];
            return false;
        }
        parts[2] = parts[2].Substring(0, parts[2].Length - Capability.Length);
        line = string.Join("|", parts);
        return true; // Keep the original protocol/build validator after stripping the capability.
    }
    private static JoinContext CaptureConnectionContext(object connection)
    {
        var coordinatorGate = Coordinator.GetField("Sync", Flags).GetValue(null);
        lock (coordinatorGate)
        {
            var descriptor = Coordinator.GetMethod("GetCurrentHostDescriptor", Flags).Invoke(null, null);
            var phase = Coordinator.GetProperty("CurrentPhase", Flags)?.GetValue(null)?.ToString();
            // The managed state machine is read under its own gate; no game objects on send threads.
            if (phase == null)
            {
                var state = Coordinator.GetField("_state", Flags).GetValue(null);
                phase = state.GetType().GetProperty("Phase").GetValue(state).ToString();
            }
            var result = new JoinContext { Nonce = Guid.NewGuid().ToString("N"), LateJoin = descriptor != null && phase == "Playing" };
            if (descriptor != null)
            {
                result.SessionId = (Guid)Property(descriptor, "SessionId");
                result.RunId = (long)Property(descriptor, "RunId");
                result.Sequence = (int)Property(descriptor, "Sequence");
            }
            return result;
        }
    }
    private static void StampConnectionId(NetNode __instance, object client, ref string line)
    {
        if (!__instance.IsHost || line == null || !line.StartsWith("ID|", StringComparison.Ordinal)) return;
        var id = line.TrimEnd('\r', '\n').Substring(3);
        if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var assigned) || assigned <= 0)
            throw new InvalidOperationException("Malformed local connection identity");
        var context = HostConnections.GetValue(client, CaptureConnectionContext);
        var json = new JObject { ["v"] = 1, ["nonce"] = context.Nonce, ["late"] = context.LateJoin,
            ["session"] = context.SessionId.ToString(), ["run"] = context.RunId, ["seq"] = context.Sequence };
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json.ToString(Formatting.None)));
        line = "ID|" + assigned.ToString(CultureInfo.InvariantCulture) + "|" + ContextTag + "|" + payload + "\n";
    }
    private static bool ReadConnectionId(NetNode __instance, ref string line, ref bool __result, ref string forwardLine)
    {
        if (__instance.IsHost || line == null) return true;
        if (RejectedConnections.TryGetValue(__instance, out _)) { __result = false; forwardLine = null; return false; }
        bool Reject(string reason)
        {
            RejectedConnections.GetValue(__instance, _ => new object());
            Log.Warning("[CoopSyncFixes] Rejecting connection: {Reason}", reason);
            return false;
        }
        if (!line.StartsWith("ID|", StringComparison.Ordinal))
        {
            if ((line.StartsWith("RUNCOMMIT|", StringComparison.OrdinalIgnoreCase) || line.StartsWith("RUNEXEC|", StringComparison.OrdinalIgnoreCase)) &&
                !ClientConnections.TryGetValue(__instance, out _))
            { __result = false; forwardLine = null; return Reject("Launch arrived before connection context"); }
            return true;
        }
        try
        {
        var parts = line.TrimEnd('\r', '\n').Split('|');
        if (parts.Length != 4 || parts[2] != ContextTag || parts[3].Length > 4096 ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var assigned) || assigned <= 0)
            throw new InvalidOperationException("Missing CoopSyncFixes connection context");
        var value = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(parts[3])));
        if ((int?)value["v"] != 1 || !Guid.TryParse((string)value["nonce"], out _) ||
            !Guid.TryParse((string)value["session"], out var session))
            throw new InvalidOperationException("Invalid CoopSyncFixes connection context");
        var context = new JoinContext { Nonce = (string)value["nonce"], LateJoin = (bool)value["late"],
            SessionId = session, RunId = (long)value["run"], Sequence = (int)value["seq"] };
        if (context.LateJoin && (session == Guid.Empty || context.RunId <= 0 || context.Sequence <= 0))
            throw new InvalidOperationException("Late join context lacks a committed run");
        if (ClientConnections.TryGetValue(__instance, out var previous))
        {
            if (previous.Nonce != context.Nonce || previous.LateJoin != context.LateJoin ||
                previous.SessionId != context.SessionId || previous.RunId != context.RunId || previous.Sequence != context.Sequence)
                throw new InvalidOperationException("Connection context changed during replay");
        }
        else ClientConnections.Add(__instance, context);
        line = "ID|" + parts[1];
        return true;
        }
        catch (Exception error)
        {
            __result = false; forwardLine = null;
            return Reject(error.Message);
        }
    }
    private static void ArmExplicitJoinContext()
    {
        var net = ModEntry._net;
        if (net == null || net.IsHost || !ClientConnections.TryGetValue(net, out var context)) return;
        var descriptor = Coordinator.GetMethod("GetCurrentRemoteDescriptor", Flags).Invoke(null, null);
        var lobbyGate = Lobby.GetField("Sync", Flags).GetValue(null);
        lock (lobbyGate)
        {
            var pending = Lobby.GetField("_midRunJoinSpawnPending", Flags);
            if (!context.LateJoin || !context.Matches(descriptor)) { pending.SetValue(null, false); return; }
            if (context.Armed) return;
            pending.SetValue(null, true);
            context.Armed = true;
            Log.Information("[CoopSyncFixes] Explicit late-join context armed for current run.");
        }
    }
}
