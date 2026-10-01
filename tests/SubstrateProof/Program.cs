using CraftSurvive.Game.Tests;
using Rusty.Engine.Entities;
using Rusty.Engine.Mechanics;
using Rusty.Engine.StateMachine;

// An Engine canary, not a check of product code: it links no product file. It pins the Engine
// gameplay substrate the product intends to build on, so a pair update that changes it fails here
// first. A refusal is checked by its typed MechanicsRefusal reason and its effects (nothing
// partial, no revision moved); its message is printed as an observation only.
//
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

List<string> observations = [];

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

// Returns the typed refusal (null when the operation threw something else, or nothing) while
// reporting the exception type and message, so a case asserts *why* it was refused.
MechanicsRefusal? Refusal(Action operation, out string surface, out string message)
{
    try
    {
        operation();
        surface = "returned without throwing";
        message = string.Empty;
        return null;
    }
    catch (Exception exception)
    {
        surface = exception.GetType().Name;
        message = exception.Message;
        return (exception as MechanicsException)?.Reason;
    }
}

ulong CapacityUsed(InventoryStore store, EntityId who, CapacityMetricId metric)
{
    foreach (CapacityUsage usage in store.View(who).Capacity)
        if (usage.Metric.Equals(metric))
            return usage.Used;
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
Check.That(stone.Equipment is null, "a non-equippable item did not carry a null equipment policy");
string zeroSlotSurface = Surface(() => _ = new ItemEquipmentPolicy(0, handGroup));
Check.That(
    zeroSlotSurface == nameof(ArgumentOutOfRangeException),
    $"a zero-slot equipment policy surfaced as {zeroSlotSurface}, expected ArgumentOutOfRangeException");
Observe($"a zero-slot equipment policy surfaced as: {zeroSlotSurface}");

inventory.RegisterInventory(new InventoryState(owner, [new InventoryCapacityLimit(slots, MetricMaximum)]));
inventory.RegisterInventory(new InventoryState(receiver, [new InventoryCapacityLimit(slots, MetricMaximum)]));
Check.That(inventory.InventoryOwners.Count == 2, "the store does not list both registered inventories");

InventoryMutationReceipt granted = inventory.Grant(owner, stone, main, 10UL);
Check.That(granted.AfterQuantity == 10UL, $"grant reported {granted.AfterQuantity}, expected 10");
Check.That(
    granted.InventoryRevisionAfter > granted.InventoryRevisionBefore,
    "grant did not advance the inventory revision");
Check.That(Held(inventory, owner, main) == 10UL, "grant is not visible in the owner's view");

InventoryMutationReceipt consumed = inventory.Consume(owner, main, 4UL);
Check.That(consumed.AfterQuantity == 6UL, $"consume reported {consumed.AfterQuantity}, expected 6");
Check.That(Held(inventory, owner, main) == 6UL, "consume is not visible in the owner's view");

// Refusals must be atomic and typed. The store revision is asserted as well as
// the inventory revision: a refusal that moved either one would break the
// optimistic-concurrency assumptions Slices 4 and 7 are priced on.
ulong inventoryRevisionBeforeRefusal = inventory.View(owner).InventoryRevision;
ulong storeRevisionBeforeRefusal = inventory.View(owner).StoreRevision;
MechanicsRefusal? insufficient = Refusal(() => inventory.Consume(owner, main, 999UL), out string insufficientSurface, out _);
Check.That(
    insufficient == MechanicsRefusal.Insufficient,
    $"insufficient consume surfaced as {insufficientSurface} ({insufficient}), expected MechanicsException (Insufficient)");
Check.That(Held(inventory, owner, main) == 6UL, "a refused consume changed the stack");
Check.That(
    inventory.View(owner).InventoryRevision == inventoryRevisionBeforeRefusal,
    "a refused consume advanced the inventory revision");
Check.That(
    inventory.View(owner).StoreRevision == storeRevisionBeforeRefusal,
    "a refused consume advanced the store revision");
Observe($"insufficient consume surfaced as: {insufficientSurface}");

// A per-stack maximum and a per-metric capacity limit are different refusals
// with the same exception type, so each case isolates its cause and asserts its
// typed reason. This one runs against a separate owner whose
// metric limit cannot trip: 1000 exceeds MaximumQuantity and nothing else.
EntityId bulkOwner = new(3UL);
inventory.RegisterInventory(new InventoryState(bulkOwner, [new InventoryCapacityLimit(slots, 100_000UL)]));
ulong storeRevisionBeforeStackRefusal = inventory.View(owner).StoreRevision;
MechanicsRefusal? stackMaximum = Refusal(
    () => inventory.Grant(bulkOwner, stone, spill, 1000UL),
    out string stackMaximumSurface,
    out string stackMaximumMessage);
Check.That(
    stackMaximum == MechanicsRefusal.StackMaximum,
    $"a stack-maximum refusal surfaced as {stackMaximumSurface} ({stackMaximum}), expected MechanicsException (StackMaximum)");
Check.That(Held(inventory, bulkOwner, spill) == 0UL, "a stack-maximum refusal left a partial stack");
Check.That(
    inventory.View(bulkOwner).InventoryRevision == 0UL,
    "a stack-maximum refusal advanced the inventory revision");
Check.That(
    inventory.View(owner).StoreRevision == storeRevisionBeforeStackRefusal,
    "a stack-maximum refusal advanced the store revision");
Observe($"exceeding the per-stack maximum surfaced as: {stackMaximumSurface}: {stackMaximumMessage}");

// This one is the capacity limit: 60 fits the stack maximum but 6 held + 60
// exceeds the metric maximum of 64. The store-revision baseline is taken here
// because registering the isolation inventory above legitimately advanced it.
ulong storeRevisionBeforeCapacityRefusal = inventory.View(owner).StoreRevision;
MechanicsRefusal? capacity = Refusal(
    () => inventory.Grant(owner, stone, spill, 60UL),
    out string capacitySurface,
    out string capacityMessage);
Check.That(
    capacity == MechanicsRefusal.Capacity,
    $"a capacity refusal surfaced as {capacitySurface} ({capacity}), expected MechanicsException (Capacity)");
Check.That(Held(inventory, owner, spill) == 0UL, "a capacity refusal left a partial stack");
Check.That(
    inventory.View(owner).InventoryRevision == inventoryRevisionBeforeRefusal,
    "a capacity refusal advanced the inventory revision");
Check.That(
    inventory.View(owner).StoreRevision == storeRevisionBeforeCapacityRefusal,
    "a capacity refusal advanced the store revision");
Observe($"exceeding the metric capacity surfaced as: {capacitySurface}: {capacityMessage}");

// The boundary itself must be usable: 6 held + 58 fills the metric exactly, and
// one more unit must then be refused, which also guards an off-by-one limit.
inventory.Grant(owner, stone, spill, 58UL);
Check.That(
    Held(inventory, owner, spill) == 58UL,
    $"granting exactly to the capacity boundary left {Held(inventory, owner, spill)}, expected 58");
Check.That(
    CapacityUsed(inventory, owner, slots) == MetricMaximum,
    $"the filled inventory reports {CapacityUsed(inventory, owner, slots)} used, expected {MetricMaximum}");
MechanicsRefusal? boundary = Refusal(() => inventory.Grant(owner, stone, spare, 1UL), out string boundarySurface, out _);
Check.That(
    boundary == MechanicsRefusal.Capacity,
    $"a grant past the capacity boundary surfaced as {boundarySurface} ({boundary}), expected MechanicsException (Capacity)");
Check.That(Held(inventory, owner, spare) == 0UL, "a refused boundary grant left a partial stack");
Observe("the metric capacity boundary accepts an exact fill and refuses the next unit");

inventory.SplitFungible(owner, main, spare, 3UL);
Check.That(
    Held(inventory, owner, main) == 3UL && Held(inventory, owner, spare) == 3UL,
    $"split left main={Held(inventory, owner, main)} spare={Held(inventory, owner, spare)}, expected 3/3");

inventory.MergeFungible(owner, spare, main);
Check.That(
    Held(inventory, owner, main) == 6UL && Held(inventory, owner, spare) == 0UL,
    $"merge left main={Held(inventory, owner, main)} spare={Held(inventory, owner, spare)}, expected 6/0");

inventory.TransferFungible(owner, receiver, main, delivery, 2UL);
Check.That(
    Held(inventory, owner, main) == 4UL && Held(inventory, receiver, delivery) == 2UL,
    $"transfer left owner={Held(inventory, owner, main)} receiver={Held(inventory, receiver, delivery)}, expected 4/2");

InventoryView view = inventory.View(owner);
Check.That(view.Capacity.Count > 0, "the owner's view reports no capacity usage");
Observe($"owner view reports {view.Stacks.Count} stack(s) and store revision {view.StoreRevision}");

// A multi-step change must be stageable and invisible until it publishes: this
// is the discipline crafting and loot transactions need.
using (InventoryEdit edit = inventory.Prepare())
{
    edit.Grant(owner, stone, main, 2UL);
    edit.Consume(owner, spill, 8UL);
    Check.That(
        Staged(edit, owner, main) == 6UL && Staged(edit, owner, spill) == 50UL,
        "a staged edit does not show its own pending result");
    Check.That(
        Held(inventory, owner, main) == 4UL && Held(inventory, owner, spill) == 58UL,
        "a staged edit was visible before it published");
    edit.Publish();
}
Check.That(
    Held(inventory, owner, main) == 6UL && Held(inventory, owner, spill) == 50UL,
    $"publishing the staged edit left main={Held(inventory, owner, main)} spill={Held(inventory, owner, spill)}, expected 6/50");
Observe("a staged grant+consume edit is invisible before Publish and applied after it");

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
Check.That(materialized.Item.Equals(relicEntity), "materialising a unique item did not keep the requested entity");
Check.That(
    inventory.TryGetItem(relicEntity, out ItemState? _),
    "a materialised unique item is not readable from the store");
Check.That(
    inventory.ContainedEntities(owner).Contains(relicEntity),
    "the owner's contained entities omit the materialised item");

inventory.TransferUnique(relicEntity, owner, receiver);
Check.That(
    inventory.TryGetContainer(relicEntity, out EntityId container) && container.Equals(receiver),
    "transferring a unique item did not move its container");

// Equipment needs a registered equipment state and a matching slot definition.
inventory.RegisterEquipment(new EquipmentState(receiver));
EquipmentSlotDefinition slot = new(handSlot, [weaponClass]);
EquipmentMutationReceipt equipped = inventory.Equip(receiver, relicEntity, [slot]);
Check.That(
    inventory.TryGetEquipment(receiver, out EquipmentState? equipment) && equipment is not null && equipment.Assignments.Count == 1,
    "equipping the item did not record an assignment");
if (equipment is not null && equipment.Assignments.Count == 1)
{
    Check.That(
        equipment.Assignments[0].Item.Equals(relicEntity),
        "the recorded assignment does not reference the equipped item");
    Check.That(
        equipment.Assignments[0].Slot.Equals(handSlot),
        "the recorded assignment does not reference the requested slot");
}
Check.That(inventory.TryGetItem(relicEntity, out ItemState? _), "equipping destroyed the item entity");
Observe($"an equipped unique item reports {equipped.GetType().Name} with 1 assignment");

inventory.Unequip(receiver, relicEntity);
Check.That(
    inventory.TryGetEquipment(receiver, out EquipmentState? afterUnequip) && afterUnequip is not null && afterUnequip.Assignments.Count == 0,
    "unequipping left an assignment behind");

inventory.DestroyUnique(relicEntity);
Check.That(!inventory.TryGetItem(relicEntity, out ItemState? _), "a destroyed unique item is still readable");

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
Check.That(
    bareMaximumSurface == nameof(InvalidOperationException),
    $"capturing a track with a bare double maximum surfaced as {bareMaximumSurface}, expected InvalidOperationException");
Observe($"capturing a track whose maximum is not a co-located stat surfaced as: {bareMaximumSurface}");

// Spend/Restore return the amount actually applied after clamping, not the
// requested amount and not the resulting value.
double spent = stats.GetTrack(breathId).Spend(5d);
Check.That(spent == 5d, $"Spend(5) reported {spent}, expected the applied 5");
Check.That(stats.GetTrack(breathId).Current == 15d, $"spend left {stats.GetTrack(breathId).Current}, expected 15");

bool overspent = stats.GetTrack(breathId).TrySpend(100d);
Check.That(!overspent, "TrySpend succeeded with insufficient current value");
Check.That(stats.GetTrack(breathId).Current == 15d, "a refused TrySpend changed the current value");

double restored = stats.GetTrack(breathId).Restore(100d);
Check.That(restored == 5d, $"Restore(100) reported {restored}, expected the applied 5 after clamping");
Check.That(
    stats.GetTrack(breathId).Current == 20d,
    $"restore left {stats.GetTrack(breathId).Current}, expected a clamp at the maximum");
Observe($"Track.Spend(5) applied {spent}; Track.Restore(100) applied {restored} and clamped at 20");

StatModifierHandle modifier = stats.GetStat(healthId).AddModifier(5d, StatModifierKind.Add);
Check.That(stats.GetStat(healthId).Value == 25d, $"an additive modifier left value {stats.GetStat(healthId).Value}, expected 25");
Check.That(stats.GetStat(healthId).Modifiers.Count == 1, "the modifier is not listed on the stat");
Check.That(stats.GetStat(healthId).RemoveModifier(modifier), "removing the modifier reported failure");
Check.That(stats.GetStat(healthId).Value == 20d, $"removing the modifier left value {stats.GetStat(healthId).Value}, expected 20");
Observe("an additive stat modifier applies and removes cleanly");

StatsComponentSnapshot? snapshot = StatsComponentCapture.Capture(stats);
Check.That(snapshot is not null, "stats capture returned no snapshot");
if (snapshot is not null)
{
    StatsComponent rebuilt = StatsComponentCapture.Rebuild(snapshot, (_, _, _) => { });
    Check.That(rebuilt.GetTrack(breathId).Current == 20d, "the rebuilt component lost the track value");
    Check.That(
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
Check.That(entities.Has(subject, runtimeComponent), "the entity does not report the added component");
Check.That(entities.Get(subject, runtimeComponent).Value == 7, "the component value did not round-trip");
Check.That(entities.IsAlive(subject), "a created entity does not report alive");
Check.That(
    entities.Query<ProofRuntime>(false).Count == 1,
    "the enabled-only query did not return the created entity");

EntityId batched = entities.Create(EntityLifecycle.Active);
EntityBatch batch = new EntityBatch().Set(batched, runtimeComponent, new ProofRuntime(9));
entities.Commit(batch);
Check.That(entities.Get(batched, runtimeComponent).Value == 9, "a batched component set did not commit");
Check.That(entities.Query<ProofRuntime>(false).Count == 2, "the batched entity is missing from the query");

// Disabled entities are only visible when the query asks for them.
EntityId dormant = entities.Create(EntityLifecycle.Disabled);
entities.Add(dormant, new ProofRuntime(11));
Check.That(
    entities.Query<ProofRuntime>(false).Count == 2,
    "an enabled-only query included a disabled entity");
Check.That(
    entities.Query<ProofRuntime>(true).Count == 3,
    "a disabled-inclusive query omitted a disabled entity");
Observe("query semantics: enabled-only excludes disabled entities, inclusive returns them");

entities.Destroy(subject);
Check.That(!entities.IsAlive(subject), "a destroyed entity still reports alive");

// Batch creation with a caller-chosen id is the staged path a spawn table would
// use. Record which way this build resolves it rather than assuming.
EntityId reserved = new(900UL);
string batchCreateSurface = "committed";
try
{
    entities.Commit(new EntityBatch().Create(reserved, EntityLifecycle.Active));
}
catch (Exception exception)
{
    batchCreateSurface = exception.GetType().Name;
}
if (batchCreateSurface == "committed")
    Check.That(entities.IsAlive(reserved), "a committed batch Create left the entity not alive");
Observe($"a batch Create for a caller-chosen id surfaced as: {batchCreateSurface}");

// ----------------------------------------------------------- 4. state machine
StateMachineDefinition machine = new(
    1UL,
    [0UL, 1UL, 2UL],
    [new StateMachineTransition(0UL, 1UL), new StateMachineTransition(1UL, 2UL)]);
Check.That(machine.AllowsTransition(0UL, 1UL), "the definition rejects a declared transition");
Check.That(!machine.AllowsTransition(0UL, 2UL), "the definition allows an undeclared transition");

StateMachineInstance instance = machine.CreateInstance(0UL, 0UL);
StateMachineTransitionReceipt step = machine.Transition(instance, 0UL, 1UL, null);
Check.That(step.Previous == 0UL, $"the transition reported previous {step.Previous}, expected 0");
Check.That(step.Instance.Current == 1UL, $"the transition left state {step.Instance.Current}, expected 1");
Check.That(step.Instance.Revision == 1UL, $"the transition left revision {step.Instance.Revision}, expected 1");

string undeclaredSurface = Surface(() => machine.Transition(step.Instance, 1UL, 0UL, null));
Check.That(
    undeclaredSurface == nameof(InvalidOperationException),
    $"an undeclared transition surfaced as {undeclaredSurface}, expected InvalidOperationException");
Check.That(
    step.Instance.Current == 1UL,
    "the refused transition mutated the instance it was given");

string revisionGateSurface = Surface(() => machine.Transition(step.Instance, 1UL, 2UL, 99UL));
Check.That(
    revisionGateSurface == nameof(InvalidOperationException),
    $"a state-machine revision mismatch surfaced as {revisionGateSurface}, expected InvalidOperationException");
Observe($"an undeclared transition surfaced as: {undeclaredSurface}");
Observe($"a state-machine ExpectedRevision mismatch surfaced as: {revisionGateSurface}");

// --------------------------------------------------------------------- result
Console.WriteLine("observed behaviour:");
foreach (string observation in observations)
    Console.WriteLine($"  - {observation}");
return Check.Finish("Engine substrate canary");

internal readonly record struct ProofRuntime(int Value);
