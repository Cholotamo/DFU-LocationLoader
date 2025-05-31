using UnityEngine;
using System;
using DaggerfallWorkshop.Utility;               // ← for StaticDoor
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Utility.AssetInjection;

/// <summary>
/// Attach to a trigger BoxCollider for barred doors.
/// Intercepts clicks via a custom raycast including triggers,
/// then calls the real DF door transition so save/load works correctly.
/// </summary>
public class BarredDoor : MonoBehaviour, IPlayerActivable
{
    // set by LocationLoader when you create this trigger
    public bool hasDungeon = false;
    public int dungeonRegion;
    public int dungeonLocation;

    // this is the “real” StaticDoor that our trigger is faking
    [HideInInspector]
    public StaticDoor staticDoor;

    // where to place the player again, once they exit
    Vector3 _exitReturnPos;

    bool _listeningInterior = false;
    bool _listeningExterior = false;

    BoxCollider _bc;

    void Awake()
    {
        // cache the BoxCollider and force it to be a trigger
        _bc = GetComponent<BoxCollider>();
        if (_bc == null)
            Debug.LogError("BarredDoor requires a BoxCollider component");
        else
            _bc.isTrigger = true;
    }

    void Update()
    {
        // When left‐click happens, do our own RaycastAll (with triggers) and see if we hit this BoxCollider first.
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

        // 1) remember exactly where we were standing, so we can restore on exit:
        _exitReturnPos = gps.transform.position;

        // grab the DFLocation for that region/location…
        DFLocation loc = DaggerfallUnity.Instance.ContentReader
            .MapFileReader
            .GetLocation(dungeonRegion, dungeonLocation);
        if (!loc.Loaded)
        {
            DaggerfallUI.AddHUDText($"Failed to load location {dungeonRegion}-{dungeonLocation}.");
            return;
        }

        // 2) before actually entering the dungeon, “lie” to GPS so that Save/Load
        //    will treat us as “inside dungeon at entryX/entryZ.”
        //      → this prevents Respawner from trying to load a dungeon at our old exterior coords
        int entryX = loc.Exterior.RecordElement.Header.X;
        int entryZ = loc.Exterior.RecordElement.Header.Y;
        gps.WorldX = entryX;
        gps.WorldZ = entryZ;

        // 3) cache exterior world scene for save/restore
        SaveLoadManager.CacheScene(GameManager.Instance.StreamingWorld.SceneName);

        // subscribe once to the “we just loaded a dungeon” event
        if (!_listeningInterior)
        {
            PlayerEnterExit.OnTransitionDungeonInterior += OnDungeonInterior;
            _listeningInterior = true;
        }

        // disable micro‐map
        DaggerfallUnity.Settings.AutomapDisableMicroMap = true;

        // 4) now call DF’s real TransitionDungeonInterior passing our fake StaticDoor,
        //    so that everything (including Save/Load) behaves exactly as if the player clicked a real door.
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

        // Build the automap state so the player sees the dungeon automap:
        var automap = Automap.instance;
        if (automap == null)
        {
            Debug.LogError("Automap not found!");
            return;
        }
        automap.UpdateAutomapStateOnWindowPush();

        // Clear that “blank” automap window hack:
        var ui = DaggerfallUI.UIManager;
        var tempWindow = new DaggerfallAutomapWindow(ui);
        ui.PushWindow(tempWindow);
        ui.PopWindow();

        // Now that we’re inside the dungeon, cache that new dungeon scene as well:
        var dungeonGO = GameManager.Instance.PlayerEnterExit.Dungeon.gameObject;
        SaveLoadManager.CacheScene(dungeonGO.name);

        // Finally, hook OnTransitionDungeonExterior so we can restore when they leave:
        if (!_listeningExterior)
        {
            PlayerEnterExit.OnTransitionDungeonExterior += OnDungeonExterior;
            _listeningExterior = true;
        }
    }

    private void OnDungeonExterior(PlayerEnterExit.TransitionEventArgs args)
    {
        // Unsubscribe:
        PlayerEnterExit.OnTransitionDungeonExterior -= OnDungeonExterior;
        _listeningExterior = false;

        var streaming = GameManager.Instance.StreamingWorld;
        var gps       = GameManager.Instance.PlayerGPS;

        // 5) restore the exterior‐world scene we cached back in Activate()
        SaveLoadManager.RestoreCachedScene(streaming.SceneName);

        // 6) put the player back exactly where they clicked the barred door
        var gpsGO = gps.gameObject;
        gpsGO.transform.position = _exitReturnPos;

        var adv = GameObject.Find("PlayerAdvanced");
        if (adv) adv.transform.position = _exitReturnPos + Vector3.up * 0.1f;

        // 7) re‐enable micro‐map
        DaggerfallUnity.Settings.AutomapDisableMicroMap = false;
    }
}

