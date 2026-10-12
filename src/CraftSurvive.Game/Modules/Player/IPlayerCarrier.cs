using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>
/// Something the player can ride (#9879): a product-moved collider the character controller stands
/// them on and carries (the Engine's platform carry over a borrowed <see cref="CharacterObstacle"/>).
/// The player controller steps it with each controller step, so the carry and the motion agree.
/// </summary>
internal interface IPlayerCarrier
{
    /// <summary>The carrier's stable entity identity, as the controller reports its platform.</summary>
    ulong Entity { get; }

    /// <summary>Seated aboard: the movement keys paddle and steer it, not the body.</summary>
    bool Seated { get; }

    /// <summary>
    /// The board/stand action: from beside it, boards and sits (a world point to stand the player at);
    /// seated, stands up (null, and the player is free to walk or step off).
    /// Returns false when the action is not the carrier's to take (it is too far away).
    /// </summary>
    bool TryToggle(Vector3 playerLocal, out Vector3? boardAt);

    /// <summary>
    /// One controller step: moves it (the paddle is the planar intent while seated - forward drives along the
    /// bow, sideways turns it - zero otherwise; aboard is whether the controller stood the player on it last) and
    /// returns its collider for this step in the local frame, or null when it is not near enough to matter.
    /// </summary>
    CharacterObstacle? Step(float seconds, Vector2 paddle, Vector3 playerLocal, bool aboard);
}
