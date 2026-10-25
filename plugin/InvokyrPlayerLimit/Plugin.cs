using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;
using _200_Dev.Networking;
using _200_Dev.Portrait;
using _200_Dev.Utility.Runtime.NetworkManager;
using User;

namespace InvokyrPlayerLimit
{
    [BepInPlugin("top.juluo.invokyr.playerlimit", "Invokyr Player Limit", "1.1.0")]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource ModLog;
        internal static ConfigEntry<bool> Enable;
        internal static ConfigEntry<int> PlayerLimit;
        internal static ConfigEntry<bool> ExtraPortraits;
        internal static ConfigEntry<bool> EnableReplicate;
        internal static ConfigEntry<int> ExtraCharacters;

        public override void Load()
        {
            ModLog = base.Log;
            Enable = Config.Bind("General", "Enable", true, "总开关：关闭后本 mod 不干预人数上限。");
            PlayerLimit = Config.Bind("General", "PlayerLimit", 8, "期望的最大玩家数（房主端生效）。游戏默认上限为 6；实际生效值 = min(本配置, 可用角色数)，内置可用角色为 8。");
            ExtraPortraits = Config.Bind("General", "ExtraPortraits", true, "是否把大厅玩家头像格补齐到人数上限。克隆头像格会作为独立网络对象注册（切换建模正常，无报错）。");
            EnableReplicate = Config.Bind("Replicate", "EnableReplicate", false, "（实验性，默认关闭）是否复刻内置角色以扩充可用角色数。当前验证：native 端不识别注入的复刻角色（分配仍只认 8 个内置角色），并可能导致返回主菜单崩溃，建议保持关闭。");
            ExtraCharacters = Config.Bind("Replicate", "ExtraCharacters", 8, "复刻新增的角色数量（总角色数 = 内置角色数 + 本值，如 8 + 8 = 16）。");
            Harmony.CreateAndPatchAll(typeof(Plugin).Assembly);
            ModLog.LogInfo("[PlayerLimit] loaded. Host-side patch active (Enable=" + Enable.Value + ", PlayerLimit=" + PlayerLimit.Value + ", ExtraPortraits=" + ExtraPortraits.Value + ", Replicate=" + EnableReplicate.Value + "x" + ExtraCharacters.Value + ").");
        }

        // The effective player cap cannot exceed the number of available mission characters:
        // every player must occupy one unique character model (they cannot be repeated). We clamp
        // the configured limit against UserInstancesManager.CharacterCount.
        internal static int GetEffectiveLimit()
        {
            int configured = PlayerLimit.Value;
            if (configured <= 0) return configured;
            try
            {
                int charCount = UserInstancesManager.CharacterCount;
                if (charCount > 0 && configured > charCount)
                {
                    if (ModLog != null) ModLog.LogInfo("[PlayerLimit] clamped " + configured + " -> " + charCount + " (available mission characters).");
                    return charCount;
                }
            }
            catch (Exception e)
            {
                if (ModLog != null) ModLog.LogWarning("[PlayerLimit] read CharacterCount failed: " + e.Message);
            }
            return configured;
        }

