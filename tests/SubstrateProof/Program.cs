using Rusty.Engine.Entities;
using Rusty.Engine.Mechanics;
using Rusty.Engine.StateMachine;

// Staged proof for campaign #8595 slice #8596 (S0): can this product admit the
// Engine's gameplay substrate at all? The product has never constructed an
// InventoryStore, a StatsComponent, an EntityStore, or a StateMachine, so the
// integration cost that Slices 4 and 7 depend on is currently an assumption.
//
// This program uses managed SDK values only. It launches no runtime, creates no
// spatial session, and retains no pointer, so it belongs in the ordinary
// managed lane beside the other focused checks. The live-host half of the
// substrate proof — navigation against a real session, entity graphics
// projection, and water material behaviour — is proved in the product lane.

const uint ProofComponentLocalId = 9001U;

List<string> failures = [];
List<string> observations = [];

void Require(bool condition, string message)
{
    if (!condition) failures.Add(message);
}

void Observe(string message) => observations.Add(message);

ulong Quantity(InventoryStore store, EntityId who, InventoryStackId stackId)
{
    foreach (InventoryStack stack in store.View(who).Stacks)
        if (stack.Id.Equals(stackId))
            return stack.Quantity;
    return 0UL;
}

Console.WriteLine("CraftSurvive substrate proof (campaign #8595 S0) — managed SDK values only");
Observe($"SDK assembly version: {typeof(InventoryStore).Assembly.GetName().Version}");

// ------------------------------------------------- 1. items, stacks, capacity
InventoryStore inventory = new();
EntityId owner = new(1UL);
EntityId receiver = new(2UL);
CapacityMetricId slots = CapacityMetricId.Parse("proof.slots");
InventoryStackId main = InventoryStackId.Parse("main");
InventoryStackId spill = InventoryStackId.Parse("spill");
InventoryStackId delivery = InventoryStackId.Parse("delivery");
ItemDefinitionId stoneId = ItemDefinitionId.Parse("proof.stone");

ItemDefinition stone = new(
    stoneId,
    ItemKind.Fungible,
    64UL,
    [],
    [new ItemCapacityCost(slots, 1UL)],
    null,
    []);

inventory.RegisterInventory(new InventoryState(owner, [new InventoryCapacityLimit(slots, 64UL)]));
inventory.RegisterInventory(new InventoryState(receiver, [new InventoryCapacityLimit(slots, 64UL)]));
Require(inventory.InventoryOwners.Count == 2, "the store does not list both registered inventories");

InventoryMutationReceipt granted = inventory.Grant(owner, stone, main, 10UL);
Require(granted.AfterQuantity == 10UL, $"grant reported {granted.AfterQuantity}, expected 10");
Require(
    granted.InventoryRevisionAfter > granted.InventoryRevisionBefore,
    "grant did not advance the inventory revision");
Require(Quantity(inventory, owner, main) == 10UL, "grant is not visible in the owner's view");

InventoryMutationReceipt consumed = inventory.Consume(owner, main, 4UL);
Require(consumed.AfterQuantity == 6UL, $"consume reported {consumed.AfterQuantity}, expected 6");
Require(Quantity(inventory, owner, main) == 6UL, "consume is not visible in the owner's view");

ulong quantityBeforeRefusal = Quantity(inventory, owner, main);
ulong revisionBeforeRefusal = inventory.View(owner).InventoryRevision;
string refusalSurface = "returned without throwing";
try
{
    inventory.Consume(owner, main, 999UL);
}
catch (Exception exception)
{
    refusalSurface = exception.GetType().Name;
}
Require(Quantity(inventory, owner, main) == quantityBeforeRefusal, "a refused consume changed the stack");
Require(
    inventory.View(owner).InventoryRevision == revisionBeforeRefusal,
    "a refused consume advanced the inventory revision");
Observe($"insufficient consume surfaced as: {refusalSurface}");

