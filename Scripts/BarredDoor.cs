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

public class BarredDoor : MonoBehaviour, IPlayerActivable
{
    // set by your LocationLoader when you Create the trigger
    public bool hasDungeon = false;
    public int dungeonRegion;
    public int dungeonLocation;

    // Remember the spot to return the player to
    Vector3 _exitReturnPos;

    bool _listeningInterior = false;
    bool _listeningExterior = false;

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

        // only subscribe once
        if (!_listeningInterior)
        {
            PlayerEnterExit.OnTransitionDungeonInterior += OnDungeonInterior;
            _listeningInterior = true;
        }

        // disable the micro-map for our hack
        DaggerfallUnity.Settings.AutomapDisableMicroMap = true;

        // kick off the interior load
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
        // no longer need this
        PlayerEnterExit.OnTransitionDungeonInterior -= OnDungeonInterior;
        _listeningInterior = false;

        var automap = Automap.instance;
        if (automap == null)
        {
            Debug.LogError("Automap not found!");
            return;
        }

        // build the automap state
        automap.UpdateAutomapStateOnWindowPush();

        // clear that blank window hack
        var ui = DaggerfallUI.UIManager;
        var tempWindow = new DaggerfallAutomapWindow(ui);
        ui.PushWindow(tempWindow);
        ui.PopWindow();

        // subscribe to the “exit dungeon” event
        if (!_listeningExterior)
        {
            PlayerEnterExit.OnTransitionDungeonExterior += OnDungeonExterior;
            _listeningExterior = true;
        }
    }

    private void OnDungeonExterior(PlayerEnterExit.TransitionEventArgs args)
    {
        // only once
        PlayerEnterExit.OnTransitionDungeonExterior -= OnDungeonExterior;
        _listeningExterior = false;

        // restore player position
        var gpsGO = GameManager.Instance.PlayerGPS.gameObject;
        gpsGO.transform.position = _exitReturnPos;

        // also update the “advanced” instance if you want:
        var adv = GameObject.Find("PlayerAdvanced");
        if (adv) adv.transform.position = _exitReturnPos + Vector3.up * 0.1f;

        // re-enable your micro map if you want
        DaggerfallUnity.Settings.AutomapDisableMicroMap = false;
    }
}
