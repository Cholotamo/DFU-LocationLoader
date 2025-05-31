// LocationSaveDataInterface.cs
// Combines the “loot” and “enemy” serializers plus the save‐data interface (v2).

using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Utility.AssetInjection;
using DaggerfallWorkshop.Utility;
using FullSerializer;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.Questing;

namespace LocationLoader
{
    // ------------------------------------------------------------------------------------
    // (1) The original loot‐data struct (v1).  Nothing changed here.
    // ------------------------------------------------------------------------------------
    [fsObject("v1")]
    public class LocationLootData_v1
    {
        public ulong              loadID;
        public LootContainerTypes containerType;
        public InventoryContainerImages containerImage;
        public Vector3            currentPosition;
        public Vector3            localPosition;
        public Vector3            worldCompensation;
        public float              heightScale;
        public int                textureArchive;
        public int                textureRecord;
        public string             lootTableKey;
        public string             entityName;
        public int                stockedDate;
        public bool               playerOwned;
        public bool               customDrop;
        public bool               isEnemyClass;
        public ItemData_v1[]      items;
    }

    // ------------------------------------------------------------------------------------
    // (2) The original per‐location save struct (v1).  Required by existing code.
    // ------------------------------------------------------------------------------------
    [fsObject("v1")]
    public struct LocationSaveData_v1
    {
        public ulong[]              clearedEnemies;
        public EnemyData_v1[]       activeEnemies;
        public LocationLootData_v1[] lootContainers;
    }

    // ------------------------------------------------------------------------------------
    // (3) LocationLootSerializer now exposes “public bool Activated { get; set; }” and
    //     makes its “DaggerfallLoot loot” field public.  Other scripts expect that.
    // ------------------------------------------------------------------------------------
    public class LocationLootSerializer : MonoBehaviour, ISerializableGameObject
    {
        #region Fields
        public DaggerfallLoot loot;                         // <— made public
        Vector3             worldCompensation;
        private LocationSaveDataInterface saveDataInterface;
        #endregion

        #region Properties
        // Exposed so LocationSaveDataInterface can check whether to serialize this loot
        public bool Activated { get; set; }
        #endregion

        #region Unity
        void Awake()
        {
            loot = GetComponent<DaggerfallLoot>();
            if (!loot)
                throw new Exception("DaggerfallLoot not found on LocationLootSerializer GameObject.");
            saveDataInterface = LocationModLoader.modObject.GetComponent<LocationSaveDataInterface>();
        }

        void OnEnable()
        {
            if (LoadID != 0)
            {
                RefreshWorldCompensation();
                saveDataInterface.RegisterActiveSerializer(this);
            }
        }

        void OnDisable()
        {
            if (LoadID != 0)
            {
                if (saveDataInterface != null)
                {
                    if (Activated)
                        saveDataInterface.SerializeLoot(this);
                    saveDataInterface.DeregisterActiveSerializer(this);
                }
            }
        }
        #endregion

        public bool TryLoadSavedData()
        {
            if (LoadID == 0)
                return false;
            return saveDataInterface.TryDeserializeLoot(this);
        }

        public void RefreshWorldCompensation()
        {
            worldCompensation = GameManager.Instance.StreamingWorld.WorldCompensation;
        }

        public void InvalidateSave()
        {
            if (LoadID != 0 && saveDataInterface != null)
            {
                saveDataInterface.DeregisterActiveSerializer(this);
                loot = null;
                Activated = false;
            }
        }

        #region ISerializableGameObject
        public ulong LoadID => (loot ? loot.LoadID : 0);
        public bool  ShouldSave => (loot != null && Activated);

        public object GetSaveData()
        {
            if (loot == null || !Activated)
                return null;

            var data = new LocationLootData_v1
            {
                loadID            = LoadID,
                containerType     = loot.ContainerType,
                containerImage    = loot.ContainerImage,
                currentPosition   = loot.transform.position,
                localPosition     = loot.transform.localPosition,
                worldCompensation = worldCompensation,
                heightScale       = loot.transform.localScale.y,
                textureArchive    = loot.TextureArchive,
                textureRecord     = loot.TextureRecord,
                stockedDate       = loot.stockedDate,
                playerOwned       = loot.playerOwned,
                customDrop        = loot.customDrop,
                items             = loot.Items.SerializeItems(),
                entityName        = loot.entityName,
                isEnemyClass      = loot.isEnemyClass
            };