// A capacity maximum is a hard limit, and refusing must not leave a partial
// stack behind: crafting and loot both depend on that atomicity.
string capacitySurface = "returned without throwing";
try
{
    inventory.Grant(owner, stone, spill, 1000UL);
}
catch (Exception exception)
{
    capacitySurface = exception.GetType().Name;
}
Require(
    Quantity(inventory, owner, spill) == 0UL,
    "an over-capacity grant left a partial stack behind");
Observe($"an over-capacity grant surfaced as: {capacitySurface}");

inventory.SplitFungible(owner, main, spill, 3UL);
Require(
    Quantity(inventory, owner, main) == 3UL && Quantity(inventory, owner, spill) == 3UL,
    $"split left main={Quantity(inventory, owner, main)} spill={Quantity(inventory, owner, spill)}, expected 3/3");

inventory.MergeFungible(owner, spill, main);
Require(
    Quantity(inventory, owner, main) == 6UL && Quantity(inventory, owner, spill) == 0UL,
    $"merge left main={Quantity(inventory, owner, main)} spill={Quantity(inventory, owner, spill)}, expected 6/0");

inventory.TransferFungible(owner, receiver, main, delivery, 2UL);
Require(
    Quantity(inventory, owner, main) == 4UL && Quantity(inventory, receiver, delivery) == 2UL,
    $"transfer left owner={Quantity(inventory, owner, main)} receiver={Quantity(inventory, receiver, delivery)}, expected 4/2");

InventoryView view = inventory.View(owner);
Require(view.Capacity.Count > 0, "the owner's view reports no capacity usage");
Observe($"owner view reports {view.Stacks.Count} stack(s) and store revision {view.StoreRevision}");

// ------------------------------------------------------- 2. stats and tracks
// A track's maximum must be a Stat owned by the same component: capturing a
// component whose track points at a foreign maximum is refused, so a product
// cannot model "breath: 20" as a bare number and still persist it.
StatsComponent stats = new();
StatId healthId = StatId.Parse("proof.health");
StatId breathMaximumId = StatId.Parse("proof.breath.maximum");
TrackId breathId = TrackId.Parse("proof.breath");
stats.AddStat(healthId, new Stat(20d, 0d, 20d, 1d, MidpointRounding.ToZero, MidpointRounding.ToZero));
stats.AddStat(breathMaximumId, new Stat(20d, 0d, 20d, 1d, MidpointRounding.ToZero, MidpointRounding.ToZero));
stats.AddTrack(
    breathId,
    new Track(
        stats.GetStat(breathMaximumId),
        20d,
        0d,
        TrackMaximumChangePolicy.PreserveMissingAmount,
        1d,
        MidpointRounding.ToZero,
        MidpointRounding.ToZero));

double spent = stats.GetTrack(breathId).Spend(5d);
Require(
    stats.GetTrack(breathId).Current == 15d,
    $"spend left {stats.GetTrack(breathId).Current}, expected 15");
Observe($"Track.Spend(5) returned {spent}");

bool overspent = stats.GetTrack(breathId).TrySpend(100d);
Require(!overspent, "TrySpend succeeded with insufficient current value");
Require(stats.GetTrack(breathId).Current == 15d, "a refused TrySpend changed the current value");

double restored = stats.GetTrack(breathId).Restore(100d);
Require(
    stats.GetTrack(breathId).Current == 20d,
    $"restore left {stats.GetTrack(breathId).Current}, expected a clamp at the maximum");
Observe($"Track.Restore(100) returned {restored} and clamped at {stats.GetTrack(breathId).Current}");

StatsComponentSnapshot? snapshot = StatsComponentCapture.Capture(stats);
Require(snapshot is not null, "stats capture returned no snapshot");
if (snapshot is not null)
{
    StatsComponent rebuilt = StatsComponentCapture.Rebuild(snapshot, (_, _, _) => { });
    Require(rebuilt.GetTrack(breathId).Current == 20d, "the rebuilt component lost the track value");
    Require(
        rebuilt.TryGetStat(healthId, out Stat? rebuiltHealth) && rebuiltHealth is not null && rebuiltHealth.BaseValue == 20d,
        "the rebuilt component lost the stat");
    Observe("stats capture/rebuild round-trips a stat and a track");
}

