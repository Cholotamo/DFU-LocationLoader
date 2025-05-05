using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Utility;
using System;
using System.Runtime.CompilerServices;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.Entity;
using static DaggerfallWorkshop.Utility.ContentReader;
using DaggerfallWorkshop.Utility.AssetInjection;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;

namespace LocationLoader
{
    public class LocationLoader : MonoBehaviour
    {
        Dictionary<Vector2Int, WeakReference<DaggerfallTerrain>> loadedTerrain = new Dictionary<Vector2Int, WeakReference<DaggerfallTerrain>>();
        Dictionary<Vector2Int, List<LocationData>> pendingIncompleteLocations = new Dictionary<Vector2Int, List<LocationData>>();
        Dictionary<ulong, List<Vector2Int>> instancePendingTerrains = new Dictionary<ulong, List<Vector2Int>>();

        public class LLTerrainData
        {
            public List<Rect> LocationInstanceRects = new List<Rect>();
            public List<LocationData> LocationInstances = new List<LocationData>();
        }

        private Dictionary<Vector2Int, LLTerrainData> terrainExtraData =
            new Dictionary<Vector2Int, LLTerrainData>();

        LocationResourceManager resourceManager;

        public const int TERRAIN_SIZE = 128;
        public const int ROAD_WIDTH = 4; // Actually 2, but let's leave a bit of a gap
        public const float TERRAINPIXELSIZE = 819.2f;
        public const float TERRAIN_SIZE_MULTI = TERRAINPIXELSIZE / TERRAIN_SIZE;

        bool sceneLoading = false;
        private ulong lastLocationId = 0;

        public static int LootExpirationDays => 7;

        void Start()
        {
            Debug.Log("Begin mod init: Location Loader");

            LocationConsole.RegisterCommands();
            LocationRMBVariant.RegisterCommands();
            resourceManager = GetComponent<LocationResourceManager>();

            Debug.Log("Finished mod init: Location Loader");
        }

        private void OnEnable()
        {
            DaggerfallTerrain.OnPromoteTerrainData += OnTerrainPromoted;
            StreamingWorld.OnInitWorld += StreamingWorld_OnInitWorld;
            StreamingWorld.OnUpdateTerrainsEnd += StreamingWorld_OnUpdateTerrainsEnd;
            LocationData.OnLocationEnabled += LocationData_OnLocationEnabled;
        }

        private void OnDisable()
        {
            DaggerfallTerrain.OnPromoteTerrainData -= OnTerrainPromoted;
            StreamingWorld.OnInitWorld -= StreamingWorld_OnInitWorld;
            StreamingWorld.OnUpdateTerrainsEnd -= StreamingWorld_OnUpdateTerrainsEnd;
            LocationData.OnLocationEnabled -= LocationData_OnLocationEnabled;
        }

        void Update()
        {
            var game = GameManager.Instance;
            if (!game.StateManager.GameInProgress)
                return;

            CheckCurrentLocation();
        }

        void CheckCurrentLocation()
        {
            var game = GameManager.Instance;

            if (game.IsPlayerInside)
                return;

            // When near an instance, activate it
            var playerGps = game.PlayerGPS;
            var mapPixel = playerGps.CurrentMapPixel;
            LLTerrainData terrainData = GetTerrainExtraData(new Vector2Int(mapPixel.X, mapPixel.Y));

            if (terrainData.LocationInstances.Count == 0)
                return;

            // World coords are 256 values per terrain tile, or 32768 per map pixel (256*128)
            var playerTerrainX = (playerGps.WorldX % 32768) / 256;
            var playerTerrainY = (playerGps.WorldZ % 32768) / 256;

            const int extraRect = 1; // Add one terrain tile around each instance
            foreach (var instance in terrainData.LocationInstances)
            {
                var loc = instance.Location;
                var prefab = instance.Prefab;

                var instanceMinX = loc.terrainX - prefab.HalfWidth - extraRect;
                var instanceMinY = loc.terrainY - prefab.HalfHeight - extraRect;
                var instanceMaxX = loc.terrainX + prefab.HalfWidth + extraRect;
                var instanceMaxY = loc.terrainY + prefab.HalfHeight + extraRect;

                if (playerTerrainX >= instanceMinX
                    && playerTerrainX <= instanceMaxX
                    && playerTerrainY >= instanceMinY
                    && playerTerrainY <= instanceMaxY)
                {
                    if (loc.locationID != lastLocationId)
                    {
                        // Activate location

                        foreach (var lootSerializer in instance.LocationLoots)
                        {
                            if (!lootSerializer.Activated)
                            {
                                LocationHelper.GenerateLoot(lootSerializer.loot);
                                lootSerializer.Activated = true;
                            }
                        }

                        lastLocationId = loc.locationID;
                    }

                    return;
                }
            }
        }

        private void StreamingWorld_OnInitWorld()
        {
            sceneLoading = true;
        }

        private void StreamingWorld_OnUpdateTerrainsEnd()
        {
            if(sceneLoading)
            {
                StartCoroutine(InstantiateAllDynamicObjectsNextFrame());
            }
        }

        private void LocationData_OnLocationEnabled(object sender, EventArgs _)
        {
            if(!sceneLoading)
            {
                LocationData instance = sender as LocationData;
                // Ignore embedded instances (where location instance is null)
                if(instance != null && !instance.IsEmbeddedLocation && !instance.HasSpawnedDynamicObjects)
                {
                    InstantiateInstanceDynamicObjects(instance);
                }
            }
        }

        System.Collections.IEnumerator InstantiateAllDynamicObjectsNextFrame()
        {
            yield return new WaitForEndOfFrame();

            var instances = FindObjectsOfType<LocationData>();
            foreach (var instance in instances)
            {
                // Ignore embedded prefabs
                // Or instances which already have dynamic objects spawned
                if (instance.IsEmbeddedLocation || instance.HasSpawnedDynamicObjects)
                    continue;

                InstantiateInstanceDynamicObjects(instance);
            }

            sceneLoading = false;

            yield break;
        }

