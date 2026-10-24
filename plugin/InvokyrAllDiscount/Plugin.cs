using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using _200_Dev.EnvironmentElements.Shop;
using Il2CppSystem.Collections.Generic;

namespace InvokyrAllDiscount
{
    [BepInPlugin("top.juluo.invokyr.alldiscount", "Invokyr All Discount", "1.2.1")]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource ModLog;
        internal static ConfigEntry<bool> Enable;
        internal static ConfigEntry<float> Discount;

        public override void Load()
        {
            ModLog = base.Log;
            Enable = Config.Bind("General", "Enable", true, "总开关：关闭后本 mod 不干预商店价格。");
            Discount = Config.Bind("General", "Discount", 0.7f, "打折倍率：最终价 = 原价 × 倍率（0.0~1.0，0.7 = 7 折，1.0 = 原价）。");
            Harmony.CreateAndPatchAll(typeof(Plugin).Assembly);
            ModLog.LogInfo("[AllDiscount] loaded. Host-side patch active (Enable=" + Enable.Value + ", Discount=" + Discount.Value + ").");
        }
    }

    // Runs server-side (host) every time shop items are instantiated. The game's own
    // SrvApplyDiscount sets the price to originalPrice * 0.7, so we recover the original
    // price by dividing by 0.7 and then apply the configurable multiplier
    // (finalPrice = originalPrice * factor). Prices/isDiscounted are Mirror SyncVars, so the
    // change propagates to every connected client; clients need no mod.
    [HarmonyPatch(typeof(ShopItemSlotsManager), nameof(ShopItemSlotsManager.SrvInstantiateItems))]
    public static class SrvInstantiateItems_Patch
    {
        const float GameDiscount = 0.7f;   // the game's built-in discount factor

        static void Postfix(ShopItemSlotsManager __instance)
        {
            try
            {
                if (__instance == null || !Plugin.Enable.Value) return;

                int discounted = 0;
                DiscountAll(__instance._upgradeSlots, ref discounted);
                DiscountAll(__instance._furnitureSlots, ref discounted);
                DiscountAll(__instance._gadgetSlots, ref discounted);
                DiscountAll(__instance._consumableSlots, ref discounted);

                if (Plugin.ModLog != null)
                    Plugin.ModLog.LogInfo($"[AllDiscount] shop setup complete: {discounted} item(s) discounted (factor=" + Plugin.Discount.Value + ").");
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
                try
                {
                    // Ensure the item is discounted (skip if the game already discounted it).
                    if (!slot.Network_isDiscounted)
                        slot.SrvApplyDiscount();
                    // Recover the original price from the game's discounted price (divide by 0.7),
                    // then apply the configurable multiplier.
                    float discountedPrice = slot.Network_price;
                    float original = discountedPrice / GameDiscount;
                    int price = (int)Math.Round(original * Plugin.Discount.Value);
                    if (price < 0) price = 0;
                    slot.Network_price = price;
                    slot.Network_isDiscounted = true;
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
