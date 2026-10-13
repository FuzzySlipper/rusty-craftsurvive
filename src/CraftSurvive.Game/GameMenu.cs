using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game;

/// <summary>
/// The pause menu (#9916). Esc in the UI, or the controller's Start button, holds the game under a
/// centred menu: the clock stops (the Engine's gameplay time runs at zero), the player's input is
/// dropped and nothing advances until Continue. From it the player begins a new world, sets the
/// options or quits where the host has a window to close. Loading another world waits for save slots.
/// </summary>
public sealed partial class CraftSurviveProduct
{
    /// <summary>The controller's Start button (the standard gamepad's button 9) opens and closes the menu.</summary>
    private const ControllerButton MenuButton = ControllerButton.Button9;

    /// <summary>The world actions that hold the game under the menu and let it run again; "pause" is the journey's.</summary>
    private const string MenuPauseAction = "menu-pause", MenuResumeAction = "menu-resume";

    private const string QuitUnavailable = "Close the browser tab to quit; the world saves as you play.";

    private bool paused;

    /// <summary>Hold the game under the menu, or let it run again.</summary>
    private void SetPaused(bool hold)
    {
        if (paused == hold) return;
        paused = hold;
        if (worldBuilt) player.ClearInput();
        PublishMenu();
    }

    private void PublishMenu() => ui.PublishMenu(new GameMenuUiFacts(paused, QuitAvailable));

    private bool QuitAvailable => engine.Host.Read().ExitAvailable;

    /// <summary>Close the window and stop the host, as closing the window does; saves are kept as the game runs.</summary>
    private void Quit()
    {
        if (!QuitAvailable) throw new FormatException(QuitUnavailable);
        engine.Host.RequestExit();
    }

    /// <summary>Hold the game, let it run or read it, as Esc and Continue do (craft.menu.pause).</summary>
    private string PauseReadout(string state)
    {
        switch (state)
        {
            case "on": SetPaused(true); break;
            case "off": SetPaused(false); break;
            case "read": break;
            default: return "refused: on, off or read";
        }

        return FormattableString.Invariant($"paused={paused} quitAvailable={QuitAvailable}");
    }

    /// <summary>Whether the controller's menu button was pressed this update.</summary>
    private static bool MenuButtonPressed(ProductUpdate update)
    {
        foreach (ProductInputEvent input in update.Input)
        {
            if (input.Kind == InputEventKind.ControllerButton && input.ControllerButton == MenuButton && input.Edge == InputEdge.Pressed) return true;
        }

        return false;
    }
}