            return data;
        }

        public void RestoreSaveData(object dataIn)
        {
            if (loot == null)
                return;
            var data = (LocationLootData_v1)dataIn;
            if (data.loadID != LoadID)
                return;

            // Compute Y‐offset due to world compensation difference
            float diffY = GameManager.Instance.StreamingWorld.WorldCompensation.y - data.worldCompensation.y;
            loot.transform.position = data.currentPosition + new Vector3(0, diffY, 0);

            // Attempt to swap in a custom model; if none, restore billboard
            var billboard = loot.GetComponent<DaggerfallBillboard>();
            if (MeshReplacement.SwapCustomFlatGameobject(
                    data.textureArchive, data.textureRecord,
                    loot.transform, Vector3.zero, inDungeon: false))
            {
                if (billboard) Destroy(billboard);
                Destroy(GetComponent<MeshRenderer>());
            }
            else
            {
                if (!billboard)
                    billboard = loot.gameObject.AddComponent<DaggerfallBillboard>();
                billboard.SetMaterial(data.textureArchive, data.textureRecord);

                if (data.heightScale == 0) data.heightScale = 1;
                if (data.heightScale != billboard.transform.localScale.y)
                {
                    float height = billboard.Summary.Size.y * (data.heightScale / billboard.transform.localScale.y);
                    billboard.transform.Translate(0, (billboard.Summary.Size.y - height) / 2f, 0);
                }
            }

            // Restore inventory
            loot.Items.DeserializeItems(data.items);

            // Restore other attributes
            loot.ContainerType   = data.containerType;
            loot.ContainerImage  = data.containerImage;
            loot.TextureArchive  = data.textureArchive;
            loot.TextureRecord   = data.textureRecord;
            loot.stockedDate     = data.stockedDate;
            loot.playerOwned     = data.playerOwned;
            loot.customDrop      = data.customDrop;
            loot.entityName      = data.entityName;
            loot.isEnemyClass    = data.isEnemyClass;

            // If it’s now empty, remove it
            if (loot.Items.Count == 0)
                GameObjectHelper.RemoveLootContainer(loot);

            Activated = true;
        }
        #endregion
    }

    // ------------------------------------------------------------------------------------
    // (4) LocationEnemySerializer.  No changes—already public fields if needed.
    // ------------------------------------------------------------------------------------
    public class LocationEnemySerializer : MonoBehaviour, ISerializableGameObject
    {
        #region Fields
        DaggerfallEnemy enemy;
        DaggerfallEntityBehaviour entityBehaviour;
        Vector3         worldCompensation;
        private LocationSaveDataInterface saveDataInterface;
        #endregion

        #region Unity
        void Awake()
        {
            enemy = GetComponent<DaggerfallEnemy>();
            if (!enemy)
                throw new Exception("DaggerfallEnemy not found on LocationEnemySerializer GameObject.");
            entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();
            saveDataInterface = LocationModLoader.modObject.GetComponent<LocationSaveDataInterface>();
        }

        void OnEnable()
        {
            if (LoadID != 0)
            {
                RefreshWorldCompensation();
                saveDataInterface.RegisterActiveSerializer(this);
            }
        }

        void OnDisable()
        {
            if (LoadID != 0 && LocationModLoader.modObject != null)
            {
                if (IsDead())
                    saveDataInterface.AddDeadEnemy(this);
                saveDataInterface.DeregisterActiveSerializer(this);
            }
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            UnityEditor.Handles.Label(transform.position + Vector3.up, "LoadID=" + LoadID);
        }
#endif
        #endregion

        public bool IsDead()
        {
            if (!entityBehaviour) return false;
            var ent = (EnemyEntity)entityBehaviour.Entity;
            return ent.CurrentHealth <= 0;
        }

        public void RefreshWorldCompensation()
        {
            worldCompensation = GameManager.Instance.StreamingWorld.WorldCompensation;
        }

        public void InvalidateSave()
        {
            if (LoadID != 0 && saveDataInterface != null)
            {
                saveDataInterface.DeregisterActiveSerializer(this);
                enemy = null;
            }
        }