        public bool TryGetTerrain(int worldX, int worldY, out DaggerfallTerrain terrain)
        {
            var worldCoord = new Vector2Int(worldX, worldY);
            return TryGetTerrain(worldCoord, out terrain);
        }

        public bool TryGetTerrain(Vector2Int worldCoord, out DaggerfallTerrain terrain)
        {
            if (loadedTerrain.TryGetValue(worldCoord, out WeakReference<DaggerfallTerrain> terrainReference))
            {
                if (terrainReference.TryGetTarget(out terrain))
                {
                    // Terrain has been pooled and placed somewhere else
                    // Happens with Distant Terrain
                    if (terrain.MapPixelX != worldCoord.x || terrain.MapPixelY != worldCoord.y)
                    {
                        loadedTerrain.Remove(worldCoord);
                        return false;
                    }
                    return true;
                }
                else
                {
                    loadedTerrain.Remove(worldCoord);
                }
            }

            terrain = null;
            return false;
        }

        public LLTerrainData GetTerrainExtraData(Vector2Int worldCoord)
        {
            if (!terrainExtraData.TryGetValue(worldCoord, out LLTerrainData extraData))
            {
                extraData = CreateStaticExtraData(worldCoord);
                terrainExtraData.Add(worldCoord, extraData);
            }

            return extraData;
        }

        void InstantiateInstanceDynamicObjects(LocationData locationData)
        {
            if(!locationData)
            {
                Debug.LogError($"[LL] Failed to spawn dynamic objects: location data was null");
                return;
            }

            if(locationData.IsEmbeddedLocation)
            {
                Debug.LogError($"[LL] Failed to spawn dynamic objects: location was embedded");
                return;
            }

            LocationPrefab locationPrefab = locationData.Prefab;
            if (locationPrefab == null)
            {
                Debug.LogError($"[LL] Failed to spawn dynamic objects: prefab was null");
                return;
            }

            LocationInstance loc = locationData.Location;
            if (loc == null || string.IsNullOrEmpty(loc.prefab))
            {
                Debug.LogError($"[LL] Failed to spawn dynamic objects: instance was null or invalid");
                return;
            }

            GameObject instance = locationData.gameObject;
            if(!instance)
            {
                Debug.LogError($"[LL] Failed to spawn dynamic objects at ({loc.worldX}, {loc.worldY}): GameObject was null");
                return;
            }

            if(!LocationModLoader.modObject)
            {
                Debug.LogError($"[LL] Failed to spawn dynamic objects at ({loc.worldX}, {loc.worldY}): mod object was null");
                return;
            }

            var saveInterface = LocationModLoader.modObject.GetComponent<LocationSaveDataInterface>();
            if (!saveInterface)
            {
                Debug.LogError($"[LL] Failed to spawn dynamic objects at ({loc.worldX}, {loc.worldY}): save interface was null");
                return;
            }

            foreach (LocationObject obj in locationPrefab.obj)
            {
                if (obj == null)
                {
                    Debug.LogError($"[LL] Failed to spawn dynamic object at ({loc.worldX}, {loc.worldY}) on prefab '{loc.prefab}': obj was null");
                    continue;
                }

                if(string.IsNullOrEmpty(obj.name))
                {
                    Debug.LogError($"[LL] Failed to spawn dynamic object at ({loc.worldX}, {loc.worldY}) on prefab '{loc.prefab}': obj had null name");
                    continue;
                }

                GameObject go = null;

                if (obj.type == LocationObject.TypeBillboard)
                {
                    go = LocationHelper.LoadStaticObject(
                        obj.type,
                        obj.name,
                        instance.transform,
                        obj.pos,
                        obj.rot,
                        obj.scale
                        );

                    Vector3 offset = Vector3.zero;

                    var billboard = go.GetComponent<Billboard>();
                    if (billboard)
                    {
                        billboard.AlignToBase();
                        offset.y = (billboard.Summary.Size.y / 2);
                    }

                    go.transform.localPosition = obj.pos + offset;
                }
                else if (obj.type == LocationObject.TypeEditorMarker)
                {
                    if (InstantiateEditorMarker(locationData, obj, loc, saveInterface, instance, ref go)) continue;
                }
                else if (obj.type == LocationObject.TypeRMB)
                {
                    //if (blocksFile.GetBlockIndex(obj.name) == -1)
                        //WorldDataReplacement.AssignNextIndex(obj.name); Commented out for until PR accepted

                    // Step 1: Get the player's current climate index
                    int currentClimateIndex = GameManager.Instance.PlayerGPS.CurrentClimateIndex;

                    // Step 2: Use MapsFile to get the climate settings based on the current climate index
                    var climateSettings = MapsFile.GetWorldClimateSettings(currentClimateIndex);

                    // Step 3: Convert DFLocation.ClimateBaseType to ClimateBases using ClimateSwaps
                    ClimateBases climateBase = ClimateSwaps.FromAPIClimateBase(climateSettings.ClimateType);

                    // Step 4: Convert DFLocation.ClimateTextureSet to ClimateNatureSets using ClimateSwaps
                    ClimateNatureSets climateNature = ClimateSwaps.FromAPITextureSet(climateSettings.NatureSet);

                    // Step 5: Determine the season (set to Winter if it’s currently Winter; otherwise, use Summer)
                    ClimateSeason climateSeason = (DaggerfallUnity.Instance.WorldTime.Now.SeasonValue == DaggerfallDateTime.Seasons.Winter)
                        ? ClimateSeason.Winter
                        : ClimateSeason.Summer;

                    // Create the RMB block game object
                    DFBlock blockData;
                    GameObject rmbBlock = RMBLayout.CreateBaseGameObject(obj.name, layoutX: 0, layoutY: 0, out blockData);

                    // Add the ground plane with the determined climate base and season
                    //if (obj.groundPlane == true) RMBLayout.AddGroundPlane(ref blockData, rmbBlock.transform, climateBase, climateSeason);

                    // Add nature flats with the determined nature set and season
                    RMBLayout.AddNatureFlats(ref blockData, rmbBlock.transform, null, climateNature, climateSeason);

                    // Add Lights with billboard batching or custom lighting
                    RMBLayout.AddLights(ref blockData, rmbBlock.transform, rmbBlock.transform, null);

                    // Add other block flats, like animals and NPCs
                    RMBLayout.AddMiscBlockFlats(ref blockData, rmbBlock.transform, mapId: 0, locationIndex: 0);

                    // Add Exterior Block Flats
                    RMBLayout.AddExteriorBlockFlats(ref blockData, rmbBlock.transform, rmbBlock.transform, mapId: 0, locationIndex: 0, climateNature, climateSeason);

                    // Place and set up the final RMB block in the scene
                    rmbBlock.transform.parent = instance.transform;
                    rmbBlock.transform.localPosition = obj.pos;
                    rmbBlock.transform.localRotation = obj.rot;
                    rmbBlock.transform.localScale = obj.scale;
                }
            }

            locationData.HasSpawnedDynamicObjects = true;
        }

