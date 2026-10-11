using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Content;

/// <summary>
/// The product's one colour rule (#9844): colours are authored in sRGB (palettes, hex codes and
/// hand-tuned constants, as a colour picker shows them), and every colour the Engine draws in the world
/// is linear (rusty-engine <c>docs/csharp-lifecycle.md#colours</c>): material colour, tint and
/// emission, primitives, mesh and heightfield vertex colours, lights, fog and particle colour keys.
/// So each authored colour is decoded once, where it is handed to the Engine, and a material takes its
/// colour once with a white tint (the Engine multiplies colour by tint). Billboards, labels and meters
/// take sRGB as authored.
/// </summary>
internal static class ColourSpace
{
    /// <summary>An authored sRGB colour as the Engine draws it: linear, alpha unchanged.</summary>
    internal static Color Linear(this Color srgb) => Color.FromSrgb(srgb.R, srgb.G, srgb.B, srgb.A);

    /// <summary>The white a material's tint takes when its colour is given once.</summary>
    internal static readonly Color White = new(1, 1, 1, 1);
}