        #region ISerializableGameObject
        public ulong LoadID => GetLoadID();
        public bool  ShouldSave => HasChanged();

        public object GetSaveData()
        {
            if (!enemy) return null;
            var eb = GetComponent<DaggerfallEntityBehaviour>();
            if (eb == null) return null;

            var entity       = (EnemyEntity)eb.Entity;
            var motor        = enemy.GetComponent<EnemyMotor>();
            var senses       = enemy.GetComponent<EnemySenses>();
            var mobileEnemy  = enemy.GetComponentInChildren<MobileUnit>();

            var data = new EnemyData_v1
            {
                loadID                   = LoadID,
                gameObjectName           = eb.gameObject.name,
                currentPosition          = enemy.transform.position,
                localPosition            = enemy.transform.localPosition,
                currentRotation          = enemy.transform.rotation,
                worldContext             = entity.WorldContext,
                worldCompensation        = worldCompensation,
                entityType               = entity.EntityType,
                careerName               = entity.Career.Name,
                careerIndex              = entity.CareerIndex,
                startingHealth           = entity.MaxHealth,
                currentHealth            = entity.CurrentHealth,
                currentFatigue           = entity.CurrentFatigue,
                currentMagicka           = entity.CurrentMagicka,
                isHostile                = motor.IsHostile,
                hasEncounteredPlayer     = senses.HasEncounteredPlayer,
                isDead                   = entity.CurrentHealth <= 0,
                questSpawn               = enemy.QuestSpawn,
                mobileGender             = mobileEnemy.Enemy.Gender,
                items                    = entity.Items.SerializeItems(),
                equipTable               = entity.ItemEquipTable.SerializeEquipTable(),
                instancedEffectBundles   = GetComponent<EntityEffectManager>()
                                              .GetInstancedBundlesSaveData(),
                alliedToPlayer           = (mobileEnemy.Enemy.Team == MobileTeams.PlayerAlly),
                questFoeSpellQueueIndex  = entity.QuestFoeSpellQueueIndex,
                questFoeItemQueueIndex   = entity.QuestFoeItemQueueIndex,
                wabbajackActive          = entity.WabbajackActive,
                team                     = (int)entity.Team + 1,
                specialTransformationCompleted = mobileEnemy.SpecialTransformationCompleted
            };

            var qrb = GetComponent<QuestResourceBehaviour>();
            if (qrb != null)
                data.questResource = qrb.GetSaveData();

            return data;
        }

        public void RestoreSaveData(object dataIn)
        {
            if (!enemy) return;
            var data = (EnemyData_v1)dataIn;
            if (data.loadID != LoadID) return;

            var eb   = GetComponent<DaggerfallEntityBehaviour>();
            var senses = enemy.GetComponent<EnemySenses>();
            var motor  = enemy.GetComponent<EnemyMotor>();
            var entity = eb.Entity as EnemyEntity;
            var mobileEnemy = enemy.GetComponentInChildren<MobileUnit>();

            bool genderChanged = false;
            if (data.mobileGender != MobileGender.Unspecified)
            {
                if      (entity.Gender == Genders.Male   && data.mobileGender == MobileGender.Female) genderChanged = true;
                else if (entity.Gender == Genders.Female && data.mobileGender == MobileGender.Male)   genderChanged = true;
            }

            if (entity == null
                || entity.EntityType != data.entityType
                || entity.CareerIndex != data.careerIndex
                || genderChanged)
            {
                var setupEnemy = enemy.GetComponent<SetupDemoEnemy>();
                setupEnemy.ApplyEnemySettings(
                    data.entityType,
                    data.careerIndex,
                    data.mobileGender,
                    data.isHostile,
                    alliedToPlayer: data.alliedToPlayer
                );
                setupEnemy.AlignToGround();
                entity = eb.Entity as EnemyEntity;
            }

            entity.Quiesce = true;
            eb.gameObject.name = data.gameObjectName;
            enemy.transform.rotation          = data.currentRotation;
            entity.QuestFoeSpellQueueIndex    = data.questFoeSpellQueueIndex;
            entity.QuestFoeItemQueueIndex     = data.questFoeItemQueueIndex;
            entity.WabbajackActive            = data.wabbajackActive;
            entity.Items.DeserializeItems(data.items);
            entity.ItemEquipTable.DeserializeEquipTable(data.equipTable, entity.Items);
            entity.MaxHealth                  = data.startingHealth;
            entity.SetHealth(data.currentHealth, true);
            entity.SetFatigue(data.currentFatigue, true);
            entity.SetMagicka(data.currentMagicka, true);
            if (data.team > 0)
                entity.Team = (MobileTeams)(data.team - 1);
            motor.IsHostile         = data.isHostile;
            senses.HasEncounteredPlayer = data.hasEncounteredPlayer;

            float diffY = GameManager.Instance.StreamingWorld.WorldCompensation.y - data.worldCompensation.y;
            enemy.transform.position = data.currentPosition + new Vector3(0, diffY, 0);

            if (data.isDead)
                eb.gameObject.SetActive(false);

            enemy.QuestSpawn = data.questSpawn;
            if (enemy.QuestSpawn)
            {
                var qrb = eb.gameObject.AddComponent<QuestResourceBehaviour>();
                qrb.RestoreSaveData(data.questResource);
                if (qrb.QuestUID == 0 || qrb.TargetSymbol == null)
                {
                    enemy.QuestSpawn = false;
                    Destroy(qrb);
                }
            }

            GetComponent<EntityEffectManager>()
                .RestoreInstancedBundleSaveData(data.instancedEffectBundles);

            if (data.specialTransformationCompleted && mobileEnemy)
                mobileEnemy.SetSpecialTransformationCompleted();

            entity.Quiesce = false;
        }
        #endregion