        private static bool InstantiateEditorMarker(LocationData locationData, LocationObject obj, LocationInstance loc,
            LocationSaveDataInterface saveInterface, GameObject instance, ref GameObject go)
        {
            string[] arg = obj.name.Split('.');

            if (arg.Length != 2)
            {
                Debug.LogError($"[LL] Invalid type 2 obj name '{obj.name}' in prefab '{loc.prefab}'");
                return true;
            }

            if (arg[0] == "199")
            {
                switch (arg[1])
                {
                    case "16":
                        object result = SaveLoadManager.Deserialize(typeof(EnemyMarkerExtraData), obj.extraData);
                        if(result == null)
                        {
                            Debug.LogError($"[LL] Could not spawn enemy in prefab '{loc.prefab}': invalid extra data");
                            return true;
                        }

                        var extraData = (EnemyMarkerExtraData)result;
                        if (!Enum.IsDefined(typeof(MobileTypes), extraData.EnemyId) && DaggerfallEntity.GetCustomCareerTemplate(extraData.EnemyId) == null)
                        {
                            Debug.LogError($"[LL] Could not spawn enemy in prefab '{loc.prefab}', unknown mobile type '{extraData.EnemyId}'");
                            return true;
                        }

                        ulong v = (uint)obj.objectID;
                        ulong loadId = LocationSaveDataInterface.ToObjectLoadId(loc.locationID, obj.objectID);

                        // Enemy is dead, don't spawn anything

                        if (saveInterface.IsEnemyDead(loadId))
                        {
                            break;
                        }

                        MobileTypes mobileType = (MobileTypes)extraData.EnemyId;

                        go = GameObjectHelper.CreateEnemy(TextManager.Instance.GetLocalizedEnemyName((int)mobileType), mobileType, obj.pos, MobileGender.Unspecified, instance.transform);
                        if(!go)
                        {
                            Debug.LogError($"[LL] Could not spawn enemy in prefab '{loc.prefab}': GameObject.CreateEnemy returned null");
                            return true;
                        }

                        SerializableEnemy serializable = go.GetComponent<SerializableEnemy>();
                        if (serializable)
                        {
                            Destroy(serializable);
                        }

                        DaggerfallEntityBehaviour behaviour = go.GetComponent<DaggerfallEntityBehaviour>();
                        if(!behaviour)
                        {
                            Debug.LogError($"[LL] Failed to spawn enemy at ({loc.worldX}, {loc.worldY}) on prefab '{loc.prefab}': behaviour was null");
                            return true;
                        }

                        EnemyEntity entity = (EnemyEntity)behaviour.Entity;
                        if (entity == null)
                        {
                            Debug.LogError($"[LL] Failed to spawn enemy at ({loc.worldX}, {loc.worldY}) on prefab '{loc.prefab}': entity was null");
                            return true;
                        }

                        if (entity.MobileEnemy.Gender == MobileGender.Male)
                        {
                            entity.Gender = Genders.Male;
                        }
                        else if (entity.MobileEnemy.Gender == MobileGender.Female)
                        {
                            entity.Gender = Genders.Female;
                        }

                        if (extraData.TeamOverride != 0 && Enum.IsDefined(typeof(MobileTeams), extraData.TeamOverride))
                        {
                            entity.Team = (MobileTeams)extraData.TeamOverride;
                        }

                        DaggerfallEnemy enemy = go.GetComponent<DaggerfallEnemy>();
                        if (!enemy)
                        {
                            Debug.LogError($"[LL] Failed to spawn enemy at ({loc.worldX}, {loc.worldY}) on prefab '{loc.prefab}': no enemy component");
                            return true;
                        }

                        enemy.LoadID = loadId;
                        var serializer = go.AddComponent<LocationEnemySerializer>();

                        locationData.AddEnemy(serializer);

                        break;

                    case "19":
                    {
                        int iconIndex = UnityEngine.Random.Range(0, DaggerfallLootDataTables.randomTreasureIconIndices.Length);
                        int iconRecord = DaggerfallLootDataTables.randomTreasureIconIndices[iconIndex];
                        go = LocationHelper.CreateLootContainer(loc.locationID, obj.objectID, 216, iconRecord, instance.transform);
                        if (!go)
                        {
                            Debug.LogError($"[LL] Could not spawn treasure in prefab '{loc.prefab}': LocationHelper.CreateLootContainer returned null");
                            return true;
                        }

                        Vector3 offset = Vector3.zero;

                        var billboard = go.GetComponent<Billboard>();
                        if (billboard)
                        {
                            billboard.AlignToBase();
                            offset.y = (billboard.Summary.Size.y / 2);
                        }

                        go.transform.localPosition = obj.pos + offset;

                        locationData.AddLoot(go.GetComponent<LocationLootSerializer>());

                        break;
                    }
                }
            }

            return false;
        }

