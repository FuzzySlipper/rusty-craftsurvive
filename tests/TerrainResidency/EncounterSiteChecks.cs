using System.Buffers.Binary;
using System.Security.Cryptography;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Manipulation;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

/// <summary>Encounter placement against generated terrain: walkers stay out of water, swimmers only where a shore is reachable.</summary>
internal static class EncounterSiteChecks
{
    internal static void Run()
    {
        // Encounters against the real world: the water invariant must hold on generated
        // terrain, not only on hypothetical sites.
        {
            TerrainConfiguration configuration = TerrainConfiguration.Default;
            TerrainRecipe encounterRecipe = configuration.CreateRecipe(new TestDraws(configuration.Seed));
            TerrainEncounterFacts facts = new(encounterRecipe);
            long waterX = 0, waterZ = 0, landX = 0, landZ = 0;
            bool foundWater = false, foundLand = false;
            long scanStep = Math.Max(1, (long)encounterRecipe.Map.Spacing / 2);
            for (long x = -encounterRecipe.Radius + scanStep; x < encounterRecipe.Radius && !(foundWater && foundLand); x += scanStep)
            {
                for (long z = -encounterRecipe.Radius + scanStep; z < encounterRecipe.Radius && !(foundWater && foundLand); z += scanStep)
                {
                    long surface = encounterRecipe.SurfaceAt(x, z), waterTop = encounterRecipe.WaterTopAt(x, z);
                    if (!foundWater && surface < waterTop) { waterX = x; waterZ = z; foundWater = true; }
                    if (!foundLand && surface > waterTop) { landX = x; landZ = z; foundLand = true; }
                }
            }

            Check.That(foundWater, "the generated map contained no water column to place against");
            Check.That(foundLand, "the generated map contained no land column to place against");

            Check.That(facts.TryDescribe(RegionKind.Wilderness, 1, landX, landZ, out EncounterSite landSite),
                "a land column inside the world must be describable");
            Check.That(landSite.SurfaceY > landSite.WaterLevel, "a land column must describe ground above the water line");
            Check.That(SpawnRules.Evaluate(landSite.ToSpawnSite(), CreatureTraits.Walker).Allowed,
                "a walker must be allowed on real land");

            Check.That(facts.TryDescribe(RegionKind.Wilderness, 1, waterX, waterZ, out EncounterSite waterSite),
                "a water column inside the world must be describable");
            SpawnVerdict walkerInWater = SpawnRules.Evaluate(waterSite.ToSpawnSite(), CreatureTraits.Walker);
            Check.That(!walkerInWater.Allowed && walkerInWater.Refusal == SpawnRefusal.SubmergedWithoutSwimming,
                $"a walker was allowed into real water at ({waterX}, {waterZ}): {walkerInWater.Reason}");

            // The provider's shore claim must agree with an independent scan of the same radius.
            bool shoreWithinReach = false;
            for (long offsetX = -TerrainEncounterFacts.ShoreReachRadius; offsetX <= TerrainEncounterFacts.ShoreReachRadius && !shoreWithinReach; offsetX += 4)
            {
                for (long offsetZ = -TerrainEncounterFacts.ShoreReachRadius; offsetZ <= TerrainEncounterFacts.ShoreReachRadius && !shoreWithinReach; offsetZ += 4)
                {
                    long candidateX = waterX + offsetX;
                    long candidateZ = waterZ + offsetZ;
                    if (facts.IsInside(candidateX, candidateZ)
                        && encounterRecipe.SurfaceAt(candidateX, candidateZ) >= encounterRecipe.WaterTopAt(candidateX, candidateZ))
                    {
                        shoreWithinReach = true;
                    }
                }
            }

            Check.That(waterSite.ShoreIsReachable == shoreWithinReach,
                "the shore reachability the site reports must match the terrain around it");
            SpawnVerdict swimmer = SpawnRules.Evaluate(waterSite.ToSpawnSite(), CreatureTraits.Amphibious);
            Check.That(swimmer.Allowed == waterSite.ShoreIsReachable,
                "a swimmer may only be placed in water it can leave");

            // A river above the sea is water too: its own water line refuses a walker.
            RiverPoint river = encounterRecipe.Map.Rivers.Reaches.SelectMany(r => r.Skip(r.Length / 3).Take(r.Length / 3))
                .Where(p => p.Surface > GenerationConstants.WaterLevel + 2).MaxBy(p => p.HalfWidth);
            Check.That(facts.TryDescribe(RegionKind.Wilderness, 1, (long)river.X, (long)river.Z, out EncounterSite riverSite)
                && riverSite.WaterLevel > GenerationConstants.WaterLevel && riverSite.SurfaceY < riverSite.WaterLevel
                && !SpawnRules.Evaluate(riverSite.ToSpawnSite(), CreatureTraits.Walker).Allowed,
                "a walker is refused in an inland river above the sea");

            Check.That(!facts.TryDescribe(RegionKind.Wilderness, 1, TerrainConstants.DefaultSize, 0, out _),
                "a column outside the finite world must be refused");
            Console.WriteLine(
                $"Encounters on generated terrain: land at ({landX}, {landZ}) surface {landSite.SurfaceY}, water at ({waterX}, {waterZ}) surface {waterSite.SurfaceY}, " +
                $"water level {waterSite.WaterLevel}, river water level {riverSite.WaterLevel}, shore reachable {waterSite.ShoreIsReachable}; walkers refused in water, swimmers allowed only with a shore");
        }
    }
}
