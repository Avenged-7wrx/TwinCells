using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using dc.en;
using HarmonyLib;

// Keep the original tracked candidate, but never convert a failed lookup's out
// value (Dictionary.TryGetValue writes zero) into a reciprocal network identity.
public static class MobRegistrationPatch
{
    public const string BaseSha256 = "378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28";
    public const string Owner = "CoopMobRegistrationFix.PositiveIdentity.20261108";
    const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly object Gate = new();
    static volatile bool IsReady;
    static long Suppressed;
    public static bool Ready => IsReady;
    public static bool Disabled { get; private set; }
    public static string InitializationError { get; private set; }
    public static long SuppressedInvalidCommits => Interlocked.Read(ref Suppressed);
    static MethodInfo Lookup;
    static FieldInfo Forward, Reverse;

    public static void Install()
    {
        lock (Gate)
        {
            if (IsReady) return;
            if (InitializationError != null) throw new InvalidOperationException("Prior registration initialization failed: " + InitializationError);
            Harmony harmony = null; MethodInfo target = null;
            try
            {
                var assembly = typeof(NetNode).Assembly;
                if (assembly.GetName().Version != new Version(0, 5, 1, 0) || global::CoopPackRuntime.PackPins.Sha256(assembly.Location) != BaseSha256)
                    throw new InvalidOperationException("CoopMobRegistrationFix requires the exact community 0.5.1 DLL");
                var type = assembly.GetType("DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization", true);
                target = type.GetMethod("AddTrackedMobLocked", S, null, new[] { typeof(Mob) }, null);
                Lookup = type.GetMethod("TryGetMobSyncId", S, null, new[] { typeof(Mob), typeof(int).MakeByRefType() }, null);
                Forward = type.GetField("IdToMob", S); Reverse = type.GetField("MobToId", S);
                if (target?.ReturnType != typeof(int) || Lookup?.ReturnType != typeof(bool) ||
                    Forward?.FieldType != typeof(Dictionary<int, Mob>) || Reverse?.FieldType != typeof(Dictionary<Mob, int>))
                    throw new InvalidOperationException("Pinned registration shape changed");
                Disabled = Environment.GetEnvironmentVariable("COOP_MOB_REGISTRATION_DISABLE") == "1";
                harmony = new Harmony(Owner);
                harmony.Patch(target, transpiler: new HarmonyMethod(typeof(MobRegistrationPatch).GetMethod(nameof(Transpile), S)));
                if (!Harmony.GetPatchInfo(target).Owners.Contains(Owner)) throw new InvalidOperationException("Registration hook missing");
                IsReady = true;
            }
            catch (Exception e)
            {
                IsReady = false; InitializationError = e.Message;
                if (harmony != null && target != null)
                    try { harmony.Unpatch(target, HarmonyPatchType.All, Owner); }
                    catch (Exception rollback) { InitializationError += "; rollback: " + rollback.Message; }
                throw;
            }
        }
    }

    static bool CommitAllowed(bool resolved, int id)
    {
        // Same-DLL disabled and pre-publication paths preserve the original >=0 gate.
        if (!IsReady || Disabled) return id >= 0;
        bool allowed = resolved && id > 0;
        if (!allowed && id >= 0) Interlocked.Increment(ref Suppressed);
        return allowed;
    }

    static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var code = instructions.ToList();
        var calls = Enumerable.Range(0, code.Count).Where(i => code[i].Calls(Lookup)).ToArray();
        var maps = Enumerable.Range(0, code.Count).Where(i => code[i].opcode == OpCodes.Ldsfld && Equals(code[i].operand, Forward) &&
            i >= 3 && code[i - 3].opcode == OpCodes.Ldloc_1 && code[i - 2].opcode == OpCodes.Ldc_I4_0 &&
            (code[i - 1].opcode == OpCodes.Blt_S || code[i - 1].opcode == OpCodes.Blt)).ToArray();
        if (calls.Length != 1 || maps.Length != 1) throw new InvalidOperationException("Expected one lookup and one final reciprocal-map guard");
        int call = calls[0], map = maps[0];
        if (code[call - 2].opcode != OpCodes.Ldarg_0 || code[call - 1].opcode != OpCodes.Ldloca_S ||
            !(code[call - 1].operand is LocalBuilder id) || id.LocalIndex != 1 ||
            (code[call + 1].opcode != OpCodes.Brfalse_S && code[call + 1].opcode != OpCodes.Brfalse) || map <= call ||
            code[map + 1].opcode != OpCodes.Ldloc_1 || code[map + 2].opcode != OpCodes.Ldarg_0 ||
            code[map + 3].opcode != OpCodes.Callvirt || code[map + 4].opcode != OpCodes.Ldsfld || !Equals(code[map + 4].operand, Reverse) ||
            code[map + 5].opcode != OpCodes.Ldarg_0 || code[map + 6].opcode != OpCodes.Ldloc_1 || code[map + 7].opcode != OpCodes.Callvirt)
            throw new InvalidOperationException("Registration stack/commit anchor changed");
        if (code.Skip(map - 2).Take(2).Any(c => c.labels.Count != 0 || c.blocks.Count != 0) || code[map - 3].blocks.Count != 0)
            throw new InvalidOperationException("Registration commit control-flow boundary changed");
        var resolved = generator.DeclareLocal(typeof(bool));
        // Replace just the final >=0 commit test. The earlier admission/canonical
        // lookup, index insertion, alias handling, lock, and return stay original.
        var start = new CodeInstruction(OpCodes.Ldloc, resolved); start.labels.AddRange(code[map - 3].labels);
        var target = code[map - 1].operand;
        code.RemoveRange(map - 3, 3);
        code.InsertRange(map - 3, new[] { start, new CodeInstruction(OpCodes.Ldloc_1),
            new CodeInstruction(OpCodes.Call, typeof(MobRegistrationPatch).GetMethod(nameof(CommitAllowed), S)), new CodeInstruction(OpCodes.Brfalse, target) });
        // The original branch still sees precisely the original lookup result.
        code.InsertRange(call + 1, new[] { new CodeInstruction(OpCodes.Dup), new CodeInstruction(OpCodes.Stloc, resolved) });
        return code;
    }
}