        Vector3 GetLocationPosition(LocationData locationData, DaggerfallTerrain daggerTerrain)
        {
            var loc = locationData.Location;

            if (loc.type == 2 || loc.type == 3)
            {
                return new Vector3(loc.terrainX * TERRAIN_SIZE_MULTI, DaggerfallUnity.Instance.TerrainSampler.OceanElevation * daggerTerrain.TerrainScale, loc.terrainY * TERRAIN_SIZE_MULTI);
            }
            else
            {
                float terrainHeightMax = DaggerfallUnity.Instance.TerrainSampler.MaxTerrainHeight * daggerTerrain.TerrainScale;
                float sinkOffset = Mathf.Lerp(0, locationData.HeightOffset, loc.sink);
                return new Vector3(loc.terrainX * TERRAIN_SIZE_MULTI, locationData.OverlapAverageHeight * terrainHeightMax + sinkOffset, loc.terrainY * TERRAIN_SIZE_MULTI);
            }
        }

        void SetActiveRecursively(GameObject go)
        {
            go.SetActive(true);
            foreach(Transform child in go.transform)
            {
                if (child.TryGetComponent(out LocationData _))
                {
                    child.gameObject.SetActive(true);
                    SetActiveRecursively(child.gameObject);
                }
            }
        }

        LocationData InstantiateTopLocationPrefab(string prefabName, float overlapAverageHeight, LocationPrefab locationPrefab, LocationInstance loc, DaggerfallTerrain daggerTerrain)
        {
            GameObject instance = resourceManager.InstantiateLocationPrefab(prefabName, locationPrefab, daggerTerrain.transform);

            LocationData data = instance.AddComponent<LocationData>();
            data.Location = loc;
            data.Prefab = locationPrefab;
            data.OverlapAverageHeight = overlapAverageHeight;

            if (loc.type == 1 && loc.sink > 0.0f)
            {
                FindAdjustedHeightOffset(data);
            }

            Vector3 terrainOffset = GetLocationPosition(data, daggerTerrain);
            instance.transform.localPosition = terrainOffset;
            instance.transform.localRotation = loc.rot;
            instance.transform.localScale = new Vector3(loc.scale, loc.scale, loc.scale);

            // FOOTPRINT-BASED WATER PRUNE for type-0 prefabs
            if (loc.type == 0)
            {
                // 1) compute normalized water threshold
                float maxWorldPerUnit = DaggerfallUnity.Instance.TerrainSampler.MaxTerrainHeight * daggerTerrain.TerrainScale;
                float waterNormThreshold = 101f / maxWorldPerUnit;

                // 2) figure out your footprint in heightmap coordinates
                int halfW = locationPrefab.HalfWidth;
                int halfH = locationPrefab.HalfHeight;
                int minX = Mathf.Clamp(loc.terrainX - halfW, 0, TERRAIN_SIZE - 1);
                int minY = Mathf.Clamp(loc.terrainY - halfH, 0, TERRAIN_SIZE - 1);
                int maxX = Mathf.Clamp(loc.terrainX + halfW, 0, TERRAIN_SIZE - 1);
                int maxY = Mathf.Clamp(loc.terrainY + halfH, 0, TERRAIN_SIZE - 1);

                // 3) scan for any water‐level samples
                bool overlapsWater = false;
                for (int yy = minY; yy <= maxY && !overlapsWater; yy++)
                {
                    for (int xx = minX; xx <= maxX; xx++)
                    {
                        if (daggerTerrain.MapData.heightmapSamples[yy, xx] < waterNormThreshold)
                        {
                            overlapsWater = true;
                            break;
                        }
                    }
                }

                // 4) if any tile is below water level, destroy the prefab
                if (overlapsWater)
                {
                    Debug.LogWarning($"[LL] Skipping '{prefabName}' at ({loc.worldX},{loc.worldY}): footprint overlaps water");
                    Destroy(instance);
                    return null;
                }
            }

            // Now that we have the LocationData, add it to "pending instances" if needed
            if (instancePendingTerrains.TryGetValue(loc.locationID, out List<Vector2Int> pendingTerrains))
            {
                foreach (Vector2Int terrainCoord in pendingTerrains)
                {
                    if (!pendingIncompleteLocations.TryGetValue(terrainCoord, out List<LocationData> terrainPendingLocations))
                    {
                        terrainPendingLocations = new List<LocationData>();
                        pendingIncompleteLocations.Add(terrainCoord, terrainPendingLocations);
                    }

                    terrainPendingLocations.Add(data);
                }
            }

            SetActiveRecursively(instance);

            // The "LocationData.OnEnabled" callback might or might not spawn dynamic objects before this point
            if (!sceneLoading && !data.HasSpawnedDynamicObjects)
            {
                InstantiateInstanceDynamicObjects(data);
            }

            var reader = DaggerfallUnity.Instance.ContentReader.MapFileReader;
            int climateIndex = reader.GetClimateIndex(loc.worldX, loc.worldY);
            var climateSettings = MapsFile.GetWorldClimateSettings(climateIndex);

            // Convert to DaggerfallLocation enums
            var climateBase   = ClimateSwaps.FromAPIClimateBase(climateSettings.ClimateType);
            var climateNature = ClimateSwaps.FromAPITextureSet  (climateSettings.NatureSet);
            var climateSeason = (DaggerfallUnity.Instance.WorldTime.Now.SeasonValue == DaggerfallDateTime.Seasons.Winter)
                ? ClimateSeason.Winter
                : ClimateSeason.Summer;

            // Add (or fetch) a DaggerfallLocation on the root prefab
            var locationComponent = instance.GetComponent<DaggerfallLocation>()
                                    ?? instance.AddComponent<DaggerfallLocation>();

            // Tell it you’re using a custom climate and set your values
            locationComponent.ClimateUse      = LocationClimateUse.Custom;
            locationComponent.CurrentClimate  = climateBase;
            locationComponent.CurrentNatureSet = climateNature;
            locationComponent.CurrentSeason   = climateSeason;

            // Actually push those settings into the material/shader system
            locationComponent.ApplyClimateSettings();

            return data;
        }