// ---------------------------------------------------------------- 3. entities
ComponentType<ProofRuntime> runtimeComponent =
    ComponentType<ProofRuntime>.Create(ProductComponentKeys.Create(ProofComponentLocalId));
EntityStore entities = new([runtimeComponent]);
EntityId subject = entities.Create(EntityLifecycle.Active);
entities.Add(subject, new ProofRuntime(7));
Require(entities.Has(subject, runtimeComponent), "the entity does not report the added component");
Require(entities.Get(subject, runtimeComponent).Value == 7, "the component value did not round-trip");
Require(
    entities.Query<ProofRuntime>(false).Count == 1,
    "the component query did not return the created entity");
Require(entities.IsAlive(subject), "a created entity does not report alive");

EntityId batched = entities.Create(EntityLifecycle.Active);
ulong staleRevision = entities.Revision;
EntityBatch batch = new EntityBatch().Set(batched, runtimeComponent, new ProofRuntime(9), null);
entities.Commit(batch, entities.Revision);
Require(
    entities.Get(batched, runtimeComponent).Value == 9,
    "a batched component set did not commit");

string staleSurface = "returned without throwing";
try
{
    entities.Commit(batch, staleRevision);
}
catch (Exception exception)
{
    staleSurface = exception.GetType().Name;
}
Require(entities.IsAlive(batched), "a stale batch commit damaged existing state");
Observe($"a stale batch revision surfaced as: {staleSurface}");

EntityRevision revision = entities.GetEntityRevision(subject);
entities.Destroy(subject, revision);
Require(!entities.IsAlive(subject), "a destroyed entity still reports alive");

// ----------------------------------------------------------- 4. state machine
StateMachineDefinition machine = new(
    1UL,
    [0UL, 1UL, 2UL],
    [new StateMachineTransition(0UL, 1UL), new StateMachineTransition(1UL, 2UL)]);
Require(machine.AllowsTransition(0UL, 1UL), "the definition rejects a declared transition");
Require(!machine.AllowsTransition(0UL, 2UL), "the definition allows an undeclared transition");

StateMachineInstance instance = machine.CreateInstance(0UL, 0UL);
StateMachineTransitionReceipt step = machine.Transition(instance, 0UL, 1UL, null);
Require(step.Previous == 0UL, $"the transition reported previous {step.Previous}, expected 0");
Require(step.Instance.Current == 1UL, $"the transition left state {step.Instance.Current}, expected 1");
Require(step.Instance.Revision == 1UL, $"the transition left revision {step.Instance.Revision}, expected 1");

StateMachineTransitionReceipt? undeclared = null;
string undeclaredSurface = "returned without throwing";
try
{
    undeclared = machine.Transition(step.Instance, 1UL, 0UL, null);
}
catch (Exception exception)
{
    undeclaredSurface = exception.GetType().Name;
}
if (undeclared is not null)
    Require(
        undeclared.Value.Instance.Current == 1UL,
        $"an undeclared transition moved the machine to {undeclared.Value.Instance.Current}");
Observe($"an undeclared transition (1 -> 0) surfaced as: {undeclaredSurface}");

// --------------------------------------------------------------------- result
if (failures.Count > 0)
{
    foreach (string failure in failures)
        Console.WriteLine($"  FAIL {failure}");
    Console.WriteLine($"CraftSurvive substrate proof failed with {failures.Count} problem(s).");
    return 1;
}

Console.WriteLine("observed behaviour:");
foreach (string observation in observations)
    Console.WriteLine($"  - {observation}");
Console.WriteLine("CraftSurvive substrate proof passed.");
return 0;

internal readonly record struct ProofRuntime(int Value);
