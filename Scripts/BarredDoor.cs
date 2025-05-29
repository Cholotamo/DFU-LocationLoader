using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.UserInterface;

public class BarredDoor : MonoBehaviour, IPlayerActivable
{
    // This is called by PlayerActivate when the player clicks this trigger.
    public void Activate(RaycastHit hit)
    {
        DaggerfallUI.AddHUDText("The door is barred from the other side.");
    }
}