        bool IsInSnowFreeClimate(DaggerfallTerrain daggerTerrain)
        {
            int climateIndex = DaggerfallUnity.Instance.ContentReader.MapFileReader.GetClimateIndex(daggerTerrain.MapPixelX, daggerTerrain.MapPixelY);
            return WeatherManager.IsSnowFreeClimate(climateIndex);
        }

        void OnTerrainPromoted(DaggerfallTerrain daggerTerrain, TerrainData terrainData)
        {
            Vector2Int worldLocation = new Vector2Int(daggerTerrain.MapPixelX, daggerTerrain.MapPixelY);
            loadedTerrain[worldLocation] = new WeakReference<DaggerfallTerrain>(daggerTerrain);

            List<LocationData> terrainLocations = new List<LocationData>();

            // Terrain can be reused in terrain mods (ex: Distant Terrain)
            // Delete existing locations left on reused terrain
            foreach(var existingLoot in daggerTerrain.GetComponentsInChildren<LocationLootSerializer>())
            {
                existingLoot.InvalidateSave();
            }

            foreach (var existingEnemy in daggerTerrain.GetComponentsInChildren<LocationEnemySerializer>())
            {
                existingEnemy.InvalidateSave();
            }

            foreach (var existingLocation in daggerTerrain.GetComponentsInChildren<LocationData>())
            {
                Destroy(existingLocation.gameObject);
            }

            // Spawn the terrain's instances
            foreach (LocationInstance loc in resourceManager.GetTerrainInstances(daggerTerrain.MapData.mapPixelX, daggerTerrain.MapData.mapPixelY))
            {
                string context = $"location=\"{loc.name}\"";

                LocationPrefab locationPrefab = resourceManager.GetPrefabInfo(loc.prefab);
                if (locationPrefab == null)
                    continue;

                if (DaggerfallUnity.Instance.WorldTime.Now.SeasonValue == DaggerfallDateTime.Seasons.Winter
                    && !IsInSnowFreeClimate(daggerTerrain)
                    && !string.IsNullOrEmpty(locationPrefab.winterPrefab))
                {
                    var winterPrefab = resourceManager.GetPrefabInfo(locationPrefab.winterPrefab);
                    if (winterPrefab == null)
                        Debug.LogError($"Winter prefab '{locationPrefab.winterPrefab}' could not be loaded");
                    else
                        locationPrefab = winterPrefab;
                }

                if (LocationHelper.IsOutOfBounds(loc, locationPrefab))
                {
                    Debug.LogWarning($"Out-of-bounds location at ({daggerTerrain.MapPixelX}, {daggerTerrain.MapPixelY}) ({context})");
                    continue;
                }

                if (loc.type == 2)
                {
                    // We find and adjust the type 2 instance position here
                    // So that terrain can be flattened in consequence
                    // If the current tile has no coast and adjacent terrain are not loaded,
                    // then we don't care about flattening, since it means the instance
                    // is gonna be on water at the edge of this tile anyway
                    if (FindNearestCoast(loc, daggerTerrain, out Vector2Int coastTileCoord))
                    {
                        loc.terrainX = coastTileCoord.x;
                        loc.terrainY = coastTileCoord.y;
                    }
                    else
                    {
                        // Prune the prefab if no coast is found
                        return; // Exit early to skip further processing for this instance
                    }
                }

                if (PruneInstance(loc, locationPrefab))
                {
                    continue;
                }

                int count = 0;
                float averageHeight = 0;

                var (halfWidth, halfHeight) = LocationHelper.GetHalfDimensions(loc, locationPrefab);

                int minX = Math.Max(loc.terrainX - halfWidth, 0);
                int minY = Math.Max(loc.terrainY - halfHeight, 0);
                int maxX = Math.Min(loc.terrainX + halfWidth, 128);
                int maxY = Math.Min(loc.terrainY + halfHeight, 128);
                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        averageHeight += daggerTerrain.MapData.heightmapSamples[y, x];
                        count++;
                    }
                }

                averageHeight /= count;

                var instantiatedLocation = InstantiateTopLocationPrefab(loc.prefab, averageHeight, locationPrefab, loc, daggerTerrain);
                if (instantiatedLocation)
                {
                    terrainLocations.Add(instantiatedLocation);
                }
            }

