using UnityEngine;
using System;
using DaggerfallWorkshop.Utility;               // for StaticDoor
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Utility.AssetInjection;

namespace LocationLoader
{
    /// <summary>
    /// Attach to a trigger BoxCollider for barred doors.
    /// Intercepts clicks via a custom raycast including triggers.
    /// Also writes into LocationSaveDataInterface when entering/exiting a fake dungeon.
    /// </summary>
    public class BarredDoor : MonoBehaviour, IPlayerActivable
    {
        // set by LocationLoader when you create the trigger
        public bool hasDungeon = false;
        public int dungeonRegion;
        public int dungeonLocation;

        // the real StaticDoor this trigger is faking
        [HideInInspector]
        public StaticDoor staticDoor;

        // remember exactly where to come back (local-space position)
        Vector3 _exitReturnPos;

        bool _listeningInterior = false;
        bool _listeningExterior = false;

        BoxCollider _bc;
        private LocationSaveDataInterface _saveInterface;

        void Awake()
        {
            // cache the BoxCollider and force it to be a trigger
            _bc = GetComponent<BoxCollider>();
            if (_bc == null)
                Debug.LogError("BarredDoor requires a BoxCollider component");
            else
                _bc.isTrigger = true;

            // fetch the single SaveDataInterface instance
            _saveInterface = LocationModLoader.modObject.GetComponent<LocationSaveDataInterface>();
            if (_saveInterface == null)
                Debug.LogError("BarredDoor: LocationSaveDataInterface not found on modObject!");

            // If we loaded while already in a fake dungeon, restore exit pos immediately
            if (_saveInterface != null && _saveInterface.wasInFakeDungeon)
            {
                _exitReturnPos = _saveInterface.exitReturnPos;
                // We also want to start listening for an exit event,
                // even though Activate() wasn't called this session.
                PlayerEnterExit.OnTransitionDungeonExterior += OnDungeonExterior;
                _listeningExterior = true;
            }
        }

        void Update()
        {
            // Perform custom raycast on Left-click to detect triggers (including this one's BoxCollider)
            if (Input.GetMouseButtonDown(0))
            {
                Camera cam = Camera.main;
                if (cam == null) return;

                Ray ray = cam.ScreenPointToRay(Input.mousePosition);
                RaycastHit[] hits = Physics.RaycastAll(
                    ray,
                    100f,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Collide);

                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

                foreach (var hit in hits)
                {
                    if (hit.collider == _bc)
                    {
                        Activate(hit);
                        break;
                    }
                }
            }
        }

        public void Activate(RaycastHit hit)
        {
            if (!hasDungeon)
            {
                DaggerfallUI.AddHUDText("This door is barred from the other side.");
                return;
            }

            var enterExit = GameManager.Instance.PlayerEnterExit;
            var gps       = GameManager.Instance.PlayerGPS;
            var streaming = GameManager.Instance.StreamingWorld;

            // 1) Record our “fake” exterior coords and exact local-space position
            _exitReturnPos = gps.transform.position;

            // 2) Load the DFLocation for the real dungeon:
            DFLocation loc = DaggerfallUnity.Instance.ContentReader
                .MapFileReader
                .GetLocation(dungeonRegion, dungeonLocation);
            if (!loc.Loaded)
            {
                DaggerfallUI.AddHUDText($"Failed to load location {dungeonRegion}-{dungeonLocation}.");
                return;
            }

            // 4) Tell our save-data handler that we are now “in a fake dungeon”
            _saveInterface.wasInFakeDungeon = true;
            _saveInterface.dungeonRegion       = dungeonRegion;
            _saveInterface.dungeonLocation     = dungeonLocation;
            _saveInterface.exitReturnPos    = _exitReturnPos;

            // 6) Cache the exterior world scene now, so it can be re-loaded on exit
            SaveLoadManager.CacheScene(streaming.SceneName);

            // 7) Subscribe once for “we just finished loading into a dungeon interior”
            if (!_listeningInterior)
            {
                PlayerEnterExit.OnTransitionDungeonInterior += OnDungeonInterior;
                _listeningInterior = true;
            }

            // 8) Disable micro-map (same as Daggerfall’s own code)
            DaggerfallUnity.Settings.AutomapDisableMicroMap = true;

            // 9) Now start the dungeon
            enterExit.StartDungeonInterior(loc, preferEnterMarker: true, importEnemies: true);

        }

        private void OnDungeonInterior(PlayerEnterExit.TransitionEventArgs args)
        {
            // Unsubscribe immediately from the “entered interior” event
            PlayerEnterExit.OnTransitionDungeonInterior -= OnDungeonInterior;
            _listeningInterior = false;

            // Build the automap
            var automap = Automap.instance;
            if (automap == null)
            {
                Debug.LogError("Automap not found!");
                return;
            }
            automap.UpdateAutomapStateOnWindowPush();

            // Pop that blank window hack
            var ui = DaggerfallUI.UIManager;
            var tempWindow = new DaggerfallAutomapWindow(ui);
            ui.PushWindow(tempWindow);
            ui.PopWindow();

            // Cache the dungeon-scene we just created (so it can be restored on reload/exit)
            var dungeonGO = GameManager.Instance.PlayerEnterExit.Dungeon.gameObject;
            SaveLoadManager.CacheScene(dungeonGO.name);

            // Now subscribe to “we’re leaving the dungeon exterior”
            if (!_listeningExterior)
            {
                PlayerEnterExit.OnTransitionDungeonExterior += OnDungeonExterior;
                _listeningExterior = true;
            }
        }

