using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Mirror;

namespace InvokyrBlessControl
{
    // "可控祝福" —— 独立的棋盘骰子 mod。
    // 棋盘上决定前进点数的骰子（Dice_Blessed_* / Dice_Cursed_* / Dice_Blursed_* 等）的面是
    // BoardGame.Elements.SOFaceResultCellEffect（digitResult 点数 + alterationSubType 祝福/诅咒）。
    // 本 mod 在房主端拦截该面效果执行，可单独开关祝福面 / 诅咒面，骰子照常出点数。
    [BepInPlugin("top.juluo.invokyr.blesscontrol", "Invokyr Bless Control", "1.0.0")]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource ModLog;

        internal static ConfigEntry<bool> Enable;
        internal static ConfigEntry<int> BlessHealAmount;
        internal static ConfigEntry<bool> CurseEnabled;

        public override void Load()
        {
            ModLog = base.Log;

            Enable = Config.Bind("General", "Enable", true,
                "Master switch. When true, board blessing / curse face effects are controlled by the settings below.");
            BlessHealAmount = Config.Bind("BoardDice", "BlessHealAmount", 50,
                "Amount of health a board blessing face heals everyone. Set 0 to remove the blessing effect entirely " +
                "(the die still rolls its normal point count). NOTE: v1 only supports 0 (off) vs native (any >0 value still heals the game default).");
            CurseEnabled = Config.Bind("BoardDice", "CurseEnabled", false,
                "Whether a board curse face applies its curse effect. Set false to remove curse effects (the die still rolls its normal point count).");

            Harmony.CreateAndPatchAll(typeof(Plugin).Assembly);
            ModLog.LogInfo("[InvokyrBlessControl] loaded. Board blessing/curse faces are controllable (points unaffected).");
        }
    }

    // Intercept a board movement die's face effect. The die still rolls its normal point count;
    // only the blessing / curse side effect is skipped or kept per the config. Host authoritative.
    [HarmonyPatch(typeof(BoardGame.Elements.SOFaceResultCellEffect), "SrvExecuteEffect")]
    public static class BoardFace_SrvExecuteEffect_Patch
    {
        static bool Prefix(BoardGame.Elements.SOFaceResultCellEffect __instance)
        {
            try
            {
                if (!Plugin.Enable.Value) return true;
                if (!Mirror.NetworkServer.active) return true; // host authoritative only

                bool blessing;
                EAlterationSubType sub;
                try
                {
                    blessing = __instance.IsBlessing;
                    sub = __instance.alterationSubType;
                }
                catch { return true; }

                if (blessing || sub == EAlterationSubType.BLESSING)
                {
                    Plugin.ModLog?.LogInfo($"[BlessControl] board blessing face: healAmount={Plugin.BlessHealAmount.Value} -> effect={(Plugin.BlessHealAmount.Value > 0 ? "native" : "removed")}");
                    return Plugin.BlessHealAmount.Value > 0; // 0 => no blessing (points normal)
                }
                if (sub == EAlterationSubType.CURSE)
                {
                    Plugin.ModLog?.LogInfo($"[BlessControl] board curse face: curseEnabled={Plugin.CurseEnabled.Value}");
                    return Plugin.CurseEnabled.Value; // false => no curse (points normal)
                }
                return true; // pure point face -> keep native
            }
            catch (Exception e)
            {
                Plugin.ModLog?.LogWarning("[BlessControl] board face patch error: " + e);
                return true;
            }
        }
    }
}