            // Check for pending instances waiting on this terrain
            if(pendingIncompleteLocations.TryGetValue(worldLocation, out List<LocationData> pendingLocations))
            {
                for(int i = 0; i < pendingLocations.Count; ++i)
                {
                    LocationData pendingLoc = pendingLocations[i];

                    if(pendingLoc == null)
                        // We got no info left on this instance
                        continue;

                    if(pendingLoc.IsEmbeddedLocation)
                    {
                        Debug.LogError($"[LL] Embedded location in pending incomplete locations at ({worldLocation.x}, {worldLocation.y})");
                        continue;
                    }

                        // Invalid locations?
                    if (!instancePendingTerrains.TryGetValue(pendingLoc.Location.locationID, out List<Vector2Int> pendingTerrains))
                        continue;

                    // Removes the instance from all "pending terrains"
                    void ClearPendingInstance()
                    {
                        foreach(Vector2Int pendingTerrainCoord in pendingTerrains)
                        {
                            if (pendingTerrainCoord == worldLocation)
                                continue;

                            if(pendingIncompleteLocations.TryGetValue(pendingTerrainCoord, out List<LocationData> pendingTerrainPendingInstances))
                            {
                                pendingTerrainPendingInstances.Remove(pendingLoc);
                                if (pendingTerrainPendingInstances.Count == 0)
                                    pendingIncompleteLocations.Remove(pendingTerrainCoord);
                            }
                        }

                        instancePendingTerrains.Remove(pendingLoc.Location.locationID);
                    }

                    if(!TryGetTerrain(pendingLoc.Location.worldX, pendingLoc.Location.worldY, out DaggerfallTerrain pendingLocTerrain))
                    {
                        // Terrain the location was on has expired
                        ClearPendingInstance();
                        continue;
                    }

                    // Type 2 location try to see if they found a coast to snap to
                    if (pendingLoc.Location.type == 2)
                    {
                        if (FindNearestCoast(pendingLoc.Location, pendingLocTerrain, out Vector2Int coastCoord))
                        {
                            pendingLoc.Location.terrainX = coastCoord.x;
                            pendingLoc.Location.terrainY = coastCoord.y;

                            pendingLoc.gameObject.transform.localPosition = GetLocationPosition(pendingLoc, pendingLocTerrain);

                            // Instance is not pending anymore
                            ClearPendingInstance();
                            continue;
                        }
                    }
                    // Adjust type 1 location height sink
                    else if(pendingLoc.Location.type == 1)
                    {
                        if (FindAdjustedHeightOffset(pendingLoc))
                        {
                            pendingLoc.gameObject.transform.localPosition = GetLocationPosition(pendingLoc, pendingLocTerrain);
                            // Instance is not pending anymore
                            ClearPendingInstance();
                            continue;
                        }
                        else
                        {
                            pendingLoc.gameObject.transform.localPosition = GetLocationPosition(pendingLoc, pendingLocTerrain);
                        }
                    }

                    // Remove this terrain from the location's pending terrains
                    pendingTerrains.Remove(worldLocation);
                }

                pendingIncompleteLocations.Remove(worldLocation);
            }

            if (BlendTerrain(daggerTerrain, terrainLocations))
            {
                terrainData.SetHeights(0, 0,
                    daggerTerrain.MapData.heightmapSamples); // Reset terrain data after heightmap samples change
            }

