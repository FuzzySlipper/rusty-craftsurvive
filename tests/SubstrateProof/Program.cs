using Rusty.Engine.Entities;
using Rusty.Engine.Mechanics;
using Rusty.Engine.StateMachine;

// Staged proof for campaign #8595 slice #8596 (S0): can this product admit the
// Engine's gameplay substrate at all? The product has never constructed an
// InventoryStore, a StatsComponent, an EntityStore, or a StateMachine, so the
// integration cost that Slices 4 and 7 depend on is currently an assumption.
//
// Coverage is deliberately bounded and stated honestly: three of the five
// substrate elements S0 names are managed-provable, and this program proves
// those three — InventoryStore, StatsComponent, and StateMachine. The other two
// are host-bound and belong to the product lane: EntityGraphicsProjection needs
// an IGraphicsService, and RequestNavigationPath needs a live SpatialSession.
// S4's integration estimate stays provisional until that half runs.
//
// This program uses managed SDK values only. It launches no runtime, creates no
// session, and retains no pointer, so it belongs in the ordinary managed lane
// beside the other focused checks.

const uint ProofComponentLocalId = 9001U;
const ulong StackMaximum = 64UL;
const ulong MetricMaximum = 64UL;

List<string> failures = [];
List<string> observations = [];

void Require(bool condition, string message)
{
    if (!condition) failures.Add(message);
}

void Observe(string message) => observations.Add(message);

ulong Held(InventoryStore store, EntityId who, InventoryStackId stackId)
{
    foreach (InventoryStack stack in store.View(who).Stacks)
        if (stack.Id.Equals(stackId))
            return stack.Quantity;
    return 0UL;
}

ulong Staged(InventoryEdit edit, EntityId who, InventoryStackId stackId)
{
    foreach (InventoryStack stack in edit.View(who).Stacks)
        if (stack.Id.Equals(stackId))
            return stack.Quantity;
    return 0UL;
}

