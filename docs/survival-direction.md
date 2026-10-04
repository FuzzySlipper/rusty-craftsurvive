# CraftSurvive: scavenging, expeditions, and a home worth returning to

CraftSurvive is an exploration and scavenging adventure in a vast, beautiful,
dangerous fantasy landscape. The player searches for exposed remnants of an
ancient world, enters buried dungeons, and brings back materials, discoveries,
and magically trapped people. These finds grow a player-built home whose
residents and services support further expeditions.

This is the durable gameplay direction, not a statement that these systems are
implemented. [csharp-migration-map.md](csharp-migration-map.md) describes product
owners; [known-limitations.md](known-limitations.md) describes implementation
limits. Work state, unresolved design discussion, and implementation sequencing
belong in Den project `rusty-craftsurvive`.

The principal section numbers (2, 6, 8, and 10) retain their subjects for existing
references. The preceding design is preserved in Den under
`history/survival-direction-adventurer-baseline`; earlier assessment and module
surveys remain under `history/survival-direction-campaign-record` and
`history/survival-direction-module-survey`. Historical requirements are not an
additional gameplay checklist.

## 2. Gameplay identity and design target

### The player fantasy

Be an adventurer who can read a hostile landscape, find what it has exposed,
brave the places beneath it, and bring something worthwhile home. Over time,
a shelter becomes an inhabited base: a visible expression of the journeys taken
and a practical foundation for journeys still to come.

The game's defining verbs are **prepare, roam, discover, descend, scavenge,
rescue, return, and build**. Resource acquisition centers on finding and
recovering things. There is no general block-by-block mining progression.
Building is a major part of progression, alongside exploration and dungeon
adventure.

### Design pillars

| Pillar | What it means for play |
| --- | --- |
| Beautiful danger | The landscape invites exploration through scale, atmosphere, and landmarks while climate and exposure make travel consequential. |
| Discovery supplies progress | Useful resources and opportunities come from places worth seeking out. Repeatedly excavating ordinary terrain is not the economic foundation. |
| Two kinds of expedition pressure | The open world emphasizes environmental danger and weather; dungeons concentrate combat, climbing, and close exploration. Neither space needs to be exclusively one kind of challenge. |
| A useful, inhabited home | Salvage becomes buildings and capabilities. Rescued residents occupy rooms and provide services or bonuses. |
| Ancient mystery | Buried magic and technology suggest a remote past whose relationship to the living world remains partly unknown. |

### Setting and technology

The living world is fantasy dominated by magic, with an early-modern level of
technology in which gunpowder exists and is common. Ancient magic and technology
lie deep beneath the landscape. Their remnants are encountered where erosion,
caves, canyons, or other openings have exposed a way in.

Breath of the Wild is a reference for the feeling of very long-lost technology
within a fantasy world. It does not prescribe CraftSurvive's ancient civilization,
its visual motifs, or an explanation of how magic and machinery relate. The exact
technology mix and lore remain deliberately unspecified. Common gunpowder is
part of the setting; it does not yet settle the player's weapon roster or combat
balance.

### The expedition loop

1. **Prepare at home.** Use the base's available storage, facilities, and resident
   services to prepare for the intended journey and its conditions.
2. **Travel and discover.** Undertake an overland journey, read landmarks and
   weather, and explore sites locally to find entrances to the buried world.
3. **Enter a dungeon.** Transition into a distinct interior, explore its routes,
   climb through its vertical spaces, and face its encounters.
4. **Recover what matters.** Gather useful salvage and discoveries, including
   people held in a portable magical confinement.
5. **Extract and return.** Bring finds out of the dungeon and home from the
   expedition. Finding something, getting it topside, and getting it home are
   distinct parts of the journey, with their detailed logistics still to be designed.
6. **Build and restore.** Improve the base, prepare rooms, and release rescued
   people to live there and contribute services or bonuses.
7. **Venture farther.** Use the resulting capabilities to undertake further
   exploration and more demanding expeditions.

This is the main progression loop, not a rule that every outing must contain
all seven steps. Reconnaissance, a short salvage trip, or building at home can
still be worthwhile play.

### World scale and generation

The world is large and finite. Streaming and bounded residency are necessary,
but no square-kilometre target defines the design. Neither infinite generation
nor continuous first-person traversal of the entire world map is required.

