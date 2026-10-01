namespace CraftSurvive.Game.Modules.Survival;

/// <summary>How hard the world is on the player. Stored by value: append only, never renumber.</summary>
internal enum Difficulty
{
    /// <summary>Hunger and air never hurt, food lasts twice as long, and health returns faster.</summary>
    Gentle = 0,

    Normal = 1,

    /// <summary>Food and air run out faster, starving and drowning hurt sooner, and health returns slower.</summary>
    Harsh = 2,
}
