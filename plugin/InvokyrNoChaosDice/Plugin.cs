using System;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppSystem.Collections.Generic;
using _200_Dev.Items;
using Items;
using UnityEngine;
using Mirror;

namespace InvokyrNoChaosDice
{
    [BepInPlugin("top.juluo.invokyr.nochaosdice", "Invokyr No Chaos Dice", "1.2.0")]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource ModLog;

        internal static ConfigEntry<bool> Enable;
        internal static ConfigEntry<bool> BlockMultiEffect;
        internal static ConfigEntry<string> ExtraBlock;
        internal static ConfigEntry<string> Allow;
        internal static ConfigEntry<bool> DebugLog;

        // cache of dice aliases discovered while cleaning the pools
        internal static readonly System.Collections.Generic.HashSet<string> BlockedAliases =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal static readonly System.Collections.Generic.List<string> AllowedAliases =
            new System.Collections.Generic.List<string>();

        public override void Load()
        {
            ModLog = base.Log;

            Enable = Config.Bind("General", "Enable", true,
                "Master switch. When true, chaos / multi-effect combo dice are removed from every spawn pool at board-open time.");
            BlockMultiEffect = Config.Bind("General", "BlockMultiEffectDice", true,
                "Auto-block dice whose name carries multiple numeric effect groups after 'Dice_' (e.g. Dice_1_2, Dice_4_5_6).");
            ExtraBlock = Config.Bind("General", "ExtraBlock", "revivetp,teleRez",
                "Comma-separated extra name/alias substrings to also block (case-insensitive). Default covers known combo dice like TeleRez(revive+tp).");
            Allow = Config.Bind("General", "Allow", "",
                "Comma-separated name/alias substrings that are always kept, even if otherwise flagged.");
            DebugLog = Config.Bind("General", "DebugLog", false,
                "Debug logging (default off). When true, prints the full spawn-pool roster and every die's block/keep judgement for troubleshooting. Keep off for normal play.");

            Harmony.CreateAndPatchAll(typeof(Plugin).Assembly);
            ModLog.LogInfo("[NoChaosDice] loaded. Chaos / multi-effect combo dice are removed from spawn pools at board-open time (host-authoritative). Existing dice in save are left untouched.");
        }

