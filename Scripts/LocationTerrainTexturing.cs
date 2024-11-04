using System.Collections.Generic;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using Unity.Jobs;
using UnityEngine;

namespace LocationLoader
{
    public class LocationTerrainTexturing : DefaultTerrainTexturing
    {
        public override JobHandle ScheduleAssignTilesJob(ITerrainSampler terrainSampler, ref MapPixelData mapData,
            JobHandle dependencies, bool march = true)
        {
            SetLocationRMBTiles(ref mapData);
            var mapFileReader = DaggerfallUnity.Instance.ContentReader.MapFileReader;

            return base.ScheduleAssignTilesJob(terrainSampler, ref mapData, dependencies, march);
        }

        static void SetLocationRMBTiles(ref MapPixelData mapData)
        {
            var ll = LocationModLoader.modObject.GetComponent<LocationLoader>();

            ll.GetTerrainExtraData(new Vector2Int(mapData.mapPixelX, mapData.mapPixelY));

            /*
            // Get location
            DaggerfallUnity dfUnity = DaggerfallUnity.Instance;
            DFLocation location = dfUnity.ContentReader.MapFileReader.GetLocation(mapPixel.mapRegionIndex, mapPixel.mapLocationIndex);

            // Position tiles inside terrain area
            DFPosition tilePos = TerrainHelper.GetLocationTerrainTileOrigin(location);

            // Full 8x8 locations have "terrain blend space" around walls to smooth down random terrain towards flat area.
            // This is indicated by texture index > 55 (ground texture range is 0-55), larger values indicate blend space.
            // We need to know rect of actual city area so we can use blend space outside walls.
            int xmin = int.MaxValue, ymin = int.MaxValue;
            int xmax = 0, ymax = 0;

            // Iterate blocks of this location
            for (int blockY = 0; blockY < location.Exterior.ExteriorData.Height; blockY++)
            {
                for (int blockX = 0; blockX < location.Exterior.ExteriorData.Width; blockX++)
                {
                    // Get block data
                    DFBlock block;
                    string blockName = dfUnity.ContentReader.MapFileReader.GetRmbBlockName(location, blockX, blockY);
                    if (!dfUnity.ContentReader.GetBlock(blockName, out block))
                        continue;

                    // Copy ground tile info
                    for (int tileY = 0; tileY < RMBLayout.RMBTilesPerBlock; tileY++)
                    {
                        for (int tileX = 0; tileX < RMBLayout.RMBTilesPerBlock; tileX++)
                        {
                            DFBlock.RmbGroundTiles tile = block.RmbBlock.FldHeader.GroundData.GroundTiles[tileX, (RMBLayout.RMBTilesPerBlock - 1) - tileY];
                            int xpos = tilePos.X + blockX * RMBLayout.RMBTilesPerBlock + tileX;
                            int ypos = tilePos.Y + blockY * RMBLayout.RMBTilesPerBlock + tileY;

                            if (tile.TextureRecord < 56)
                            {
                                // Track interior bounds of location tiled area
                                if (xpos < xmin) xmin = xpos;
                                if (xpos > xmax) xmax = xpos;
                                if (ypos < ymin) ymin = ypos;
                                if (ypos > ymax) ymax = ypos;

                                // Store texture data from block
                                mapPixel.tilemapData[JobA.Idx(xpos, ypos, MapsFile.WorldMapTileDim)] = tile.TileBitfield == 0 ? byte.MaxValue : tile.TileBitfield;
                            }
                        }
                    }
                }
            }

            // Update location rect with extra clearance
            int extraClearance = location.MapTableData.LocationType == DFRegion.LocationTypes.TownCity ? 3 : 2;
            Rect locationRect = new Rect();
            locationRect.xMin = xmin - extraClearance;
            locationRect.xMax = xmax + extraClearance;
            locationRect.yMin = ymin - extraClearance;
            locationRect.yMax = ymax + extraClearance;
            mapPixel.locationRect = locationRect;
            */
        }
    }
}