            // Handle the dynamic part of terrain extra data
            LLTerrainData extraData = GetTerrainExtraData(worldLocation);
            foreach(var instantiatedLocation in terrainLocations)
            {
                extraData.LocationInstances.Add(instantiatedLocation);
            }
        }

        LLTerrainData CreateStaticExtraData(Vector2Int worldCoord)
        {
            LLTerrainData extraData = new LLTerrainData();

            foreach (LocationInstance loc in resourceManager.GetTerrainInstances(worldCoord.x, worldCoord.y))
            {
                LocationPrefab locationPrefab = resourceManager.GetPrefabInfo(loc.prefab);
                if (locationPrefab == null)
                    continue;

                if (loc.type == 0 || loc.type == 2 )
                {
                    extraData.LocationInstanceRects.Add(new Rect(loc.terrainX - locationPrefab.HalfWidth
                        , loc.terrainY - locationPrefab.HalfHeight
                        , locationPrefab.TerrainWidth
                        , locationPrefab.TerrainHeight));
                }
            }

            return extraData;
        }

        struct LocationRectData
        {
            public Rect rect;
            public float averageHeight;
        }

        bool BlendTerrain(DaggerfallTerrain daggerTerrain, List<LocationData> terrainLocations)
        {
            const float transitionWidth = 10.0f;
            // Convert cutoff to normalized heightmap units
            float maxWorldPerUnit = DaggerfallUnity.Instance.TerrainSampler.MaxTerrainHeight * daggerTerrain.TerrainScale;
            float waterNormThreshold = 101f / maxWorldPerUnit;

            // 1) Gather all the rectangles where we need to flatten/blend
            List<LocationRectData> locationRects = new List<LocationRectData>();
            foreach (LocationData loc in terrainLocations)
            {
                if (loc.Location.type == 0)
                {
                    locationRects.Add(new LocationRectData
                    {
                        rect = new Rect(
                            loc.Location.terrainX - loc.Prefab.HalfWidth,
                            loc.Location.terrainY - loc.Prefab.HalfHeight,
                            loc.Prefab.TerrainWidth,
                            loc.Prefab.TerrainHeight
                        ),
                        averageHeight = loc.OverlapAverageHeight
                    });
                }
            }

            // 2) Add the built-in DFLocation rect if present
            var dfRect = daggerTerrain.MapData.locationRect;
            if (dfRect.x > 0 && dfRect.y > 0)
            {
                float avg = 0f;
                int cnt = 0;
                int minX = Mathf.FloorToInt(dfRect.xMin);
                int minY = Mathf.FloorToInt(dfRect.yMin);
                int maxX = Mathf.CeilToInt (dfRect.xMax);
                int maxY = Mathf.CeilToInt (dfRect.yMax);
                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                    {
                        avg += daggerTerrain.MapData.heightmapSamples[y, x];
                        cnt++;
                    }
                avg /= cnt;

                locationRects.Add(new LocationRectData
                {
                    rect = dfRect,
                    averageHeight = avg
                });
            }

            if (locationRects.Count == 0)
                return false;

            // 3) Loop over every internal sample and blend if needed—but skip water
            for (int y = 1; y < TERRAIN_SIZE - 1; y++)
            {
                for (int x = 1; x < TERRAIN_SIZE - 1; x++)
                {
                    // Check water cutoff first
                    float origNorm = daggerTerrain.MapData.heightmapSamples[y, x];
                    if (origNorm < waterNormThreshold)
                        continue;

                    Vector2 point = new Vector2(x, y);

                    // Find nearest rect and its flat height
                    float bestDist = float.MaxValue;
                    float targetNorm = origNorm;
                    foreach (var rr in locationRects)
                    {
                        float d = GetDistanceFromRect(rr.rect, point);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            targetNorm = rr.averageHeight;
                            if (d == 0f) break;
                        }
                    }

                    // Only blend within transitionWidth
                    if (bestDist < transitionWidth)
                    {
                        float t = bestDist / transitionWidth;                         // 0 at edge, 1 at band edge
                        float smooth = Mathf.SmoothStep(0f, 1f, t);                   // ease in/out
                        float blended = Mathf.Lerp(targetNorm, origNorm, smooth);
                        daggerTerrain.MapData.heightmapSamples[y, x] = blended;
                    }
                }
            }

            return true;
        }

        bool PruneInstance(LocationInstance loc, LocationPrefab prefab)
        {
            foreach(var terrainSection in LocationHelper.GetOverlappingTerrainSections(loc, prefab))
            {
                Vector2Int worldCoord = terrainSection.WorldCoord;

                if(DaggerfallUnity.Instance.ContentReader.HasLocation(worldCoord.x, worldCoord.y, out MapSummary summary))
                {
                    // Check World Data locations only
                    if(WorldDataReplacement.GetDFLocationReplacementData(summary.RegionIndex, summary.MapIndex, out DFLocation wdLoc))
                    {
                        int locationWidth = wdLoc.Exterior.ExteriorData.Width;
                        int locationHeight = wdLoc.Exterior.ExteriorData.Height;
                        int locationX = (RMBLayout.RMBTilesPerTerrain - locationWidth * RMBLayout.RMBTilesPerBlock) / 2;
                        int locationY = (RMBLayout.RMBTilesPerTerrain - locationHeight * RMBLayout.RMBTilesPerBlock) / 2;
                        RectInt locationArea = new RectInt(locationX, locationY, locationWidth * RMBLayout.RMBTilesPerBlock, locationHeight * RMBLayout.RMBTilesPerBlock);

                        // Instance is on a World Data location. Prune it
                        if (locationArea.Overlaps(terrainSection.Section))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private float GetDistanceFromRect(Rect rect, Vector2 point)
        {
            float squared_dist = 0.0f;

            if (point.x > rect.xMax)
                squared_dist += (point.x - rect.xMax) * (point.x - rect.xMax);
            else if (point.x < rect.xMin)
                squared_dist += (rect.xMin - point.x) * (rect.xMin - point.x);

            if (point.y > rect.yMax)
                squared_dist += (point.y - rect.yMax) * (point.y - rect.yMax);
            else if (point.y < rect.yMin)
                squared_dist += (rect.yMin - point.y) * (rect.yMin - point.y);

            if (squared_dist == 0.0f)
                return 0.0f;

            return Mathf.Sqrt(squared_dist);
        }

        // Takes a type 2 Location instance and searches for the nearest coast it can snap to
        // Returns true if an answer was found
        // If false, and any surrounding terrains weren't loaded, it will be added to the list of instances waiting on terrain
        bool FindNearestCoast(LocationInstance loc, DaggerfallTerrain daggerTerrain, out Vector2Int tileCoord)
        {
            byte GetTerrainSample(DaggerfallTerrain terrain, int x, int y)
            {
                return terrain.MapData.tilemapSamples[x, y];
            }

            byte GetSample(int x, int y)
            {
                return GetTerrainSample(daggerTerrain, x, y);
            }

            byte initial = GetSample(loc.terrainX, loc.terrainY);

            // If we start in water
            if(initial == 0)
            {
                // Find non-water in any direction
                for(int i = 1;;++i)
                {
                    bool anyValid = false;

                    // North
                    if(loc.terrainY + i < 128)
                    {
                        if(GetSample(loc.terrainX, loc.terrainY + i) != 0)
                        {
                            tileCoord = new Vector2Int(loc.terrainX, loc.terrainY + i - 1);
                            return true;
                        }
                        anyValid = true;
                    }

                    // East
                    if (loc.terrainX + i < 128)
                    {
                        if (GetSample(loc.terrainX + i, loc.terrainY) != 0)
                        {
                            tileCoord = new Vector2Int(loc.terrainX + i - 1, loc.terrainY);
                            return true;
                        }
                        anyValid = true;
                    }

                    // South
                    if (loc.terrainY - i >= 0)
                    {
                        if (GetSample(loc.terrainX, loc.terrainY - i) != 0)
                        {
                            tileCoord = new Vector2Int(loc.terrainX, loc.terrainY - i + 1);
                            return true;
                        }
                        anyValid = true;
                    }

                    // West
                    if (loc.terrainX - i >= 0)
                    {
                        if (GetSample(loc.terrainX - i, loc.terrainY) != 0)
                        {
                            tileCoord = new Vector2Int(loc.terrainX - i + 1, loc.terrainY);
                            return true;
                        }
                        anyValid = true;
                    }

                    if (!anyValid)
                        break;
                }

                // Look the edges of adjacent terrain
                if (GetNorthNeighbor(daggerTerrain, out DaggerfallTerrain northNeighbor))
                {
                    if(GetTerrainSample(northNeighbor, loc.terrainX, 0) != 0)
                    {
                        tileCoord = new Vector2Int(loc.terrainX, 127);
                        return true;
                    }
                }

                if (GetEastNeighbor(daggerTerrain, out DaggerfallTerrain eastNeighbor))
                {
                    if (GetTerrainSample(eastNeighbor, 0, loc.terrainY) != 0)
                    {
                        tileCoord = new Vector2Int(127, loc.terrainY);
                        return true;
                    }
                }

                if (GetSouthNeighbor(daggerTerrain, out DaggerfallTerrain southNeighbor))
                {
                    if (GetTerrainSample(southNeighbor, loc.terrainX, 127) != 0)
                    {
                        tileCoord = new Vector2Int(loc.terrainX, 0);
                        return true;
                    }
                }

                if (GetWestNeighbor(daggerTerrain, out DaggerfallTerrain westNeighbor))
                {
                    if (GetTerrainSample(westNeighbor, 127, loc.terrainY) != 0)
                    {
                        tileCoord = new Vector2Int(0, loc.terrainY);
                        return true;
                    }
                }

                List<Vector2Int> pendingTerrain = new List<Vector2Int>();
                if(northNeighbor == null && loc.worldY != 0)
                {
                    pendingTerrain.Add(new Vector2Int(loc.worldX, loc.worldY - 1));
                }

                if (eastNeighbor == null && loc.worldX != 1000)
                {
                    pendingTerrain.Add(new Vector2Int(loc.worldX + 1, loc.worldY));
                }

                if (southNeighbor == null && loc.worldY != 500)
                {
                    pendingTerrain.Add(new Vector2Int(loc.worldX, loc.worldY + 1));
                }

                if (westNeighbor == null && loc.worldX != 0)
                {
                    pendingTerrain.Add(new Vector2Int(loc.worldX - 1, loc.worldY));
                }

                if (pendingTerrain.Count != 0)
                {
                    instancePendingTerrains[loc.locationID] = pendingTerrain;
                }
            }
            else
            {
                // Find water in any direction
                for (int i = 1; ; ++i)
                {
                    bool anyValid = false;

                    // North
                    if (loc.terrainY + i < 128)
                    {
                        if (GetSample(loc.terrainX, loc.terrainY + i) == 0)
                        {
                            tileCoord = new Vector2Int(loc.terrainX, loc.terrainY + i);
                            return true;
                        }
                        anyValid = true;
                    }

                    // East
                    if (loc.terrainX + i < 128)
                    {
                        if (GetSample(loc.terrainX + i, loc.terrainY) == 0)
                        {
                            tileCoord = new Vector2Int(loc.terrainX + i, loc.terrainY);
                            return true;
                        }
                        anyValid = true;
                    }

                    // South
                    if (loc.terrainY - i >= 0)
                    {
                        if (GetSample(loc.terrainX, loc.terrainY - i) == 0)
                        {
                            tileCoord = new Vector2Int(loc.terrainX, loc.terrainY - i);
                            return true;
                        }
                        anyValid = true;
                    }

                    // West
                    if (loc.terrainX - i >= 0)
                    {
                        if (GetSample(loc.terrainX - i, loc.terrainY) == 0)
                        {
                            tileCoord = new Vector2Int(loc.terrainX - i, loc.terrainY);
                            return true;
                        }
                        anyValid = true;
                    }

                    if (!anyValid)
                        break;
                }
            }

            tileCoord = new Vector2Int(loc.terrainX, loc.terrainY);
            return false;
        }

        bool GetNorthNeighbor(DaggerfallTerrain daggerTerrain, out DaggerfallTerrain northNeighbor)
        {
            if(daggerTerrain.MapPixelY == 0)
            {
                northNeighbor = null;
                return false;
            }

            return TryGetTerrain(daggerTerrain.MapPixelX, daggerTerrain.MapPixelY - 1, out northNeighbor);
        }

        bool GetEastNeighbor(DaggerfallTerrain daggerTerrain, out DaggerfallTerrain eastNeighbor)
        {
            if (daggerTerrain.MapPixelX == 1000)
            {
                eastNeighbor = null;
                return false;
            }

            return TryGetTerrain(daggerTerrain.MapPixelX + 1, daggerTerrain.MapPixelY, out eastNeighbor);
        }

        bool GetSouthNeighbor(DaggerfallTerrain daggerTerrain, out DaggerfallTerrain southNeighbor)
        {
            if (daggerTerrain.MapPixelY == 500)
            {
                southNeighbor = null;
                return false;
            }

            return TryGetTerrain(daggerTerrain.MapPixelX, daggerTerrain.MapPixelY + 1, out southNeighbor);
        }

        bool GetWestNeighbor(DaggerfallTerrain daggerTerrain, out DaggerfallTerrain westNeighbor)
        {
            if (daggerTerrain.MapPixelX == 0)
            {
                westNeighbor = null;
                return false;
            }

            return TryGetTerrain(daggerTerrain.MapPixelX - 1, daggerTerrain.MapPixelY, out westNeighbor);
        }

        // Returns true if the loc is done adjusting
        bool FindAdjustedHeightOffset(LocationData locationData)
        {
            var loc = locationData.Location;
            var locationPrefab = locationData.Prefab;

            if (!TryGetTerrain(loc.worldX, loc.worldY, out DaggerfallTerrain locBaseTerrain))
            {
                return true;
            }

            float baseHeightMax = DaggerfallUnity.Instance.TerrainSampler.MaxTerrainHeight * locBaseTerrain.TerrainScale;
            float baseHeightAverage = locationData.OverlapAverageHeight * baseHeightMax;

            List<Vector2Int> pendingTerrain = new List<Vector2Int>();

            foreach (LocationHelper.TerrainSection terrainSection in LocationHelper.GetOverlappingTerrainSections(loc, locationPrefab))
            {
                if(!TryGetTerrain(terrainSection.WorldCoord.x, terrainSection.WorldCoord.y, out DaggerfallTerrain sectionTerrain))
                {
                    pendingTerrain.Add(terrainSection.WorldCoord);
                    continue;
                }

                float terrainHeightMax = DaggerfallUnity.Instance.TerrainSampler.MaxTerrainHeight * sectionTerrain.TerrainScale;

                for (int i = terrainSection.Section.min.x; i <= terrainSection.Section.max.x; i++)
                {
                    for(int j = terrainSection.Section.min.y; j <= terrainSection.Section.max.y; j++)
                    {
                        float sampleHeight = sectionTerrain.MapData.heightmapSamples[j, i];
                        float unitHeight = sampleHeight * terrainHeightMax;

                        float currentHeight = baseHeightAverage + locationData.HeightOffset;

                        if (unitHeight < currentHeight)
                        {
                            locationData.HeightOffset = unitHeight - baseHeightAverage;
                        }
                    }
                }
            }

            if(pendingTerrain.Count > 0)
            {
                instancePendingTerrains[loc.locationID] = pendingTerrain;

                return false;
            }

            return true;
        }
    }
}
