using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using _200_Dev.EnvironmentElements.Shop;
using Il2CppSystem.Collections.Generic;

namespace InvokyrAllDiscount
{
    [BepInPlugin("top.juluo.invokyr.alldiscount", "Invokyr All Discount", "1.0.0")]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource ModLog;

        public override void Load()
        {
            ModLog = base.Log;
            Harmony.CreateAndPatchAll(typeof(Plugin).Assembly);
            ModLog.LogInfo("[AllDiscount] loaded. Host-side patch active: all shop items will be discounted and synced to clients.");
        }
    }

    // Runs server-side (host) every time shop items are instantiated.
    // After the game randomly discounts a handful of slots, we discount every
    // remaining slot. All prices/isDiscounted are Mirror SyncVars, so the change
    // propagates to every connected client automatically. Clients need no mod.
    [HarmonyPatch(typeof(ShopItemSlotsManager), nameof(ShopItemSlotsManager.SrvInstantiateItems))]
    public static class SrvInstantiateItems_Patch
    {
        static void Postfix(ShopItemSlotsManager __instance)
        {
            try
            {
                if (__instance == null) return;

                int discounted = 0;
                DiscountAll(__instance._upgradeSlots, ref discounted);
                DiscountAll(__instance._furnitureSlots, ref discounted);
                DiscountAll(__instance._gadgetSlots, ref discounted);
                DiscountAll(__instance._consumableSlots, ref discounted);

                if (Plugin.ModLog != null)
                    Plugin.ModLog.LogInfo($"[AllDiscount] shop setup complete: {discounted} item(s) additionally discounted (synced to clients).");
            }
            catch (Exception e)
            {
                if (Plugin.ModLog != null) Plugin.ModLog.LogWarning("[AllDiscount] postfix error: " + e);
            }
        }

        static void DiscountAll<T>(List<T> slots, ref int count) where T : ShopItemSlot
        {
            if (slots == null) return;
            foreach (var slot in slots)
            {
                if (slot == null) continue;
                // skip items the game already discounted
                if (slot.Network_isDiscounted) continue;
                try
                {
                    slot.SrvApplyDiscount();
                    count++;
                }
                catch (Exception e)
                {
                    if (Plugin.ModLog != null) Plugin.ModLog.LogWarning("[AllDiscount] failed to discount a slot: " + e.Message);
                }
            }
        }
    }
}