        #region Helpers
        bool HasChanged()
        {
            return (enemy != null);
        }

        ulong GetLoadID()
        {
            return (enemy ? enemy.LoadID : 0);
        }
        #endregion
    }

    // ------------------------------------------------------------------------------------
    // (5) Our new “fake‐dungeon” save‐data struct (v2):
    // ------------------------------------------------------------------------------------
    [fsObject("v2")]
    public struct LocationSaveData_v2
    {
        // --- original v1 arrays ---
        public ulong[]              clearedEnemies;
        public EnemyData_v1[]       activeEnemies;
        public LocationLootData_v1[] lootContainers;

        // --- new v2 fields for fake‐dungeon support ---
        public bool     wasInFakeDungeon;
        public int      fakeWorldX;
        public int      fakeWorldZ;
        public int      realWorldX;
        public int      realWorldZ;
        public Vector3  exitReturnPos;
    }

    // ------------------------------------------------------------------------------------
    // (6) The main IHasModSaveData implementer.
    // ------------------------------------------------------------------------------------
    public class LocationSaveDataInterface : MonoBehaviour, IHasModSaveData
    {
        public static ulong ToObjectLoadId(ulong locationId, int objectId)
        {
            return (locationId << 16) | (uint)objectId;
        }

        public static ulong LocationIdFromObjectLoadId(ulong objectLoadId)
        {
            return objectLoadId >> 16;
        }

        // Internal dictionaries to hold serialized data
        Dictionary<ulong, LocationLootData_v1>       savedLoot = new Dictionary<ulong, LocationLootData_v1>();
        Dictionary<ulong, LocationLootSerializer>     activeLootSerializers = new Dictionary<ulong, LocationLootSerializer>();
        Dictionary<ulong, LocationEnemySerializer>    activeEnemySerializers = new Dictionary<ulong, LocationEnemySerializer>();
        HashSet<ulong>                               clearedEnemies = new HashSet<ulong>();

        #region Unity

        void OnEnable()
        {
            StreamingWorld.OnFloatingOriginChange += StreamingWorld_OnFloatingOriginChange;
            SaveLoadManager.OnStartLoad         += SaveLoadManager_OnStartLoad;
        }

        void OnDisable()
        {
            StreamingWorld.OnFloatingOriginChange -= StreamingWorld_OnFloatingOriginChange;
            SaveLoadManager.OnStartLoad         -= SaveLoadManager_OnStartLoad;
        }

        private void SaveLoadManager_OnStartLoad(SaveData_v1 saveData)
        {
            clearedEnemies.Clear();
        }