        // Remove chaos / multi-effect combo dice from every spawn pool the game hands dice out of.
        // Runs when the board opens (DiceBox) and when any die entity is spawned (DiceTool),
        // i.e. event-driven at generation time, NOT a timer. Idempotent.
        internal static void EnsurePoolsClean()
        {
            if (!Enable.Value) return;
            lock (CleanLock)
            {
                try
                {
                    int removed = 0;

                    // 0) LIVE SPAWN POOL: every tool the game can randomly hand out (dice included).
                    //    ItemManager is a MonoBehaviour singleton, so this is actually reachable at runtime
                    //    (unlike the ScriptableObject DiceSet/ItemSet, which FindObjectsOfType can't see).
                    try
                    {
                        var mgr = ItemManager.Instance;
                        if (mgr != null)
                        {
                            var pool = mgr.AvailableTools;
                            if (pool != null && pool.Count > 0)
                            {
                                // Full roster dump: print EVERY spawnable tool (dice included), so the
                                // complete dice set can be reviewed even if a die was never spawned today.
                                if (DebugLog.Value && !FullDumpDone)
                                {
                                    FullDumpDone = true;
                                    foreach (var t in pool)
                                    {
                                        if (t == null) continue;
                                        var isDice = IsDiceTool(t);
                                        bool blk = isDice && IsBlockedTool(t, out _);
                                        ModLog?.LogInfo($"[NoChaosDice] available tool='{ToolKey(t)}' isDice={isDice} blocked={blk}");
                                    }
                                }

                                var toRemove = new System.Collections.Generic.List<SOTool>();
                                foreach (var t in pool)
                                {
                                    if (t == null) continue;
                                    if (!IsDiceTool(t)) continue;
                                    if (IsBlockedTool(t, out var key))
                                    {
                                        toRemove.Add(t);
                                        if (t is SODiceTool d) RememberAlias(d, blocked: true);
                                        if (DebugLog.Value)
                                        {
                                            var faces = (t is SODiceTool dd) ? DescribeFaces(dd) : "";
                                            ModLog?.LogInfo($"[NoChaosDice] ItemManager pool die='{key}' faces={faces} -> BLOCK");
                                        }
                                    }
                                    else if (t is SODiceTool d2)
                                    {
                                        RememberAlias(d2, blocked: false);
                                    }
                                }
                                foreach (var t in toRemove)
                                {
                                    pool.Remove(t);
                                    removed++;
                                }
                                if (toRemove.Count > 0)
                                    ModLog?.LogInfo($"[NoChaosDice] cleaned ItemManager.availableTools: removed {toRemove.Count} chaos die(s).");
                            }
                        }
                        else
                        {
                            ModLog?.LogInfo("[NoChaosDice] ItemManager.Instance not ready yet; will retry at next die spawn.");
                        }
                    }
                    catch (Exception e)
                    {
                        ModLog?.LogWarning("[NoChaosDice] ItemManager cleanup error: " + e);
                    }

                    // 1) DiceSet pools (random die pool per difficulty) - reachable if ever loaded.
                    var sets = UnityEngine.Object.FindObjectsOfType<DiceSet>(true);
                    if (sets != null)
                    {
                        foreach (var set in sets)
                        {
                            if (set == null || set.diceSetEntries == null) continue;
                            foreach (var entry in set.diceSetEntries)
                            {
                                if (entry == null || entry.diceSetData == null) continue;
                                foreach (var sd in entry.diceSetData)
                                {
                                    if (sd == null || sd.dices == null) continue;
                                    var src = sd.dices;
                                    if (src.Count == 0) continue;
                                    var filtered = new List<SODiceTool>();
                                    int rm = 0;
                                    foreach (var die in src)
                                    {
                                        if (die == null) { filtered.Add(die); continue; }
                                        if (IsBlocked(die, out var key))
                                        {
                                            rm++;
                                            RememberAlias(die, blocked: true);
                                            if (DebugLog.Value)
                                                ModLog?.LogInfo($"[NoChaosDice] pool={sd.difficulty} die='{key}' faces={DescribeFaces(die)} -> BLOCK");
                                            continue;
                                        }
                                        filtered.Add(die);
                                        RememberAlias(die, blocked: false);
                                        if (DebugLog.Value)
                                            ModLog?.LogInfo($"[NoChaosDice] pool={sd.difficulty} die='{key}' faces={DescribeFaces(die)} -> keep");
                                    }
                                    if (rm > 0)
                                    {
                                        sd.dices = filtered;
                                        removed += rm;
                                        ModLog?.LogInfo($"[NoChaosDice] cleaned dice pool difficulty={sd.difficulty}: {src.Count} -> {filtered.Count} (removed {rm}).");
                                    }
                                }
                            }
                        }
                    }

                    // 2) ItemSet spawn lists (board dice boxes / events pick tools from these).
                    var itemSets = UnityEngine.Object.FindObjectsOfType<ItemSet>(true);
                    if (itemSets != null)
                    {
                        foreach (var set in itemSets)
                        {
                            if (set == null) continue;
                            if (set.spawnItems != null)
                            {
                                foreach (var entry in set.spawnItems)
                                {
                                    if (entry == null || entry.items == null) continue;
                                    var filtered = FilterTools(entry.items, out var rm);
                                    if (rm > 0)
                                    {
                                        entry.items = filtered;
                                        removed += rm;
                                        ModLog?.LogInfo($"[NoChaosDice] cleaned ItemSet.spawnItems entry: removed {rm} chaos die(s).");
                                    }
                                }
                            }
                            if (set.bonusItems != null)
                            {
                                var filtered = FilterTools(set.bonusItems, out var rm);
                                if (rm > 0)
                                {
                                    set.bonusItems = filtered;
                                    removed += rm;
                                    ModLog?.LogInfo($"[NoChaosDice] cleaned ItemSet.bonusItems: removed {rm} chaos die(s).");
                                }
                            }
                        }
                    }

                    if (removed > 0)
                        ModLog?.LogInfo($"[NoChaosDice] pools cleaned: removed {removed} chaos die(s). allowed={AllowedAliases.Count} blocked={BlockedAliases.Count}");
                }
                catch (Exception e)
                {
                    ModLog?.LogWarning("[NoChaosDice] pool cleanup error: " + e);
                }
            }
        }