        private void OnDungeonExterior(PlayerEnterExit.TransitionEventArgs args)
        {
            var streaming = GameManager.Instance.StreamingWorld;
            var gps       = GameManager.Instance.PlayerGPS;

            // Unsubscribe from the exit event
            PlayerEnterExit.OnTransitionDungeonExterior -= OnDungeonExterior;
            _listeningExterior = false;

            // 2) Restore the exterior-world scene we cached at Activate()
            SaveLoadManager.RestoreCachedScene(streaming.SceneName);

            // 3) Queue a reposition so that when the world finishes streaming back in,
            //    the player is placed precisely at the saved Unity-space coordinate (_exitReturnPos)
            streaming.SetAutoReposition(
                StreamingWorld.RepositionMethods.Offset,
                _exitReturnPos);

            // 4) Also put “PlayerAdvanced” one unit above so collisions line up
            var adv = GameObject.Find("PlayerAdvanced");
            if (adv) adv.transform.position = _exitReturnPos + Vector3.up * 0.1f;

            // 5) Re-enable micro-map
            DaggerfallUnity.Settings.AutomapDisableMicroMap = false;

            // 6) Mark “no longer in fake dungeon” so that future saves behave normally:
            _saveInterface.wasInFakeDungeon = false;
        }
    }

    /// <summary>
    /// Listens for “exit dungeon” transitions at all times.
    /// If we had saved wasInFakeDungeon = true, this will restore GPS and reposition.
    /// Attach this to the same GameObject that holds your LocationSaveDataInterface
    /// (e.g. the mod’s root GameObject).
    /// </summary>
    public class DungeonExitHandler : MonoBehaviour
    {
        LocationSaveDataInterface _saveInterface;

        void Awake()
        {
            // Grab the SaveDataInterface from the same GameObject
            _saveInterface = GetComponent<LocationSaveDataInterface>();
            if (_saveInterface == null)
                Debug.LogError("DungeonExitHandler: missing LocationSaveDataInterface on this GameObject!");
        }

        void OnEnable()
        {
            PlayerEnterExit.OnTransitionDungeonExterior += OnDungeonExit;
        }

        void OnDisable()
        {
            PlayerEnterExit.OnTransitionDungeonExterior -= OnDungeonExit;
        }

        private void OnDungeonExit(PlayerEnterExit.TransitionEventArgs args)
        {
            // Only restore if we actually were “in a fake dungeon”
            if (!_saveInterface.wasInFakeDungeon)
                return;

            // 1) Restore GPS.WorldX/Z to the saved “fake exterior” coords
            var gps = GameManager.Instance.PlayerGPS;

            // 2) Restore the cached exterior scene
            var streaming = GameManager.Instance.StreamingWorld;
            SaveLoadManager.RestoreCachedScene(streaming.SceneName);

            // 3) Queue a reposition so that, once the world finishes streaming,
            //    the player ends up at the saved exitReturnPos:
            streaming.SetAutoReposition(
                StreamingWorld.RepositionMethods.Offset,
                _saveInterface.exitReturnPos);

            // 4) Nudge “PlayerAdvanced” up immediately so collisions line up
            var adv = GameObject.Find("PlayerAdvanced");
            if (adv) adv.transform.position = _saveInterface.exitReturnPos + Vector3.up * 0.1f;

            // 5) Re-enable the micro-map
            DaggerfallUnity.Settings.AutomapDisableMicroMap = false;

            // 6) Clear the flag so it won’t run again inadvertently
            _saveInterface.wasInFakeDungeon = false;
        }
    }

    /// <summary>
    /// If the game is loaded with wasInFakeDungeon=true, wait for the respawner to finish,
    /// then immediately start the dungeon (Region 43, Location 161).  
    /// Attach this to the same GameObject as LocationSaveDataInterface (mod’s root object).
    /// </summary>
    public class FakeDungeonLoader : MonoBehaviour
    {
        LocationSaveDataInterface _saveInterface;

        void Awake()
        {
            _saveInterface = GetComponent<LocationSaveDataInterface>();
            if (_saveInterface == null)
                Debug.LogError("FakeDungeonLoader: missing LocationSaveDataInterface on this GameObject!");
        }

        void OnEnable()
        {
            // Fire after the save is fully restored
            SaveLoadManager.OnLoad += OnGameLoaded;
        }

        void OnDisable()
        {
            SaveLoadManager.OnLoad -= OnGameLoaded;
        }

        private void OnGameLoaded(SaveData_v1 _)
        {
            if (_saveInterface == null)
                return;

            if (!_saveInterface.wasInFakeDungeon)
            {
                Debug.Log("[FakeDungeonLoader] wasInFakeDungeon is false, skipping.");
                return;
            }

            Debug.Log("[FakeDungeonLoader] Detected wasInFakeDungeon == true. Now starting dungeon interior.");

            DFLocation loc = DaggerfallUnity.Instance.ContentReader
                .MapFileReader
                .GetLocation(_saveInterface.dungeonRegion, _saveInterface.dungeonLocation);

            if (!loc.Loaded)
            {
                Debug.LogError($"[FakeDungeonLoader] Failed to load DFLocation {_saveInterface.dungeonRegion}-{_saveInterface.dungeonLocation}.");
                return;
            }

            // Disable the micro‐map just like BarredDoor does:
            DaggerfallUnity.Settings.AutomapDisableMicroMap = true;
            Debug.Log("[FakeDungeonLoader] Calling StartDungeonInterior(...) now.");

            GameManager.Instance.PlayerEnterExit.StartDungeonInterior(
                loc,
                preferEnterMarker: true,
                importEnemies: true);
        }
    }
}
