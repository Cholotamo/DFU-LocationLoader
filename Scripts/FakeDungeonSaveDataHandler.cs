using System;
using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.Utility.ModSupport;

/// <summary>
/// Holds the run‐time “fake‐dungeon” state.  This is read/written by
/// LocationSaveDataInterface.GetSaveData() and RestoreSaveData().
/// </summary>
[Serializable]
public class FakeDungeonSaveData
{
    public bool wasInFakeDungeon = false;
    public int  fakeWorldX       = 0;
    public int  fakeWorldZ       = 0;
    public int  realWorldX       = 0;
    public int  realWorldZ       = 0;
    public Vector3 exitReturnPos = Vector3.zero;
}

/// <summary>
/// Simple singleton so that BarredDoor and LocationSaveDataInterface can share it.
/// </summary>
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

