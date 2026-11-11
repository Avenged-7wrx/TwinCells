using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using dc.tool;
using DeadCellsMultiplayerMod;
using HarmonyLib;

public sealed partial class CoopSyncFixes
{
    internal static bool RemoteWeaponEquipReady;
    internal static long RemoteWeaponEquipAlreadyCorrect, RemoteWeaponEquipForwarded;
    private static readonly MethodInfo RemoteNativeEquip = typeof(Inventory).GetMethod("equip",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(InventItem) }, null)
        ?? throw new MissingMethodException(typeof(Inventory).FullName, "equip");

    private static void InitializeRemoteWeaponEquip(Harmony harmony)
    {
        if (RemoteWeaponEquipReady) return;
        var target = typeof(ModEntry).GetMethod("ApplyRemoteWeaponUpdate",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new[] { typeof(int), typeof(string), typeof(int), typeof(int), typeof(int?) }, null)
            ?? throw new MissingMethodException(typeof(ModEntry).FullName, "ApplyRemoteWeaponUpdate");
        harmony.Patch(target, transpiler: new HarmonyMethod(typeof(CoopSyncFixes).GetMethod(nameof(RewriteRemoteWeaponEquip), Flags)));
        RemoteWeaponEquipReady = true;
    }

    private static IEnumerable<CodeInstruction> RewriteRemoteWeaponEquip(IEnumerable<CodeInstruction> instructions)
    {
        int replaced = 0;
        var replacement = typeof(CoopSyncFixes).GetMethod(nameof(EquipRemoteWeaponOnce), Flags);
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(RemoteNativeEquip))
            {
                // Stack: inventory, item. The third instance-method argument is
                // the sender's requested slot. Keep all labels/exception boundaries.
                var slot = new CodeInstruction(OpCodes.Ldarg_3);
                slot.labels.AddRange(instruction.labels); instruction.labels.Clear();
                slot.blocks.AddRange(instruction.blocks); instruction.blocks.Clear();
                yield return slot;
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException("Pinned remote weapon equip callsite changed");
    }

    private static bool EquipRemoteWeaponOnce(Inventory inventory, InventItem item, int requestedSlot)
    {
        // Native Inventory.add already calls equip. In the pinned receiver the
        // subsequent posID assignment puts the item in its intended remote slot.
        // Calling equip again searches for another empty slot, moving the item.
        // Skip only the exact native item that is already in the requested slot.
        if (!Disabled("REMOTE_WEAPON_SLOT") && inventory != null && item != null &&
            (requestedSlot == 0 || requestedSlot == 1) && item.posID == requestedSlot)
        {
            var equipped = inventory.getEquippedWeaponOn(requestedSlot);
            if (equipped != null && item.HashlinkPointer != IntPtr.Zero &&
                equipped.HashlinkPointer == item.HashlinkPointer)
            {
                Interlocked.Increment(ref RemoteWeaponEquipAlreadyCorrect);
                return true;
            }
        }
        Interlocked.Increment(ref RemoteWeaponEquipForwarded);
        return inventory.equip(item);
    }
}
