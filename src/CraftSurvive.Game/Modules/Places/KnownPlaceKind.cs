namespace CraftSurvive.Game.Modules.Places;

/// <summary>How the expedition knows a place: home, the sled, a dungeon entrance, a settlement (#9817, #9875: port, river town or hold), or a site by how well it is known.</summary>
internal enum KnownPlaceKind { Home, Visited, Seen, Entrance, Sled, Town }