        private void StreamingWorld_OnFloatingOriginChange()
        {
            foreach (var loot in activeLootSerializers.Values)
                loot.RefreshWorldCompensation();
            foreach (var enemy in activeEnemySerializers.Values)
                enemy.RefreshWorldCompensation();
        }

        #endregion

        #region Loot / Enemy Registration

        public void SerializeLoot(LocationLootSerializer serializer)
        {
            savedLoot[serializer.LoadID] = (LocationLootData_v1)serializer.GetSaveData();
        }

        public bool TryDeserializeLoot(LocationLootSerializer serializer)
        {
            if (!savedLoot.TryGetValue(serializer.LoadID, out LocationLootData_v1 value))
                return false;
            serializer.RestoreSaveData(value);
            return true;
        }

        public void RegisterActiveSerializer(LocationLootSerializer serializer)
        {
            activeLootSerializers.Add(serializer.LoadID, serializer);
        }

        public void DeregisterActiveSerializer(LocationLootSerializer serializer)
        {
            activeLootSerializers.Remove(serializer.LoadID);
        }

        public void AddDeadEnemy(LocationEnemySerializer serializer)
        {
            clearedEnemies.Add(serializer.LoadID);
        }

        public bool IsEnemyDead(ulong loadID)
        {
            return clearedEnemies.Contains(loadID);
        }

        public void RegisterActiveSerializer(LocationEnemySerializer serializer)
        {
            activeEnemySerializers.Add(serializer.LoadID, serializer);
        }

        public void DeregisterActiveSerializer(LocationEnemySerializer serializer)
        {
            activeEnemySerializers.Remove(serializer.LoadID);
        }

        #endregion

        #region Helpers to Flush/Reload Instances

        void FlushActiveInstances()
        {
            foreach (var loot in activeLootSerializers.Values)
                if (loot.Activated)
                    SerializeLoot(loot);

            foreach (var enemy in activeEnemySerializers.Values)
                if (enemy.IsDead())
                    AddDeadEnemy(enemy);
        }

        void ReloadActiveInstances(EnemyData_v1[] enemies)
        {
            var currentLoot = activeLootSerializers.Values.ToArray();
            foreach (var loot in currentLoot)
                TryDeserializeLoot(loot);

            var currentEnemies = activeEnemySerializers.Values.ToArray();
            foreach (var activeSerializer in currentEnemies)
            {
                if (clearedEnemies.Contains(activeSerializer.LoadID))
                {
                    Destroy(activeSerializer.gameObject);
                }
                else
                {
                    EnemyData_v1 enemyData = enemies.FirstOrDefault(e => e.loadID == activeSerializer.LoadID);
                    if (enemyData != null)
                        activeSerializer.RestoreSaveData(enemyData);
                    else
                        Debug.LogWarning($"Location loader: Enemy data not found for loadID {activeSerializer.LoadID}");
                }
            }
        }

        #endregion

        #region IHasModSaveData Implementation

        public Type SaveDataType => typeof(LocationSaveData_v2);

        public object NewSaveData()
        {
            return new LocationSaveData_v2
            {
                clearedEnemies    = Array.Empty<ulong>(),
                activeEnemies     = Array.Empty<EnemyData_v1>(),
                lootContainers    = Array.Empty<LocationLootData_v1>(),
                wasInFakeDungeon  = false,
                fakeWorldX        = 0,
                fakeWorldZ        = 0,
                realWorldX        = 0,
                realWorldZ        = 0,
                exitReturnPos     = Vector3.zero
            };
        }

        public object GetSaveData()
        {
            FlushActiveInstances();

            var thresholdValue = MakeLootThresholdExpirationValue();
            var lootToSave = savedLoot.Values.Where(
                loot => activeLootSerializers.ContainsKey(loot.loadID)
                        || loot.stockedDate >= thresholdValue
            );

            var fakeData = FakeDungeonSaveDataHandler.Instance.CurrentData;

