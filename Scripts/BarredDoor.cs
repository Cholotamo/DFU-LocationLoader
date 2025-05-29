using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using DaggerfallWorkshop.Utility;
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
/// Intercepts clicks via a custom raycast including triggers.
/// </summary>
public class BarredDoor : MonoBehaviour, IPlayerActivable
{
    // set by LocationLoader when you create the trigger
    public bool hasDungeon = false;
    public int dungeonRegion;
    public int dungeonLocation;

    // remember where to return on exit
    Vector3 _exitReturnPos;

    bool _listeningInterior = false;
    bool _listeningExterior = false;

    BoxCollider _bc;

    void Awake()
    {
        // cache the BoxCollider and ensure it's a trigger
        _bc = GetComponent<BoxCollider>();
        if (_bc == null)
            Debug.LogError("BarredDoor requires a BoxCollider component");
        else
            _bc.isTrigger = true;
    }

    void Update()
    {
        // on left mouse button down, perform custom raycast that includes triggers
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

            // sort by distance
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            // look for our trigger before any other
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

        // record the current external position
        _exitReturnPos = GameManager.Instance.PlayerGPS.gameObject.transform.position;

        // subscribe once for dungeon interior event
        if (!_listeningInterior)
        {
            PlayerEnterExit.OnTransitionDungeonInterior += OnDungeonInterior;
            _listeningInterior = true;
        }

        // disable micro-map
        DaggerfallUnity.Settings.AutomapDisableMicroMap = true;

        // load and enter dungeon
        DFLocation loc = DaggerfallUnity.Instance.ContentReader
            .MapFileReader
            .GetLocation(dungeonRegion, dungeonLocation);
        if (!loc.Loaded)
        {
            DaggerfallUI.AddHUDText("Failed to load location.");
            return;
        }
        enterExit.StartDungeonInterior(loc, preferEnterMarker: true, importEnemies: true);
    }

    private void OnDungeonInterior(PlayerEnterExit.TransitionEventArgs args)
    {
        // unsubscribe
        PlayerEnterExit.OnTransitionDungeonInterior -= OnDungeonInterior;
        _listeningInterior = false;

        var automap = Automap.instance;
        if (automap == null)
        {
            Debug.LogError("Automap not found!");
            return;
        }

        // build automap
        automap.UpdateAutomapStateOnWindowPush();

        // clear blank window hack
        var ui = DaggerfallUI.UIManager;
        var tempWindow = new DaggerfallAutomapWindow(ui);
        ui.PushWindow(tempWindow);
        ui.PopWindow();

        // subscribe to exit event
        if (!_listeningExterior)
        {
            PlayerEnterExit.OnTransitionDungeonExterior += OnDungeonExterior;
            _listeningExterior = true;
        }
    }

    private void OnDungeonExterior(PlayerEnterExit.TransitionEventArgs args)
    {
        // unsubscribe
        PlayerEnterExit.OnTransitionDungeonExterior -= OnDungeonExterior;
        _listeningExterior = false;

        // restore position
        var gpsGO = GameManager.Instance.PlayerGPS.gameObject;
        gpsGO.transform.position = _exitReturnPos;

        var adv = GameObject.Find("PlayerAdvanced");
        if (adv) adv.transform.position = _exitReturnPos + Vector3.up * 0.1f;

        // re-enable micro-map
        DaggerfallUnity.Settings.AutomapDisableMicroMap = false;
    }
}

