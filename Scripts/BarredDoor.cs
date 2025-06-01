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

/// <summary>
/// Attach to a trigger BoxCollider for barred doors.
/// Intercepts clicks via a custom raycast including triggers.
/// Also relies on FakeDungeonSaveDataHandler being set as your mod’s SaveDataInterface.
/// </summary>
namespace LocationLoader
{
    public class BarredDoor : MonoBehaviour, IPlayerActivable
    {
        // set by LocationLoader when you create the trigger
        public bool hasDungeon = false;
        public int dungeonRegion;
        public int dungeonLocation;

        // the real StaticDoor this trigger is faking
        [HideInInspector]
        public StaticDoor staticDoor;

        // remember exactly where to come back (local‐space position)
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

            // ① Fetch the single SaveDataInterface instance:
              _saveInterface = LocationModLoader.modObject.GetComponent<LocationSaveDataInterface>();
            if (_saveInterface == null)
                Debug.LogError("BarredDoor: LocationSaveDataInterface not found on modObject!");
        }

        void Update()
        {
            // Perform custom raycast on Left‐click to detect triggers (including this one's BoxCollider)
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

            // 1) Record our “fake” exterior coords and exact local‐position so that we can come back:
            int fakeWX = gps.WorldX;
            int fakeWZ = gps.WorldZ;
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

            // 3) Extract “real” tile‐coords of that dungeon’s entrance:
            int realWX = loc.Exterior.RecordElement.Header.X;
            int realWZ = loc.Exterior.RecordElement.Header.Y;

            // 4) Tell our save‐data handler that we are now “in a fake dungeon”:
            _saveInterface.wasInFakeDungeon  = true;
            _saveInterface.fakeWorldX       = fakeWX;
            _saveInterface.fakeWorldZ       = fakeWZ;
            _saveInterface.realWorldX       = realWX;
            _saveInterface.realWorldZ       = realWZ;
            _saveInterface.exitReturnPos    = _exitReturnPos;

            // 5) Before we actually transition, override GPS so the save system thinks “we’re on the real dungeon tile”:
            gps.WorldX = realWX;
            gps.WorldZ = realWZ;

            // 6) Cache the exterior world scene now, so it can be re‐loaded on exit:
            SaveLoadManager.CacheScene(streaming.SceneName);

            // 7) Subscribe once for “we just finished loading into a dungeon interior”:
            if (!_listeningInterior)
            {
                PlayerEnterExit.OnTransitionDungeonInterior += OnDungeonInterior;
                _listeningInterior = true;
            }

            // 8) Disable micro‐map (same as Daggerfall’s own code):
            DaggerfallUnity.Settings.AutomapDisableMicroMap = true;

            // 9) Now call the “real” door transition:
            enterExit.TransitionDungeonInterior(
                doorOwner: transform,
                door:       staticDoor,
                location:   loc,
                doFade:     true);
        }

        private void OnDungeonInterior(PlayerEnterExit.TransitionEventArgs args)
        {
            // Unsubscribe immediately:
            PlayerEnterExit.OnTransitionDungeonInterior -= OnDungeonInterior;
            _listeningInterior = false;

            // Build the automap:
            var automap = Automap.instance;
            if (automap == null)
            {
                Debug.LogError("Automap not found!");
                return;
            }
            automap.UpdateAutomapStateOnWindowPush();

            // Pop that blank window hack:
            var ui = DaggerfallUI.UIManager;
            var tempWindow = new DaggerfallAutomapWindow(ui);
            ui.PushWindow(tempWindow);
            ui.PopWindow();

            // Cache the dungeon‐scene we just created (so it can be restored on reload/exit):
            var dungeonGO = GameManager.Instance.PlayerEnterExit.Dungeon.gameObject;
            SaveLoadManager.CacheScene(dungeonGO.name);

            // Now subscribe to “we’re leaving the dungeon exterior”:
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

            // 5) Restore GPS.WorldX/WorldZ back to the “fake” exterior coords:
            gps.WorldX = _saveInterface.fakeWorldX;
            gps.WorldZ = _saveInterface.fakeWorldZ;

            // Unsubscribe:
            PlayerEnterExit.OnTransitionDungeonExterior -= OnDungeonExterior;
            _listeningExterior = false;

            // 1) Restore the exterior‐world scene we cached at Activate():
            SaveLoadManager.RestoreCachedScene(streaming.SceneName);

            // 2) Queue a reposition so that when the world finishes streaming back in,
            //    the player is placed precisely at the saved Unity‐space coordinate.
            //    We use RepositionMethods.Offset to move the camera/player to _exitReturnPos.
            streaming.SetAutoReposition(StreamingWorld.RepositionMethods.Offset, _exitReturnPos);

            // 3) Also put “PlayerAdvanced” one unit above so collisions line up.
            //    Note: we still nudge PlayerAdvanced immediately, but the actual character
            //    will be teleported by the StreamingWorld when it finishes loading.
            var adv = GameObject.Find("PlayerAdvanced");
            if (adv) adv.transform.position = _exitReturnPos + Vector3.up * 0.1f;

            // 4) Re‐enable micro‐map:
            DaggerfallUnity.Settings.AutomapDisableMicroMap = false;

            // 6) Mark “no longer in fake dungeon” so that future saves behave normally:
            _saveInterface.wasInFakeDungeon = false;
        }
    }
}