            var data = new LocationSaveData_v2
            {
                // v1 fields:
                clearedEnemies    = clearedEnemies.ToArray(),
                activeEnemies     = activeEnemySerializers.Values
                                       .Select(serializer => (EnemyData_v1)serializer.GetSaveData())
                                       .ToArray(),
                lootContainers    = lootToSave.ToArray(),

                // v2 fields:
                wasInFakeDungeon  = fakeData.wasInFakeDungeon,
                fakeWorldX        = fakeData.fakeWorldX,
                fakeWorldZ        = fakeData.fakeWorldZ,
                realWorldX        = fakeData.realWorldX,
                realWorldZ        = fakeData.realWorldZ,
                exitReturnPos     = fakeData.exitReturnPos
            };

            return data;
        }

        public void RestoreSaveData(object saveData)
        {
            var data = (LocationSaveData_v2)saveData;

            // Restore v1 dictionaries:
            savedLoot.Clear();
            foreach (var loot in data.lootContainers)
                savedLoot[loot.loadID] = loot;

            clearedEnemies.Clear();
            foreach (var cleared in data.clearedEnemies)
                clearedEnemies.Add(cleared);

            ReloadActiveInstances(data.activeEnemies);

            // Copy fake‐dungeon fields into the singleton:
            var fakeData = FakeDungeonSaveDataHandler.Instance.CurrentData;
            fakeData.wasInFakeDungeon = data.wasInFakeDungeon;
            fakeData.fakeWorldX       = data.fakeWorldX;
            fakeData.fakeWorldZ       = data.fakeWorldZ;
            fakeData.realWorldX       = data.realWorldX;
            fakeData.realWorldZ       = data.realWorldZ;
            fakeData.exitReturnPos    = data.exitReturnPos;

            // If we were saved inside a fake dungeon, DFU will now respawn in “realWorldX/realWorldZ.”
            // After that respawn finishes, we snap back to fakeWorldX/fakeWorldZ + exitReturnPos.
            if (data.wasInFakeDungeon)
            {
                var gps = GameManager.Instance.PlayerGPS;
                gps.WorldX = data.realWorldX;
                gps.WorldZ = data.realWorldZ;

                PlayerEnterExit.OnRespawnerComplete += OnRespawnedIntoFakeDungeon;
            }
        }

        void OnRespawnedIntoFakeDungeon()
        {
            PlayerEnterExit.OnRespawnerComplete -= OnRespawnedIntoFakeDungeon;

            var fakeData = FakeDungeonSaveDataHandler.Instance.CurrentData;
            var gps      = GameManager.Instance.PlayerGPS;

            gps.WorldX = fakeData.fakeWorldX;
            gps.WorldZ = fakeData.fakeWorldZ;

            var gpsGO = gps.gameObject;
            gpsGO.transform.position = fakeData.exitReturnPos;

            var adv = GameObject.Find("PlayerAdvanced");
            if (adv) adv.transform.position = fakeData.exitReturnPos + Vector3.up * 0.1f;
        }

        #endregion

        #region Utility

        public static int MakeLootThresholdExpirationValue()
        {
            var time = DaggerfallUnity.Instance.WorldTime.Now;
            int thresholdYear = time.Year;
            int thresholdDay  = time.DayOfYear; // 1–360

            if (thresholdDay <= LocationLoader.LootExpirationDays)
            {
                thresholdYear -= 1;
                thresholdDay = 360 + thresholdDay - LocationLoader.LootExpirationDays;
            }
            else
            {
                thresholdDay -= LocationLoader.LootExpirationDays;
            }

            return thresholdYear * 1000 + thresholdDay;
        }

        #endregion
    }

    // ------------------------------------------------------------------------------------
    // (7) A small singleton to hold “fake‐dungeon” fields at runtime.
    // ------------------------------------------------------------------------------------
    [Serializable]
    public class FakeDungeonSaveData
    {
        public bool    wasInFakeDungeon = false;
        public int     fakeWorldX       = 0;
        public int     fakeWorldZ       = 0;
        public int     realWorldX       = 0;
        public int     realWorldZ       = 0;
        public Vector3 exitReturnPos    = Vector3.zero;
    }

    public class FakeDungeonSaveDataHandler
    {
        static FakeDungeonSaveDataHandler instance;
        public static FakeDungeonSaveDataHandler Instance
        {
            get
            {
                if (instance == null)
                    instance = new FakeDungeonSaveDataHandler();
                return instance;
            }
        }

        public FakeDungeonSaveData CurrentData = new FakeDungeonSaveData();
    }
}

