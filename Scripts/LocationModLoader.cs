using System.Collections.Generic;
using DaggerfallWorkshop;
using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Utility.ModSupport;   //required for modding features

namespace LocationLoader
{
    public static class LocationModLoader
    {
        public static Mod mod { get; private set; }
        public static GameObject modObject { get; private set; }

	    private static Mod WODBiomesMod;
	    public static bool WODBiomesModEnabled;

	    private static Mod VEMod;
	    public static bool VEModEnabled;

        [Invoke(StateManager.StateTypes.Start, 0)]
        public static void Init(InitParams initParams)
        {
            // Get mod
            mod = initParams.Mod;

            modObject = new GameObject("LocationLoader");
            modObject.AddComponent<LocationLoader>();
            mod.SaveDataInterface = modObject.AddComponent<LocationSaveDataInterface>();
            modObject.AddComponent<LocationResourceManager>();
            mod.MessageReceiver = MessageReceiver;
            mod.IsReady = true;

		    WODBiomesMod = ModManager.Instance.GetModFromGUID("3b4319ac-34bb-411d-aa2c-d52b7b9eb69d");
		    if (WODBiomesMod != null && WODBiomesMod.Enabled)
		    {
			    WODBiomesModEnabled = true;
		    }

		    VEMod = ModManager.Instance.GetModFromGUID("1f124f8c-dd01-48ad-a5b9-0b4a0e4702d2");
		    if (VEMod != null && VEMod.Enabled)
		    {
			    VEModEnabled = true;
		    }

            // It's okay if other mods override us, they better provide a compatibility patch though
            DaggerfallUnity.Instance.TerrainNature = new LocationTerrainNature();
            //DaggerfallUnity.Instance.TerrainTexturing = new LocationTerrainTexturing();

            const int ladderModelId = 41409;
            PlayerActivate.RegisterCustomActivation(mod, ladderModelId, OnLadderActivated);
        }

        private static void MessageReceiver(string message, object data, DFModMessageCallback callback)
        {
            var ll = modObject.GetComponent<LocationLoader>();
            switch (message)
            {
                case "getTerrainLocationInstanceRects":
                    var mapPixelCoord = (Vector2Int)data;
                    LocationLoader.LLTerrainData extraData = ll.GetTerrainExtraData(mapPixelCoord);
                    callback(message, new List<Rect>(extraData.LocationInstanceRects));
                    break;
            }
        }

        static void OnLadderActivated(RaycastHit hit)
        {
            // Player must not be inside a building (already handled by DFU)
            PlayerEnterExit playerEnterExit = GameManager.Instance.PlayerEnterExit;
            if (playerEnterExit.IsPlayerInsideBuilding)
                return;

            Transform ladderTransform = hit.transform;
            Transform prefabTransform = ladderTransform.parent;
            GameObject prefabObject = prefabTransform.gameObject;
            LocationData data = prefabObject.GetComponent<LocationData>();

            PlayerMotor playerMotor = GameManager.Instance.PlayerMotor;
            bool foundBottom = data.FindClosestMarker(EditorMarkerTypes.LadderBottom, playerMotor.transform.position, out Vector3 bottomMarker);
            bool foundTop = data.FindClosestMarker(EditorMarkerTypes.LadderTop, playerMotor.transform.position, out Vector3 topMarker);

            Vector2 ladderPlanarPos = new Vector2(ladderTransform.position.x, ladderTransform.position.z);
            Vector2 bottomMarkerPlanarPos = new Vector2(bottomMarker.x, bottomMarker.z);
            Vector2 topMarkerPlanarPos = new Vector2(topMarker.x, topMarker.z);

            float bottomPlanarDistance = Vector2.Distance(ladderPlanarPos, bottomMarkerPlanarPos);
            float topPlanarDistance = Vector2.Distance(ladderPlanarPos, topMarkerPlanarPos);

            const float MaxMarkerDistance = PlayerActivate.DefaultActivationDistance * 2;
            foundBottom = foundBottom && bottomPlanarDistance < MaxMarkerDistance;
            foundTop = foundTop && topPlanarDistance < MaxMarkerDistance;

            float bottomDistance = Vector3.Distance(playerMotor.transform.position, bottomMarker);
            float topDistance = Vector3.Distance(playerMotor.transform.position, topMarker);

            // Teleport to top marker
            if (foundTop && (!foundBottom || topDistance > bottomDistance))
            {
                playerMotor.transform.position = topMarker;
                playerMotor.FixStanding();
            }
            else if (foundBottom)
            {
                playerMotor.transform.position = bottomMarker;
                playerMotor.FixStanding();
            }
        }
    }
}