// Returns the exception type name, or "returned without throwing".
string Surface(Action operation)
{
    try
    {
        operation();
        return "returned without throwing";
    }
    catch (Exception exception)
    {
        return exception.GetType().Name;
    }
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
InventoryStackId spare = InventoryStackId.Parse("spare");
InventoryStackId delivery = InventoryStackId.Parse("delivery");
ItemDefinitionId stoneId = ItemDefinitionId.Parse("proof.stone");
ItemClassificationId weaponClass = ItemClassificationId.Parse("proof.weapon");
EquipmentSlotId handSlot = EquipmentSlotId.Parse("proof.hand");
EquipmentExclusivityId handGroup = EquipmentExclusivityId.Parse("proof.hand.group");

// A non-equippable item carries no equipment policy at all; a policy with no
// required slot is rejected, so "no policy" is the only way to say "not worn".
ItemDefinition stone = new(
    stoneId,
    ItemKind.Fungible,
    StackMaximum,
    [],
    [new ItemCapacityCost(slots, 1UL)],
    null,
    []);
Require(stone.Equipment is null, "a non-equippable item did not carry a null equipment policy");
string zeroSlotSurface = Surface(() => _ = new ItemEquipmentPolicy(0, handGroup));
Require(
    zeroSlotSurface == nameof(ArgumentOutOfRangeException),
    $"a zero-slot equipment policy surfaced as {zeroSlotSurface}, expected ArgumentOutOfRangeException");
Observe($"a zero-slot equipment policy surfaced as: {zeroSlotSurface}");

inventory.RegisterInventory(new InventoryState(owner, [new InventoryCapacityLimit(slots, MetricMaximum)]));
inventory.RegisterInventory(new InventoryState(receiver, [new InventoryCapacityLimit(slots, MetricMaximum)]));
Require(inventory.InventoryOwners.Count == 2, "the store does not list both registered inventories");

InventoryMutationReceipt granted = inventory.Grant(owner, stone, main, 10UL);
Require(granted.AfterQuantity == 10UL, $"grant reported {granted.AfterQuantity}, expected 10");
Require(
    granted.InventoryRevisionAfter > granted.InventoryRevisionBefore,
    "grant did not advance the inventory revision");
Require(Held(inventory, owner, main) == 10UL, "grant is not visible in the owner's view");

InventoryMutationReceipt consumed = inventory.Consume(owner, main, 4UL);
Require(consumed.AfterQuantity == 6UL, $"consume reported {consumed.AfterQuantity}, expected 6");
Require(Held(inventory, owner, main) == 6UL, "consume is not visible in the owner's view");

// Refusals must be atomic and typed. The store revision is asserted as well as
// the inventory revision: a refusal that moved either one would break the
// optimistic-concurrency assumptions Slices 4 and 7 are priced on.
ulong inventoryRevisionBeforeRefusal = inventory.View(owner).InventoryRevision;
ulong storeRevisionBeforeRefusal = inventory.View(owner).StoreRevision;
string insufficientSurface = Surface(() => inventory.Consume(owner, main, 999UL));
Require(
    insufficientSurface == nameof(MechanicsException),
    $"insufficient consume surfaced as {insufficientSurface}, expected MechanicsException");
Require(Held(inventory, owner, main) == 6UL, "a refused consume changed the stack");
Require(
    inventory.View(owner).InventoryRevision == inventoryRevisionBeforeRefusal,
    "a refused consume advanced the inventory revision");
Require(
    inventory.View(owner).StoreRevision == storeRevisionBeforeRefusal,
    "a refused consume advanced the store revision");
Observe($"insufficient consume surfaced as: {insufficientSurface}");

// A per-stack maximum and a per-metric capacity limit are different refusals.
// This one is the stack maximum: 1000 exceeds MaximumQuantity.
string stackMaximumSurface = Surface(() => inventory.Grant(owner, stone, spill, 1000UL));
Require(
    stackMaximumSurface == nameof(MechanicsException),
    $"a stack-maximum refusal surfaced as {stackMaximumSurface}, expected MechanicsException");
Require(Held(inventory, owner, spill) == 0UL, "a stack-maximum refusal left a partial stack");
Require(
    inventory.View(owner).InventoryRevision == inventoryRevisionBeforeRefusal,
    "a stack-maximum refusal advanced the inventory revision");
Observe($"exceeding the per-stack maximum surfaced as: {stackMaximumSurface}");

// This one is the capacity limit: 60 fits the stack maximum but 6 held + 60
// exceeds the metric maximum of 64.
string capacitySurface = Surface(() => inventory.Grant(owner, stone, spill, 60UL));
Require(
    capacitySurface == nameof(MechanicsException),
    $"a capacity refusal surfaced as {capacitySurface}, expected MechanicsException");
Require(Held(inventory, owner, spill) == 0UL, "a capacity refusal left a partial stack");
Require(
    inventory.View(owner).InventoryRevision == inventoryRevisionBeforeRefusal,
    "a capacity refusal advanced the inventory revision");
Require(
    inventory.View(owner).StoreRevision == storeRevisionBeforeRefusal,
    "a capacity refusal advanced the store revision");
Observe($"exceeding the metric capacity surfaced as: {capacitySurface}");

// The boundary itself must be usable: 6 held + 58 fills the metric exactly.
inventory.Grant(owner, stone, spill, 58UL);
Require(
    Held(inventory, owner, spill) == 58UL,
    $"granting exactly to the capacity boundary left {Held(inventory, owner, spill)}, expected 58");

inventory.SplitFungible(owner, main, spare, 3UL);
Require(
    Held(inventory, owner, main) == 3UL && Held(inventory, owner, spare) == 3UL,
    $"split left main={Held(inventory, owner, main)} spare={Held(inventory, owner, spare)}, expected 3/3");

inventory.MergeFungible(owner, spare, main);
Require(
    Held(inventory, owner, main) == 6UL && Held(inventory, owner, spare) == 0UL,
    $"merge left main={Held(inventory, owner, main)} spare={Held(inventory, owner, spare)}, expected 6/0");

inventory.TransferFungible(owner, receiver, main, delivery, 2UL);
Require(
    Held(inventory, owner, main) == 4UL && Held(inventory, receiver, delivery) == 2UL,
    $"transfer left owner={Held(inventory, owner, main)} receiver={Held(inventory, receiver, delivery)}, expected 4/2");

InventoryView view = inventory.View(owner);
Require(view.Capacity.Count > 0, "the owner's view reports no capacity usage");
Observe($"owner view reports {view.Stacks.Count} stack(s) and store revision {view.StoreRevision}");

// A multi-step change must be stageable and invisible until it publishes: this
// is the discipline crafting and loot transactions need.
using (InventoryEdit edit = inventory.Prepare(inventory.Revision))
{
    edit.Grant(owner, stone, main, 2UL);
    edit.Consume(owner, spill, 8UL);
    Require(
        Staged(edit, owner, main) == 6UL && Staged(edit, owner, spill) == 50UL,
        "a staged edit does not show its own pending result");
    Require(
        Held(inventory, owner, main) == 4UL && Held(inventory, owner, spill) == 58UL,
        "a staged edit was visible before it published");
    edit.Publish();
}
Require(
    Held(inventory, owner, main) == 6UL && Held(inventory, owner, spill) == 50UL,
    $"publishing the staged edit left main={Held(inventory, owner, main)} spill={Held(inventory, owner, spill)}, expected 6/50");
Observe("a staged grant+consume edit is invisible before Publish and applied after it");

// Publishing against a stale base must be refused rather than merged blindly.
InventoryEdit staleEdit = inventory.Prepare(inventory.Revision);
staleEdit.Grant(owner, stone, main, 1UL);
ulong mainBeforeStalePublish = Held(inventory, owner, main);
inventory.Consume(owner, main, 1UL);
string stalePublishSurface = Surface(staleEdit.Publish);
staleEdit.Dispose();
Require(
    stalePublishSurface == nameof(MechanicsException),
    $"a stale edit publish surfaced as {stalePublishSurface}, expected MechanicsException");
Require(
    Held(inventory, owner, main) == mainBeforeStalePublish - 1UL,
    "a refused stale publish still changed the stack");
Observe($"publishing a stale edit surfaced as: {stalePublishSurface}");

// Unique items are a separate lifecycle from fungible stacks.
ItemDefinitionId relicId = ItemDefinitionId.Parse("proof.relic");
EntityId relicEntity = new(700UL);
ItemDefinition relic = new(
    relicId,
    ItemKind.Unique,
    1UL,
    [weaponClass],
    [new ItemCapacityCost(slots, 1UL)],
    new ItemEquipmentPolicy(1, handGroup),
    []);
ItemMaterializationReceipt materialized = inventory.MaterializeUnique(new ItemState(relicEntity, relic), owner);
Require(materialized.Item.Equals(relicEntity), "materialising a unique item did not keep the requested entity");
Require(
    inventory.TryGetItem(relicEntity, out ItemState? _),
    "a materialised unique item is not readable from the store");
Require(
    inventory.ContainedEntities(owner).Contains(relicEntity),
    "the owner's contained entities omit the materialised item");

inventory.TransferUnique(relicEntity, owner, receiver);
Require(
    inventory.TryGetContainer(relicEntity, out EntityId container) && container.Equals(receiver),
    "transferring a unique item did not move its container");

// Equipment needs a registered equipment state and a matching slot definition.
inventory.RegisterEquipment(new EquipmentState(receiver));
EquipmentSlotDefinition slot = new(handSlot, [weaponClass]);
EquipmentMutationReceipt equipped = inventory.Equip(receiver, relicEntity, [slot]);
Require(
    inventory.TryGetEquipment(receiver, out EquipmentState? equipment) && equipment is not null && equipment.Assignments.Count == 1,
    "equipping the item did not record an assignment");
Require(inventory.TryGetItem(relicEntity, out ItemState? _), "equipping destroyed the item entity");
Observe($"an equipped unique item reports {equipped.GetType().Name} with 1 assignment");

inventory.Unequip(receiver, relicEntity);
Require(
    inventory.TryGetEquipment(receiver, out EquipmentState? afterUnequip) && afterUnequip is not null && afterUnequip.Assignments.Count == 0,
    "unequipping left an assignment behind");

inventory.DestroyUnique(relicEntity);
Require(!inventory.TryGetItem(relicEntity, out ItemState? _), "a destroyed unique item is still readable");

// ------------------------------------------------------- 2. stats and tracks
// A persisted track's maximum must be a Stat owned by the same component: a
// bare double works at runtime but makes the component uncapturable, so S7 must
// budget a maximum stat for every track it wants to save.
StatsComponent stats = new();
StatId healthId = StatId.Parse("proof.health");
StatId breathMaximumId = StatId.Parse("proof.breath.maximum");
TrackId breathId = TrackId.Parse("proof.breath");
stats.AddStat(healthId, new Stat(20d, 0d, 30d, 1d, MidpointRounding.ToZero, MidpointRounding.ToZero));
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

StatsComponent bareMaximumHost = new();
bareMaximumHost.AddTrack(
    TrackId.Parse("proof.breath.unbacked"),
    new Track(20d, 20d, 0d, TrackMaximumChangePolicy.PreserveMissingAmount, 1d, MidpointRounding.ToZero, MidpointRounding.ToZero));
string bareMaximumSurface = Surface(() => _ = StatsComponentCapture.Capture(bareMaximumHost));
Require(
    bareMaximumSurface == nameof(InvalidOperationException),
    $"capturing a track with a bare double maximum surfaced as {bareMaximumSurface}, expected InvalidOperationException");
Observe($"capturing a track whose maximum is not a co-located stat surfaced as: {bareMaximumSurface}");

// Spend/Restore return the amount actually applied after clamping, not the
// requested amount and not the resulting value.
double spent = stats.GetTrack(breathId).Spend(5d);
Require(spent == 5d, $"Spend(5) reported {spent}, expected the applied 5");
Require(stats.GetTrack(breathId).Current == 15d, $"spend left {stats.GetTrack(breathId).Current}, expected 15");

bool overspent = stats.GetTrack(breathId).TrySpend(100d);
Require(!overspent, "TrySpend succeeded with insufficient current value");
Require(stats.GetTrack(breathId).Current == 15d, "a refused TrySpend changed the current value");

double restored = stats.GetTrack(breathId).Restore(100d);
Require(restored == 5d, $"Restore(100) reported {restored}, expected the applied 5 after clamping");
Require(
    stats.GetTrack(breathId).Current == 20d,
    $"restore left {stats.GetTrack(breathId).Current}, expected a clamp at the maximum");
Observe($"Track.Spend(5) applied {spent}; Track.Restore(100) applied {restored} and clamped at 20");

StatModifierHandle modifier = stats.GetStat(healthId).AddModifier(5d, StatModifierKind.Add);
Require(stats.GetStat(healthId).Value == 25d, $"an additive modifier left value {stats.GetStat(healthId).Value}, expected 25");
Require(stats.GetStat(healthId).Modifiers.Count == 1, "the modifier is not listed on the stat");
Require(stats.GetStat(healthId).RemoveModifier(modifier), "removing the modifier reported failure");
Require(stats.GetStat(healthId).Value == 20d, $"removing the modifier left value {stats.GetStat(healthId).Value}, expected 20");
Observe("an additive stat modifier applies and removes cleanly");

StatsComponentSnapshot? snapshot = StatsComponentCapture.Capture(stats);
Require(snapshot is not null, "stats capture returned no snapshot");
if (snapshot is not null)
{
    StatsComponent rebuilt = StatsComponentCapture.Rebuild(snapshot, (_, _, _) => { });
    Require(rebuilt.GetTrack(breathId).Current == 20d, "the rebuilt component lost the track value");
    Require(
        rebuilt.TryGetStat(healthId, out Stat? rebuiltHealth) && rebuiltHealth is not null && rebuiltHealth.BaseValue == 20d,
        "the rebuilt component lost the stat base value");
    Observe("stats capture/rebuild round-trips stat and track values (modifiers and aliases are not compared here)");
}

// ---------------------------------------------------------------- 3. entities
ComponentType<ProofRuntime> runtimeComponent =
    ComponentType<ProofRuntime>.Create(ProductComponentKeys.Create(ProofComponentLocalId));
EntityStore entities = new([runtimeComponent]);
EntityId subject = entities.Create(EntityLifecycle.Active);
entities.Add(subject, new ProofRuntime(7));
Require(entities.Has(subject, runtimeComponent), "the entity does not report the added component");
Require(entities.Get(subject, runtimeComponent).Value == 7, "the component value did not round-trip");
Require(entities.IsAlive(subject), "a created entity does not report alive");
Require(
    entities.Query<ProofRuntime>(false).Count == 1,
    "the enabled-only query did not return the created entity");

EntityId batched = entities.Create(EntityLifecycle.Active);
ulong staleRevision = entities.Revision;
EntityBatch batch = new EntityBatch().Set(batched, runtimeComponent, new ProofRuntime(9), null);
entities.Commit(batch, entities.Revision);
Require(entities.Get(batched, runtimeComponent).Value == 9, "a batched component set did not commit");
Require(entities.Query<ProofRuntime>(false).Count == 2, "the batched entity is missing from the query");

ulong revisionBeforeStaleCommit = entities.Revision;
string staleCommitSurface = Surface(() => entities.Commit(batch, staleRevision));
Require(
    staleCommitSurface == nameof(InvalidOperationException),
    $"a stale batch commit surfaced as {staleCommitSurface}, expected InvalidOperationException");
Require(
    entities.Get(batched, runtimeComponent).Value == 9,
    "a refused stale batch commit overwrote the component value");
Require(
    entities.Revision == revisionBeforeStaleCommit,
    "a refused stale batch commit advanced the store revision");
Observe($"a stale batch commit surfaced as: {staleCommitSurface}");

// Disabled entities are only visible when the query asks for them.
EntityId dormant = entities.Create(EntityLifecycle.Disabled);
entities.Add(dormant, new ProofRuntime(11));
Require(
    entities.Query<ProofRuntime>(false).Count == 2,
    "an enabled-only query included a disabled entity");
Require(
    entities.Query<ProofRuntime>(true).Count == 3,
    "a disabled-inclusive query omitted a disabled entity");
Observe("query semantics: enabled-only excludes disabled entities, inclusive returns them");

EntityRevision revision = entities.GetEntityRevision(subject);
entities.Destroy(subject, revision);
Require(!entities.IsAlive(subject), "a destroyed entity still reports alive");

// Batch creation with a caller-chosen id is the staged path a spawn table would
// use. Record which way this build resolves it rather than assuming.
EntityId reserved = new(900UL);
string batchCreateSurface = "committed";
try
{
    entities.Commit(new EntityBatch().Create(reserved, EntityLifecycle.Active), entities.Revision);
}
catch (Exception exception)
{
    batchCreateSurface = exception.GetType().Name;
}
if (batchCreateSurface == "committed")
    Require(entities.IsAlive(reserved), "a committed batch Create left the entity not alive");
Observe($"a batch Create for a caller-chosen id surfaced as: {batchCreateSurface}");

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

string undeclaredSurface = Surface(() => machine.Transition(step.Instance, 1UL, 0UL, null));
Require(
    undeclaredSurface == nameof(InvalidOperationException),
    $"an undeclared transition surfaced as {undeclaredSurface}, expected InvalidOperationException");
Require(
    step.Instance.Current == 1UL,
    "the refused transition mutated the instance it was given");

string revisionGateSurface = Surface(() => machine.Transition(step.Instance, 1UL, 2UL, 99UL));
Require(
    revisionGateSurface == nameof(InvalidOperationException),
    $"a state-machine revision mismatch surfaced as {revisionGateSurface}, expected InvalidOperationException");
Observe($"an undeclared transition surfaced as: {undeclaredSurface}");
Observe($"a state-machine ExpectedRevision mismatch surfaced as: {revisionGateSurface}");

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
