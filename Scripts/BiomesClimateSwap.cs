using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using DaggerfallWorkshop.Utility.AssetInjection;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;

namespace LocationLoader
{
    public static class BiomesClimateSwap
    {
        const int    CUSTOM_ARCHIVE = 10030;
        static Color32 TriggerColor  = new Color32(255,165,0,255);

        // pending swaps waiting for terrain to be ready
        static bool isSubscribed = false;
        static List<GameObject> pendingBlocks = new List<GameObject>();

        static BiomesClimateSwap()
        {
            // subscribe once to StreamingWorld end-of-update event
            if (!isSubscribed)
            {
                StreamingWorld.OnUpdateTerrainsEnd += OnUpdateTerrainsEnd;
                isSubscribed = true;
            }
        }

        public static void ApplySwaps(GameObject rmbBlock)
        {
            var climate_map = LocationModLoader.climate_map;
            if (climate_map == null || !climate_map.isReadable)
            {
                Debug.LogError("[BiomesClimateSwap] climate_map missing or not readable");
                return;
            }

            int mapW = climate_map.width;
            int mapH = climate_map.height;

            var flats = rmbBlock.GetComponentsInChildren<Billboard>(true);
            foreach (var b in flats)
            {
                if (b.Summary.FlatType != FlatTypes.Nature)
                    continue;

                var terrain = b.GetComponentInParent<DaggerfallTerrain>();
                if (terrain == null)
                {
                    // schedule retry next update
                    Debug.LogWarning($"[BiomesClimateSwap] Terrain not yet ready for {b.name}; deferring swap.");
                    if (!pendingBlocks.Contains(rmbBlock))
                        pendingBlocks.Add(rmbBlock);
                    return;
                }

                int mx = terrain.MapPixelX;
                int my = terrain.MapPixelY;

                if (mx < 0 || mx >= mapW || my < 0 || my >= mapH)
                {
                    Debug.LogError($"[BiomesClimateSwap] LocationData out of range: ({mx},{my}) vs map {mapW}×{mapH}");
                    continue;
                }

                int ty = mapH - 1 - my;
                Color32 c = climate_map.GetPixel(mx, ty);
                if (c.r != TriggerColor.r || c.g != TriggerColor.g || c.b != TriggerColor.b)
                    continue;

                // swap material
                b.SetMaterial(CUSTOM_ARCHIVE, b.Summary.Record);
                if (!LocationModLoader.VEModEnabled)
                    b.transform.localScale *= 2f;

                // re-ground
                float halfH = b.Summary.Size.y * b.transform.localScale.y * 0.5f;
                var lp = b.transform.localPosition;
                lp.y = halfH;
                b.transform.localPosition = lp;
            }
        }

        static void OnUpdateTerrainsEnd()
        {
            // retry all pending swaps now that terrains are (re)parented
            if (pendingBlocks.Count == 0)
                return;

            var retryList = new List<GameObject>(pendingBlocks);
            pendingBlocks.Clear();
            foreach (var block in retryList)
            {
                if (block != null)
                    ApplySwaps(block);
            }
        }
    }
}
