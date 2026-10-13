using System.Numerics;
using CraftSurvive.Game.Modules.Sky;
using CraftSurvive.Game.Tests;

/// <summary>
/// The scene wind the plants sway in (owner 2026-10-13): a lower range than before, from a light breeze in
/// still weather to a gale's, and over the seconds slow lulls, gusts and veering rather than a steady push.
/// </summary>
internal static class WindSwayChecks
{
    internal static void Run()
    {
        Check.That(WindSway.CalmStrength < 1 && WindSway.GaleStrength < 2 && WindSway.CalmStrength < WindSway.GaleStrength,
            "the sway runs from a light breeze to a gale, below the old range (1 in calm, 4 in a gale)");
        Check.That(WindSway.Gust(0) < WindSway.Gust(1) && WindSway.Gust(1) <= 1, "a stronger wind gusts more of its lean");

        Vector2 flow = Vector2.Normalize(new(1, 0.35f));
        float least = float.MaxValue, most = 0;
        double widestVeer = 0;
        for (double t = 0; t < 600; t += 0.5)
        {
            float strength = WindSway.Strength(0.4f, t);
            least = Math.Min(least, strength);
            most = Math.Max(most, strength);
            Vector2 toward = WindSway.Direction(flow, t);
            widestVeer = Math.Max(widestVeer, Math.Abs(Math.Atan2((flow.X * toward.Y) - (flow.Y * toward.X), Vector2.Dot(flow, toward))) * 180 / Math.PI);
        }

        float steady = WindSway.CalmStrength + ((WindSway.GaleStrength - WindSway.CalmStrength) * 0.4f);
        Console.WriteLine($"scene wind at a rain front's 0.4: {least:F2} to {most:F2} (steady {steady:F2}); veers up to {widestVeer:F1} deg");
        Check.That(least >= steady * (1 - WindSway.Swing) - 1e-4 && most <= steady * (1 + WindSway.Swing) + 1e-4
            && most - least > steady * WindSway.Swing, "the wind rises and falls in lulls and gusts, within its swing");
        Check.That(widestVeer > WindSway.VeerDegrees / 2 && widestVeer <= WindSway.VeerDegrees + 1e-6, "and veers a little about the weather's flow");
        Check.That(WindSway.Strength(0.4f, 12.5) == WindSway.Strength(0.4f, 12.5), "the same moment gives the same wind");
    }
}