Generation separates a discrete world-map step at new-game creation from local
voxel terrain, with RimWorld as a reference for that separation of scales. The
map establishes broad geography and regional relationships; local terrain
realizes a place in detail. The prototype uses a bounded square map, described
in [the product map](csharp-migration-map.md#world-map-contract). This does not
settle the final topology, generation algorithm, travel mode, or promise that
every map location will support entry into a first-person scene.

Generation should separate **geographic structure from local surface detail**.
The map stage establishes coherent landforms and relationships: ridgelines,
basins, drainage, passes, and regional climate and geology. Erosion and drainage
simulation are candidate tools for that stage, not committed algorithms.
Local terrain samples that geography as its anchor, then adds smaller rock
formations, gullies, and surface relief before producing the voxel density field.
Detail uses continuous world coordinates so chunk boundaries do not become
geographic boundaries.

Several independently weighted noise banks can contribute at different scales;
each bank may itself use fBm. Their amplitude, frequency, shape, and geographic
masks matter more than simply increasing octave count or voxel resolution.
A sheltered valley floor should not receive the same relief as exposed rock.
Local variation must preserve the map's important connections and landmarks,
including drainage and navigable passes. The prototype bounds local relief,
preserves closed drainage basins, and
suppresses noise on incised channel floors and protected passes. A complete
travel-route network and guaranteed connectivity between destinations remain
separate design work. Authored terrain studies remain
comparisons for the smaller-scale treatment.

Regional scale should allow tundra, deep desert, and other environments to have
credible separation. Biome variety should not depend on squeezing radically
different climates next to one another within a short walk.

### Overland travel and local exploration

A separate overland travel mode is under consideration, with Mount & Blade as
a reference for moving through a world at a larger scale. It could present the
world map as a coarse terrain mesh, while local exploration, home building, and
dungeon dives use detailed first-person spaces. How and where the player moves
between these scales remains open.

In this concept, substantial survival simulation concentrates on the journey:
route, weather, supplies, time, and carrying capacity. The aim is meaningful
expedition decisions at a scale that suits them, rather than a fast-ticking
survival clock while the player walks around in first person. Local weather and
immediate environmental danger still matter; the distinction is where sustained
travel logistics and resource consumption are focused. Exact time advancement
and the relationship between modes are not yet decided.

Travel may involve a vehicle, beast of burden, sled, or another transport form.
The important proposed distinction is between what a person can carry into a
site and what their overland transport can hold. Both capacities remain finite;
transport expands the expedition's reach without becoming unlimited storage.
No particular conveyance, fuel, feeding, maintenance, or driving system is
implied by this concept.

### The open world: travel, climate, and discovery

The world should feel large enough that a journey is an undertaking. Frozen
tundra and deep deserts express the intended combination of severity and awe;
they are reference environments, not an exhaustive biome list or a content quota.

Environmental and climatic dangers provide much of the open world's pressure.
Weather is a gameplay system: snowstorms, sandstorms, and other regional weather
can roll in and change the conditions of an expedition. Their purpose is to
create decisions about routes, shelter, preparation, and whether to continue.
Weather should be perceptible in the world and allow meaningful responses;
an unexplained damage timer alone does not fulfill that purpose.

The landscape also needs stretches in which the player can look, navigate,
anticipate, and appreciate the place. Constant combat or uninterrupted survival
maintenance would crowd out that experience. Open-world encounters remain
possible, but the bulk of combat belongs in dungeons.

Discovery follows the land. A canyon face, cave mouth, or exposed fragment of
ancient construction gives the player a reason to investigate. The game should
reward finding an opening into the buried world rather than digging arbitrary
shafts until a dungeon is reached.

### Dungeons: descent, combat, and recovery

Dungeons are distinct spaces reached from discovered entrances through a load
transition. They are the primary setting for combat and concentrated scavenging.
Their layouts also make climbing and spatial exploration central: shafts,
ledges, stacked chambers, bridges, stairs, and drops give a descent a physical
shape and make routes more than corridors between fights.

An entrance belongs to a place in the landscape, and leaving the dungeon returns
the player to that wider journey. Interior exploration should make valuable
finds feel situated in a buried place, rather than supplied by an abstract reward
screen. Dungeon identity rests on the combination of place, traversal,
encounters, and things worth bringing back.

### Dungeon extraction and supply caches

The working expedition concept gives dungeon finds a journey back to the
surface. Pushing farther in can make ferrying salvage out more demanding and
may attract additional dangers. Depth and the commitment to keep exploring
should create decisions about recovery, not merely increase the distance of
repeated uneventful walks.

One candidate is a deployable supply cache that provides a magical or ancient
technological teleport connection to the overland transport. Deployment could
consume resources, and the cache or its use could attract enemies. This would
let the player invest in an extraction point while creating a new source of
risk. Caches, their costs, and enemy attraction are possibilities, not committed
features or a requirement for timed defense encounters.

The connection's payload is unresolved: cargo, the player, rescued people, or
some combination. Neither a cache nor a teleport implies delivery directly to
the home base or bypassing transport capacity. Range, persistence, placement,
activation, and how danger responds all need further design. The intended
question is whether to push deeper, secure a way to recover more, or return
with what the expedition already holds.

### Scavenging and the building economy

Scavenging yields an abstract building resource or currency. A magical or
technological building implement turns that resource into the chosen
construction form. A wooden door and a stone wall do not require separate wood
and stone gathering chains simply because they look like different materials.
The currency and implement are functional descriptions, not settled lore names.

This connects expeditions directly to architectural freedom: recover useful
scavenge, then spend it on the home the player wants to build. Construction
forms may have different costs, but choosing an appearance does not by itself
create a separate raw-material economy. Exact yields, conversion rules, costs,
and any unlocks remain to be designed.

The abstraction applies to building supplies; it does not automatically turn
equipment, expedition provisions, discoveries, or rescued residents into one
universal currency. Recovery and hauling must remain meaningful: conversion
must not let the player effortlessly sweep a location into a building balance
and bypass the journey out. Exact conversion location, timing, weight, and
volume rules remain open, but convenient abstraction at the building stage
cannot erase carrying and extraction limits during an expedition.

Terrain manipulation and building are separate activities. The player can place
construction precisely; adventuring terrain changes can remain occasional,
indirect actions such as charges. The absence of mining does not remove the
ability to build or make targeted changes to the world.

### Terrain and construction form

The wider world's terrain direction is **dual contouring (DC), not exposed
cubic terrain**. It should have smoother, stylized landforms in the same broad
spirit as the dungeons. Valheim is a useful reference for the terrain's level of
stylization; realistic AAA detail is not the target. See
[visual-direction.md](visual-direction.md) for the retro surface treatment,
atmosphere, and mood guidance.

Building may retain blocks, a grid, or discrete construction pieces because
they make assembling a home convenient and precise. The exact building system
is open. Cubic assembly is a usability option for construction, not a constraint
on the landscape's visible shape. Building should not require fiddly freeform
voxel sculpting merely because terrain uses DC.

### The base: construction with a gameplay purpose

The player establishes a home in the world and builds it up with expedition
finds. Terraria is the reference for a base whose rooms and inhabitants matter
to progression as well as appearance. Construction should give the player
practical reasons to expand and arrange the home, while leaving room for
personal expression.

Rooms make space for rescued residents. Residents live in those spaces, move
around locally, and offer services or bonuses. Their contributions should make
bringing someone home and providing a room a tangible improvement to the base
and to future adventuring.

This is a small inhabited home, not a colony simulation. The direction does not
require worker assignments, production logistics, population management, or
complex autonomous schedules. The exact housing rules and service roster need
separate design; Terraria is a reference for the relationship between building
and residents, not a specification to reproduce all of its systems.

### Rescuing residents

A prospective resident is discovered during exploration as a person trapped in
some form of magical confinement. That confinement can be collected and carried
home. At the base, the player opens or releases it and the rescued person takes
an available room, becoming an inhabitant who provides a service or bonus.

The core sequence is **discover a trapped person → recover and carry them home
→ provide a room → release and house them → gain their contribution**.
Building the room can happen before the expedition or after returning; the key
relationship is that the rescue and the housing both contribute to progression.

The confinement's fictional form is intentionally unnamed. This establishes
rescue as a find that can be brought home, without deciding who trapped these
people, when they were trapped, or how their magic works. It does not imply an
escort mission, recruitment negotiation, or colony labor system. Identity,
duplicates, carrying rules, and what happens when no room is available remain
separate design decisions.

### Intended rhythm and presentation

The emotional rhythm moves between awe and exposure outside, tension and
discovery below ground, and relief and accomplishment at home. The home grows
more inhabited as the player's knowledge of the world grows.

Visual direction must support that rhythm: beautiful landscapes can be deadly,
weather must read as a changing condition, exposed ancient places must invite
investigation, and the base must feel like somewhere worth returning to. These
are gameplay-facing requirements. Smooth, stylized DC terrain is a firm part
of the direction. [visual-direction.md](visual-direction.md) establishes the
restrained retro rendering and dreamy, sometimes harsh mood. Exact palettes,
shape language, and the appearance of ancient technology remain open.

### Design acceptance

A representative complete expedition should demonstrate that:

- Landscape features draw the player toward a discoverable buried site.
- Environmental conditions or weather create an understandable travel decision.
- The dungeon offers combat, vertical traversal, and worthwhile recovery.
- Salvage from exploration supports a meaningful improvement to the base.
- A magically confined person can be recovered, carried home, released, and
  housed, with a visible service or bonus resulting from their presence.
- The improved home gives the player a reason or new capability to head out again.
- This progression works without a general mining loop or colony micromanagement.

These are design properties for the connected experience, not claims about a
finished build or mandatory acceptance criteria for every individual task.

## 6. Technical foundations and constraints

These foundations bound implementation; they do not define the whole game's
content budget or establish that the gameplay above is already available.

| Foundation | Constraint | Design consequence |
| --- | --- | --- |
| Manipulation | Place blocks and detonate charges; no general break-and-collect | Construction and deliberate adventuring edits remain distinct from mining. |
| World extent | Large and finite; no fixed area target | Streaming and bounded residency remain necessary. Map scale, local extent, and border presentation must follow the travel design rather than a nominal area. |
| Development save policy | Version the envelope, detect incompatible worlds explicitly, discard and regenerate, retain one previous backup | Development saves are disposable. This is not a gameplay death penalty or a promise about release save compatibility. |
| Surface and building | Smooth, stylized DC terrain; convenient, precise building with blocks or pieces still an option | The terrain direction is settled; construction interaction remains open. Implementation limits are recorded separately. |
| Water and movement | Static water with Engine swim mechanisms | The product owns water placement, breath, and drowning policy. Flowing water is not assumed. |

Multiplayer remains outside the initial scope. Content and subsystem choices
follow the expedition and inhabited-base design rather than a genre checklist.

### 6.1 Implementation contracts

The product uses Engine-owned meshing, collision, navigation, residency, and
presentation. A terrain surface must agree with the geometry the player and
creatures move over; changing its appearance alone is insufficient. Building,
terrain edits, and dungeon transitions must preserve that agreement.

Water's presentation and movement behaviour are separate concerns and both need
verification. Navigation is explicitly published through the Engine, and its
coordinates follow the installed SDK contract. Product generation and residency
work must remain bounded as the player travels.

The module map and [known limitations](known-limitations.md) describe supported
implementation paths and their constraints. [Live proofs](live-proofs.md) describes
how to verify them. Historical substrate measurements are evidence, not a
permanent restriction on the game's materials or terrain style.


## 8. Ownership and scope boundaries

**The product decides. The Engine guarantees.** C# owns the terrain recipe,
expedition rules, weather and exposure policy, encounters, salvage economy,
building and housing rules, resident services, and persistence meaning. The
Engine owns lifecycle, input delivery, rendering and frame construction, spatial
mechanisms, and content and persistence primitives exposed through its safe SDK.
Naming a desired gameplay system here is not a claim that its required Engine
mechanisms are available.

Use one explicit owner for each mutable state family and thin coordination
through Read → Decide → Apply → Publish where useful. The DOM companion presents
product state and accepts intent; gameplay state and decisions stay in C#.

A second renderer, custom transport, downstream P/Invoke, unsafe game code,
UI-owned gameplay state, or a replacement product runtime is out of bounds.
If a needed mechanism cannot be expressed through the installed SDK, identify
the upstream capability and stop that implementation slice rather than
recreating it downstream.

Retired Courtyard, Stoneworks, and procgen workbench experiments remain semantic
or content-authoring references in Den history. They do not return as parallel
product runtimes.

## 10. Applying the direction

Evaluate additions by their contribution to the connected experience:
**exploration leads to recovery; recovery builds an inhabited home; that home
supports further exploration.**

World generation should create compelling journeys and discoverable openings.
Weather should change travel decisions. Dungeon work should strengthen combat,
traversal, and discovery together. Item and crafting work should connect finds
to useful preparation and construction. Base work should make housing and
rescued residents matter alongside architectural freedom.

Develop the wider terrain toward smooth, stylized DC surfaces while keeping
construction precise and usable. Treat travel mode, transport capacity, and
extraction caches as connected design hypotheses;
do not turn them into implementation mandates before their rules are settled.
Occasional blasts may use bounded edits and presentation that covers their
update cost; their infrequency does not remove the need to measure that cost.
World streaming, residency, navigation, and frame budgets remain engineering
requirements regardless of the economic shift away from mining.

Do not infer hunger timers, durability ladders, escalating enemy tiers, base
raids, automation, or full colony systems solely from the survival/building genre.
A proposal for any such system needs a clear role in this game's experience.
Technical prototypes and small content baselines are means of establishing
capabilities, not substitutes for the gameplay identity above.
