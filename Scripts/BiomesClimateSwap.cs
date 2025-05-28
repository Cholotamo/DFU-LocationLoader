using UnityEngine;
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
            //Debug.Log($"[BiomesClimateSwap] Checking {flats.Length} billboards in {rmbBlock.name}");

            foreach (var b in flats)
            {
                // 1) Only nature flats
                if (b.Summary.FlatType != FlatTypes.Nature)
                {
                    //Debug.Log($"  ▶ Skipping non‐nature flat (type={b.Summary.FlatType})");
                    continue;
                }

                // 2) Try grabbing the terrain's MapPixelX/Y:
                int mx = -1, my = -1;
                var terrain = b.GetComponentInParent<DaggerfallTerrain>();
                if (terrain != null)
                {
                    mx = terrain.MapPixelX;
                    my = terrain.MapPixelY;
                    //Debug.Log($"  • Exterior flat; Terrain.MapPixel=({mx},{my})");
                } else
                {
                    Debug.LogWarning($"  ▶ No Terrain or LocationData on {b.name}, skipping");
                    continue;
                }

                // now do your bounds check, flip-Y and sample exactly as before:
                if (mx < 0 || mx >= mapW || my < 0 || my >= mapH)
                {
                    Debug.LogError($"  ✖ LocationData out of range: ({mx},{my}) vs map {mapW}×{mapH}");
                    continue;
                }

                int ty = mapH - 1 - my;
                Color32 c = climate_map.GetPixel(mx, ty);
                //Debug.Log($"  • Sampled #{c.r:X2}{c.g:X2}{c.b:X2} at ({mx},{ty})");

                // 5) Compare
                bool match = (c.r == TriggerColor.r &&
                              c.g == TriggerColor.g &&
                              c.b == TriggerColor.b);

                if (!match)
                {
                    //Debug.Log("    – Color did NOT match trigger; skipping");
                    continue;
                }

                //Debug.Log($"    ✓ Color matched #FFA500 — swapping archive on flat record {b.Summary.Record}");

                // 6) Perform swap
                b.SetMaterial(CUSTOM_ARCHIVE, b.Summary.Record);

                if (!LocationModLoader.VEModEnabled)
                {
                    b.transform.localScale *= 2f;
                    //Debug.Log("      • Scaled flat by 2×");
                }

                // 7) Re-ground
                float halfH = b.Summary.Size.y * b.transform.localScale.y * 0.5f;
                var lp = b.transform.localPosition;
                lp.y = halfH;
                b.transform.localPosition = lp;
                //Debug.Log($"      • Re-grounded to y={lp.y:F3}");
            }
        }
    }
}