        // Remove chaos dice (SODiceTool) from a generic SOTool list; returns the filtered list.
        static List<SOTool> FilterTools(List<SOTool> src, out int removed)
        {
            removed = 0;
            var filtered = new List<SOTool>();
            foreach (var item in src)
            {
                if (item == null) { filtered.Add(item); continue; }
                if (IsDiceTool(item) && IsBlockedTool(item, out var key))
                {
                    removed++;
                    if (item is SODiceTool dice) RememberAlias(dice, blocked: true);
                    if (DebugLog.Value)
                    {
                        var faces = (item is SODiceTool dd) ? DescribeFaces(dd) : "";
                        ModLog?.LogInfo($"[NoChaosDice] ItemSet die='{key}' faces={faces} -> BLOCK");
                    }
                    continue;
                }
                filtered.Add(item);
                if (item is SODiceTool d2) RememberAlias(d2, blocked: false);
            }
            return filtered;
        }

        internal static readonly object CleanLock = new object();

        // Only print the full spawn-pool roster once per process. EnsurePoolsClean fires on every
        // dice data sync / spawn, and reprinting all ~130 tools each time floods the log.
        internal static bool FullDumpDone;

        static void RememberAlias(SODiceTool die, bool blocked)
        {
            try
            {
                var alias = die.itemAlias;
                if (string.IsNullOrEmpty(alias)) return;
                if (blocked) BlockedAliases.Add(alias);
                else if (!AllowedAliases.Any(a => a.Equals(alias, StringComparison.OrdinalIgnoreCase)))
                    AllowedAliases.Add(alias);
            }
            catch { }
        }