        // Add replica characters to the roster. Each player must occupy a unique character model,
        // and the built-in EPlayerCharacter enum only has 8 values, so to go beyond 8 players we
        // inject extra roster entries that reuse an existing character's rigs/materials but carry a
        // forged (undefined) enum value (8..8+N). Whether the native allocation code actually picks
        // these up is exactly what we are probing.
        internal static void InjectReplicas()
        {
            try
            {
                if (!EnableReplicate.Value) return;
                var mgr = UserInstancesManager.Instance;
                if (mgr == null) return;
                var infos = mgr.PlayerCharacterInfos;
                if (infos == null) return;
                int baseCount = infos.Count;
                if (baseCount <= 0) return;
                int extra = ExtraCharacters.Value;
                if (extra <= 0) return;
                int target = baseCount + extra;
                if (infos.Count >= target) return; // already injected

                for (int i = 0; i < extra && (baseCount + i) < target; i++)
                {
                    var template = infos[i % baseCount];
                    // Value-copy the template struct so the replica keeps the template's valid native
                    // field pointers (rigs, materials). Constructing a fresh FPlayerCharacterInfo()
                    // leaves those pointers as garbage and crashes on the first field access.
                    var clone = template;
                    clone.playerCharacter = (User.UserInstancesManager.EPlayerCharacter)(baseCount + i);
                    clone.characterName = template.characterName + " " + (i + 1);
                    infos.Add(clone);
                }
                if (ModLog != null) ModLog.LogInfo("[PlayerLimit] injected " + extra + " replica characters -> roster " + baseCount + " -> " + infos.Count);
            }
            catch (Exception e)
            {
                if (ModLog != null) ModLog.LogWarning("[PlayerLimit] InjectReplicas: " + e);
            }
        }
    }

    // The game reports its playable roster size via the static CharacterCount. We report the
    // enlarged roster so every read site (capacity checks, lobby display, character cycling)
    // sees the full 16. Uses __result (the original value) to avoid recursion.
    [HarmonyPatch(typeof(UserInstancesManager), "get_CharacterCount")]
    public static class CharacterCount_Patch
    {
        static void Postfix(ref int __result)
        {
            try
            {
                if (!Plugin.Enable.Value || !Plugin.EnableReplicate.Value) return;
                __result = __result + Plugin.ExtraCharacters.Value;
            }
            catch (Exception e)
            {
                if (Plugin.ModLog != null) Plugin.ModLog.LogWarning("[PlayerLimit] get_CharacterCount postfix: " + e);
            }
        }
    }

    // Force the game's reported player cap (VhsNetworkManager.MaxPlayers) to at least our
    // effective value wherever it is read. The cap is used both for capacity checks and for
    // UI / lobby display, so clamping the getter covers every read site.
    [HarmonyPatch(typeof(VhsNetworkManager), "get_MaxPlayers")]
    public static class MaxPlayers_Patch
    {
        static void Postfix(ref int __result)
        {
            try
            {
                if (!Plugin.Enable.Value) return;
                int limit = Plugin.GetEffectiveLimit();
                if (__result < limit) __result = limit;
            }
            catch (Exception e)
            {
                if (Plugin.ModLog != null) Plugin.ModLog.LogWarning("[PlayerLimit] get_MaxPlayers postfix: " + e);
            }
        }
    }

    // When the host creates the Steam lobby, force the lobby member cap to at least our
    // effective value. The lobby cap is what Steam enforces on who can join at the network layer.
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.Create))]
    public static class SteamLobbyCreate_Patch
    {
        static void Prefix(ref int maxMembers)
        {
            try
            {
                if (!Plugin.Enable.Value) return;
                int limit = Plugin.GetEffectiveLimit();
                maxMembers = Math.Max(maxMembers, limit);
            }
            catch (Exception e)
            {
                if (Plugin.ModLog != null) Plugin.ModLog.LogWarning("[PlayerLimit] Create prefix: " + e);
            }
        }
    }

    // The lobby shows one player portrait (avatar card) per player. That list (_portraits) is
    // pre-placed in the scene with a fixed count (the game's default cap, 6). Raising the player
    // cap without adding portrait cards means the lobby still shows only the pre-placed cards,
    // so the 7th/8th players would have no card. On the server we clone the existing cards to
    // fill the list up to our effective limit and place the new ones in line with the originals.
    [HarmonyPatch(typeof(PortraitManager), "OnStartServer")]
    public static class PortraitManagerOnStartServer_Patch
    {
        static void Postfix(PortraitManager __instance)
        {
            // If replica injection is enabled (experimental), add replica characters before the
            // portrait cards are laid out so the lobby reflects the enlarged roster.
            Plugin.InjectReplicas();

            try
            {
                if (!Plugin.Enable.Value || !Plugin.ExtraPortraits.Value) return;
                int limit = Plugin.GetEffectiveLimit();
                if (limit <= 0) return;

                var list = __instance.Portrait;
                if (list == null) return;
                int baseCount = list.Count;
                if (baseCount <= 0) return;
                if (baseCount >= limit) return; // already has enough (or more)

                var template = list[0];
                if (template == null) return;

                // Infer the placement offset from the first two existing cards (fallback 1.0 on X).
                var spacing = new Vector3(1f, 0f, 0f);
                if (baseCount >= 2 && list[1] != null)
                    spacing = list[1].transform.localPosition - list[0].transform.localPosition;
                if (spacing.sqrMagnitude < 0.0001f) spacing = new Vector3(1f, 0f, 0f);

                var parent = template.transform.parent;
                var origin = template.transform.localPosition;

                for (int i = baseCount; i < limit; i++)
                {
                    var go = UnityEngine.Object.Instantiate(template.gameObject);
                    go.name = "PlayerPortrait_Extra_" + i;
                    var p = go.GetComponent<PlayerPortrait>();
                    if (p == null)
                    {
                        UnityEngine.Object.Destroy(go);
                        if (Plugin.ModLog != null) Plugin.ModLog.LogWarning("[PlayerLimit] clone portrait " + i + " missing PlayerPortrait; skipped.");
                        continue;
                    }
                    go.transform.SetParent(parent, false);
                    go.transform.localPosition = origin + spacing * i;
                    // Force the empty-slot look (black no-owner material + blank name label) so the
                    // cloned cards match the other vacant portraits instead of inheriting the
                    // template's current (possibly occupied / white) rendering state.
                    try
                    {
                        var mr = p.portraitRenderer;
                        if (mr != null && p._nonePortraitMaterial != null) mr.material = p._nonePortraitMaterial;
                    }
                    catch (Exception e) { if (Plugin.ModLog != null) Plugin.ModLog.LogWarning("[PlayerLimit] set none-material: " + e.Message); }
                    try
                    {
                        if (p._textMesh != null) p._textMesh.text = "";
                    }
                    catch (Exception e) { if (Plugin.ModLog != null) Plugin.ModLog.LogWarning("[PlayerLimit] clear label: " + e.Message); }

                    // The cloned card must be a networked object like the original scene cards:
                    // CliIsCharacterFree / CliSelectCharacter read a Mirror SyncVar on every card,
                    // which NREs without a network identity. The scene cards have no own identity
                    // (they inherit PortraitManager's); a runtime clone does not get registered, so
                    // give the clone its own NetworkIdentity and spawn it as a networked object.
                    try
                    {
                        var ni = go.GetComponent<Mirror.NetworkIdentity>();
                        if (ni == null && Mirror.NetworkServer.active)
                            ni = go.AddComponent<Mirror.NetworkIdentity>();
                        if (ni != null && Mirror.NetworkServer.active)
                        {
                            Mirror.NetworkServer.Spawn(go);
                            if (Plugin.ModLog != null) Plugin.ModLog.LogInfo("[PlayerLimit] spawned cloned portrait " + i + " netId=" + ni.netId);
                        }
                        else
                        {
                            if (Plugin.ModLog != null) Plugin.ModLog.LogWarning("[PlayerLimit] clone " + i + " could not get network identity; NRE may persist.");
                        }
                    }
                    catch (Exception e) { if (Plugin.ModLog != null) Plugin.ModLog.LogWarning("[PlayerLimit] spawn clone " + i + ": " + e.Message); }

                    list.Add(p);
                }

                if (Plugin.ModLog != null) Plugin.ModLog.LogInfo("[PlayerLimit] portrait cards filled " + baseCount + " -> " + list.Count + " (limit " + limit + ").");

                // The wall has room for only the pre-placed cards. Instead of letting the extra
                // cards spill past the last one (which lands them in furniture), re-lay-out ALL
                // cards evenly across the original wall span. list order is right->left.
                if (baseCount >= 2 && list.Count >= 2)
                {
                    float maxX = list[0].transform.localPosition.x;
                    float minX = list[baseCount - 1].transform.localPosition.x;
                    float step = (maxX - minX) / (list.Count - 1);
                    for (int i = 0; i < list.Count; i++)
                    {
                        var pp = list[i];
                        if (pp == null) continue;
                        var lp = pp.transform.localPosition;
                        pp.transform.localPosition = new Vector3(maxX - step * i, lp.y, lp.z);
                    }
                    if (Plugin.ModLog != null) Plugin.ModLog.LogInfo("[PlayerLimit] portrait re-layout done: step=" + step.ToString("0.###") + " range=[" + minX.ToString("0.###") + "," + maxX.ToString("0.###") + "] cards=" + list.Count);
                }
            }
            catch (Exception e)
            {
                if (Plugin.ModLog != null) Plugin.ModLog.LogWarning("[PlayerLimit] PortraitManager.OnStartServer postfix: " + e);
            }
        }
    }
}
