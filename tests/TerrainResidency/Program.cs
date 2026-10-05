using CraftSurvive.Game.Tests;

// Terrain, generation, residency, edits and player input: the pure product policy the world is
// built from, checked without an Engine context. Each area reports every failure it finds.
Check.Section("player input", PlayerInputChecks.Run);
Check.Section("world map", WorldMapChecks.Run);
Check.Section("erosion and rivers", ErosionChecks.Run);
Check.Section("regional terrain", RegionalTerrainChecks.Run);
Check.Section("generator identity", GeneratorChecks.Run);
Check.Section("overlay", OverlayChecks.Run);
Check.Section("chunk cache", ChunkCacheChecks.Run);
Check.Section("generation", GenerationChecks.Run);
Check.Section("continuous density", DensityChecks.Run);
Check.Section("edits", EditChecks.Run);
Check.Section("residency", ResidencyChecks.Run);
Check.Section("encounter sites", EncounterSiteChecks.Run);
Check.Section("content and position", ContentChecks.Run);
Check.Section("UI actions", ActionChecks.Run);
Check.Section("Inventory and crafting", InventoryChecks.Run);
return Check.Finish("TerrainResidency");