        internal static bool IsAllowed(string s)
        {
            foreach (var a in Split(Allow.Value))
                if (s.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // Identity-based blocking, judged by the die's item name/alias, NOT by counting faces
        // (every functional die carries at least one debuff face, so faces can't tell chaos apart).
        internal static bool IsBlocked(SODiceTool die, out string key)
        {
            key = "";
            if (die == null) return false;
            var name = Safe(() => die.name) ?? "";
            var alias = Safe(() => die.itemAlias) ?? "";
            return IsBlockedByName(name, alias, out key);
        }

        // Identity-based blocking, judged by the die's item name/alias, NOT by counting faces
        // (every functional die carries at least one debuff face, so faces can't tell chaos apart).
        // Name/alias based so it also covers pool tools that aren't SODiceTool.
        internal static bool IsBlockedByName(string name, string alias, out string key)
        {
            key = "";
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(alias)) return false;
            key = !string.IsNullOrEmpty(name) ? name : alias;
            if (string.IsNullOrEmpty(key)) return false;

            foreach (var a in Split(Allow.Value))
                if (key.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0) return false;

            // Explicit "chaos" marking (e.g. Data-Dice_Chaos_2 / Data-Dice_CubeChaos) -> always block.
            if (key.IndexOf("chaos", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (!string.IsNullOrEmpty(alias) && alias.IndexOf("chaos", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            // Extra combo/substring blocks (e.g. TeleRez = revive+tp).
            foreach (var b in Split(ExtraBlock.Value))
                if (key.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (!string.IsNullOrEmpty(alias) && alias != key)
                foreach (var b in Split(ExtraBlock.Value))
                    if (alias.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0) return true;

            // Multi-effect combo dice detected by splitting the name on camelCase boundaries and
            // counting how many distinct functional effects appear (e.g. BoomHeal=boom+heal,
            // CocoonFire=cocoon+fire, TeleRez=tele+rez). Two+ effects => chaos.
            if (IsMultiEffectName(name)) return true;

            // Numeric multi-effect suffix (e.g. Dice_1_2, Dice_4_5_6, Dice_Hazard_1_6).
            if (BlockMultiEffect.Value)
            {
                if (HasMultiNumberSuffix(key)) return true;
                if (!string.IsNullOrEmpty(alias) && alias != key && HasMultiNumberSuffix(alias)) return true;
            }

            return false;
        }

        // Whether a spawn-pool tool is a die: either a functional SODiceTool or any tool whose
        // name carries the "Dice" marker. The live pool holds several non-SODiceTool dice variants
        // that must still be blocked if chaos (e.g. Data-Dice_1_2, Data-Dice_BoomCocoon).
        internal static bool IsDiceTool(SOTool t)
        {
            if (t is SODiceTool) return true;
            var n = Safe(() => t.name) ?? "";
            return n.IndexOf("Dice", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool IsBlockedTool(SOTool t, out string key)
        {
            key = "";
            if (t == null) return false;
            if (t is SODiceTool d) return IsBlocked(d, out key);
            var name = Safe(() => t.name) ?? "";
            return IsBlockedByName(name, "", out key);
        }

        // Functional effect words used to recognise combo dice. Words must match an entire
        // camelCase token (Equals), never a substring, so e.g. "Light" != "Lightning".
        static readonly string[] EffectWords =
        {
            "heal", "boom", "fire", "cocoon", "teleport", "revive", "lightning", "light",
            "freeze", "gather", "scan", "pelote", "fireworks", "antigravity", "tele", "rez", "explosion"
        };

        internal static bool IsMultiEffectName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string core = name;
            int idx = core.LastIndexOf("Dice_", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) core = core.Substring(idx + 5);
            core = core.Replace("Data-", "").Replace("-", "");

            var tokens = SplitCamel(core);
            if (tokens.Length == 0) return false;
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int hits = 0;
            foreach (var tok in tokens)
            {
                foreach (var w in EffectWords)
                {
                    if (tok.Equals(w, StringComparison.OrdinalIgnoreCase) && seen.Add(w))
                    {
                        hits++;
                        break;
                    }
                }
            }
            return hits >= 2;
        }

        // Split on camelCase boundaries (a new token starts where an upper-case follows a lower-case).
        static string[] SplitCamel(string s)
        {
            if (string.IsNullOrEmpty(s)) return Array.Empty<string>();
            var parts = new System.Collections.Generic.List<string>();
            int start = 0;
            for (int i = 1; i < s.Length; i++)
            {
                if (char.IsUpper(s[i]) && char.IsLower(s[i - 1]))
                {
                    parts.Add(s.Substring(start, i - start));
                    start = i;
                }
            }
            parts.Add(s.Substring(start));
            return parts.ToArray();
        }

        static bool HasMultiNumberSuffix(string s)
        {
            int idx = s.LastIndexOf("Dice_", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            string suffix = s.Substring(idx + 5);
            int nums = suffix.Split('_').Count(seg => int.TryParse(seg.Trim(), out _));
            return nums >= 2;
        }

        internal static string DescribeFaces(SODiceTool die)
        {
            try
            {
                var arr = die.diceRotationInfos;
                if (arr == null || arr.Count == 0) return "(no faces)";
                var names = new System.Collections.Generic.List<string>();
                for (int i = 0; i < arr.Count; i++)
                {
                    var r = arr[i];
                    if (r == null) { names.Add("?"); continue; }
                    var f = r.soFaceResult;
                    names.Add(f != null ? f.GetType().Name : (string.IsNullOrEmpty(r.faceResult) ? "?" : r.faceResult));
                }
                return "[" + string.Join(", ", names) + "]";
            }
            catch (Exception e) { return "(faces err: " + e.Message + ")"; }
        }

        static string[] Split(string v)
        {
            if (string.IsNullOrEmpty(v)) return Array.Empty<string>();
            return v.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        }

        internal static T Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }

        internal static string SafeName(SODiceTool d) { try { return d?.name ?? "(unnamed)"; } catch { return "(unnamed)"; } }

        // Human-readable key for any SOTool (dice or other tool) for roster logging.
        internal static string ToolKey(SOTool t)
        {
            try
            {
                if (t is SODiceTool d) return SafeName(d);
                var n = Safe(() => t.name);
                return string.IsNullOrEmpty(n) ? "(unnamed)" : n;
            }
            catch { return "(unnamed)"; }
        }
    }

    // Fires on the server when a DiceTool entity is spawned (host side). At that point we
    // clean the spawn pools (so all future spawns come from a chaos-free pool). Existing dice
    // entities are left untouched (save-loaded chaos dice are the user's choice to keep).
    [HarmonyPatch(typeof(DiceTool), "OnStartServer")]
    public static class DiceTool_OnStartServer_Patch
    {
        static void Postfix(DiceTool __instance)
        {
            try
            {
                if (!Plugin.Enable.Value) return;
                if (!Mirror.NetworkServer.active) return; // host only
                var dd = Plugin.Safe(() => __instance.diceData);
                if (Plugin.DebugLog.Value)
                    Plugin.ModLog?.LogInfo($"[NoChaosDice] OnStartServer: die='{Plugin.SafeName(dd)}' blocked={dd != null && Plugin.IsBlocked(dd, out _)}");
                Plugin.EnsurePoolsClean();
            }
            catch (Exception e)
            {
                Plugin.ModLog?.LogWarning("[NoChaosDice] OnStartServer postfix error: " + e);
            }
        }
    }

    // The board dice box initialises when the board is opened (before any dice are handed out).
    // This is the earliest, cleanest moment to purge chaos dice from the live spawn pools.
    [HarmonyPatch(typeof(DiceBox), "OnStartServer")]
    public static class DiceBox_OnStartServer_Patch
    {
        static void Postfix()
        {
            try
            {
                if (!Plugin.Enable.Value) return;
                if (!Mirror.NetworkServer.active) return; // host only
                Plugin.ModLog?.LogInfo("[NoChaosDice] DiceBox opened (host) -> purging chaos dice from spawn pools.");
                Plugin.EnsurePoolsClean();
            }
            catch (Exception e)
            {
                Plugin.ModLog?.LogWarning("[NoChaosDice] DiceBox.OnStartServer postfix error: " + e);
            }
        }
    }

    // Universal choke point: fires every time a die's data is set/synced (Mirror SyncVar hook).
    // Kept as an extra safety net to clean the pools on the host whenever a die data changes.
    [HarmonyPatch(typeof(DiceTool), "OnDiceDataChangedSync")]
    public static class DiceTool_OnDiceDataChangedSync_Patch
    {
        static void Postfix(DiceTool __instance)
        {
            try
            {
                if (!Plugin.Enable.Value) return;
                if (!Mirror.NetworkServer.active) return; // host owns the pools
                var dd = Plugin.Safe(() => __instance.diceData);
                if (dd == null) return;
                Plugin.EnsurePoolsClean();
            }
            catch (Exception e)
            {
                Plugin.ModLog?.LogWarning("[NoChaosDice] OnDiceDataChangedSync postfix error: " + e);
            }
        }
    }
}
