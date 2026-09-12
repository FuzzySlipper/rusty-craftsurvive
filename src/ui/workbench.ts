import type { LiveDebugTransport } from '@rusty-engine/live-debug';
import { mountProcgenWorkbenchMap, type ProcgenProbe, type ProcgenReadout } from './workbench-map.js';
import { mountProcgenWorkbenchComparison } from './workbench-comparison.js';

const READOUT_INTERVAL_MS = 1_000;
const DEFAULT_CANDIDATE_PATH = 'procgen/complex-29.json';

/** Mounts the C#-owned procedural candidate readout; this DOM panel retains no game state. */
export function mountProcgenWorkbench(host: HTMLElement, transport: LiveDebugTransport): Readonly<{ dispose(): void }> {
  const toggle = button('Procgen workbench');
  toggle.setAttribute('aria-controls', 'craft-procgen-workbench');
  toggle.setAttribute('aria-expanded', 'false');

  const panel = document.createElement('section');
  panel.id = 'craft-procgen-workbench';
  panel.hidden = true;
  panel.setAttribute('aria-label', 'Procedural generation workbench');
  panel.setAttribute('data-rusty-ui-interactive', '');
  const panelStyle = (expanded: boolean): string =>
    'box-sizing:border-box;background:rgb(15 19 25 / 98%);border:1px solid #62748a;border-radius:.4rem;color:#edf5ff;font:.78rem/1.35 ui-monospace,SFMono-Regular,Menlo,monospace;overflow:auto;padding:.6rem;' +
    (expanded ? 'position:fixed;inset:12px;z-index:1000;max-width:none;max-height:none;'
      : 'margin-top:.35rem;max-height:calc(100vh - 9rem);width:min(42rem,calc(100vw - 2rem));');
  panel.style.cssText = panelStyle(false);
  isolateEvents(panel);

  const heading = document.createElement('strong');
  heading.textContent = 'Procgen workbench';
  const description = document.createElement('p');
  description.textContent = 'Compare the room and passage plan with the world. Step trace changes only the model; Enter starts walking. Use E / Y near a station.';
  description.style.cssText = 'margin:.3rem 0;max-width:34rem;';

  const candidateLabel = document.createElement('label');
  candidateLabel.textContent = 'Candidate content path';
  candidateLabel.style.cssText = 'display:grid;gap:.15rem;min-width:0;';
  const candidatePath = document.createElement('input');
  candidatePath.value = DEFAULT_CANDIDATE_PATH;
  candidatePath.autocomplete = 'off';
  candidatePath.spellcheck = false;
  candidatePath.setAttribute('aria-label', 'Candidate content path without spaces');
  candidatePath.style.cssText = 'box-sizing:border-box;min-width:0;width:100%;';
  candidateLabel.append(candidatePath);
  const sample = document.createElement('select');
  sample.setAttribute('aria-label', 'Candidate sample');
  const samples = [
    'procgen/complex-29.json', 'procgen/complex-83.json',
    'procgen/workbench-11.json', 'procgen/recovery-11.json', 'procgen/preview-11.json',
    'procgen/workbench-failure-11.json', 'procgen/recovery-failure-11.json', 'procgen/preview-failure-11.json',
  ];
  for (const path of samples) { const option = document.createElement('option'); option.value = path; option.textContent = path.replace('procgen/', '').replace('.json', ''); sample.append(option); }
  sample.value = DEFAULT_CANDIDATE_PATH;
  const updateSamples = (paths: readonly string[]): void => {
    const selected = candidatePath.value;
    const entries = Array.from(new Set(paths));
    sample.replaceChildren(...entries.map((path) => {
      const option = document.createElement('option'); option.value = path; option.textContent = path.replace('procgen/', '').replace('.json', ''); return option;
    }));
    sample.value = entries.includes(selected) ? selected : entries[0] ?? '';
  };
  const load = button('Load candidate');
  load.setAttribute('aria-label', 'Load candidate from content path');
  const enter = button('Enter');
  enter.setAttribute('aria-label', 'Enter physical play');
  const reset = button('Reset');
  reset.setAttribute('aria-label', 'Reset replay and physical run');
  const step = button('Step trace');
  step.setAttribute('aria-label', 'Step the selected abstract trace');
  const inspectWitness = button('Inspect witness');
  inspectWitness.setAttribute('aria-label', 'Select the completing abstract witness');
  const inspectFailure = button('Inspect failure');
  inspectFailure.setAttribute('aria-label', 'Select the abstract failure trace');
  const useSwitch = button('Interact (E / Y)');
  useSwitch.setAttribute('aria-label', 'Interact near the current world marker using E or controller Y');
  const breach = button('Introduce side bypass');
  breach.setAttribute('aria-label', 'Introduce the realization side bypass');
  const repair = button('Repair realization');
  repair.setAttribute('aria-label', 'Repair the realization and restore the intended gate');
  const check = button('Check realization');
  check.setAttribute('aria-label', 'Rerun the realization spatial checks');
  const refresh = button('Refresh');
  refresh.setAttribute('aria-label', 'Refresh C# procedural candidate readout');
  const expandMap = button('Expand map');
  expandMap.setAttribute('aria-pressed', 'false');
  const actions = document.createElement('div');
  actions.style.cssText = 'align-items:end;display:flex;flex-wrap:wrap;gap:.3rem;margin:.35rem 0;';
  actions.append(candidateLabel, sample, load, enter, reset, inspectWitness, inspectFailure, step, useSwitch, breach, repair, check, refresh, expandMap);

  const receipt = document.createElement('p');
  receipt.setAttribute('aria-live', 'polite');
  receipt.style.cssText = 'margin:.35rem 0;overflow-wrap:anywhere;';
  const readoutNotice = document.createElement('p');
  readoutNotice.setAttribute('aria-live', 'polite');
  readoutNotice.style.cssText = 'margin:.35rem 0;overflow-wrap:anywhere;';
  readoutNotice.textContent = 'Candidate readout unavailable until the panel opens.';
  const readoutHost = document.createElement('div');
  readoutHost.setAttribute('aria-label', 'Latest procedural candidate facts');
  const map = mountProcgenWorkbenchMap(readoutHost);
  const realizationNotice = document.createElement('p');
  realizationNotice.setAttribute('aria-live', 'polite');
  realizationNotice.style.cssText = 'background:rgb(52 31 34 / 72%);border-left:3px solid #ff6c70;margin:.35rem 0;padding:.3rem .45rem;overflow-wrap:anywhere;';
  realizationNotice.hidden = true;
  const analysisNotice = document.createElement('p');
  analysisNotice.setAttribute('aria-live', 'polite');
  analysisNotice.textContent = 'Progression analysis awaits a candidate.';
  analysisNotice.style.cssText = 'background:rgb(32 51 71 / 58%);border-left:3px solid #6db7df;margin:.35rem 0;padding:.3rem .45rem;overflow-wrap:anywhere;';
  const details = document.createElement('details');
  details.setAttribute('aria-label', 'Resolved candidate text details');
  const summary = document.createElement('summary'); summary.textContent = 'Details and analysis';
  const detailHost = document.createElement('div'); detailHost.style.cssText = 'margin-top:.35rem;';
  details.append(summary, detailHost); readoutHost.append(realizationNotice, analysisNotice, details);
  readoutHost.prepend(realizationNotice);
  const comparisonHost = document.createElement('div');
  comparisonHost.setAttribute('aria-label', 'Candidate comparison and targeted repair');
  const comparison = mountProcgenWorkbenchComparison(comparisonHost, transport, {
    load: (path) => { candidatePath.value = path; if (Array.from(sample.options).some((option) => option.value === path)) sample.value = path; void mutate('Load candidate', 'craft.procgen.load ' + path); },
    pin: (revision) => { void mutate('Pin baseline', 'craft.procgen.reference ' + String(revision)); },
    mend: (revision, operation) => { void mutate('Apply ' + operation, 'craft.procgen.mend ' + String(revision) + ' ' + operation); },
    export: async () => {
      if (readFinished !== null) await readFinished;
      const result = await transport.execute('craft.procgen.export');
      if (!result.succeeded) throw new Error(result.message);
      return result.message;
    },
    bank: (entries) => updateSamples(entries.map((entry) => entry.path)),
  });
  panel.append(heading, description, actions, receipt, readoutNotice, comparisonHost, readoutHost);
  host.append(toggle, panel);

  let disposed = false;
  let open = false;
  let working = false;
  let mutating = false;
  let readFinished: Promise<void> | null = null;
  let readout: ProcgenReadout | null = null;
  let poll: ReturnType<typeof setInterval> | null = null;
  let request: AbortController | null = null;
  let mapExpanded = false;
  const actionButtons = [load, enter, reset, inspectWitness, inspectFailure, step, useSwitch, breach, repair, check, refresh, expandMap];

  const updateButtons = (): void => {
    const unavailable = readout === null || !readout.active || pendingReadout(readout.status);
    // Background reads must not disable a focused button or discard its click.
    load.disabled = mutating || (readout !== null && pendingReadout(readout.status));
    candidatePath.disabled = mutating;
    sample.disabled = mutating;
    refresh.disabled = mutating;
    for (const action of [enter, reset, useSwitch, breach, repair, check]) action.disabled = mutating || unavailable;
    inspectWitness.disabled = mutating || unavailable || readout === null || readout.analysis === null || readout.analysis.witness.length === 0;
    inspectFailure.disabled = mutating || unavailable || readout === null || readout.analysis === null || readout.analysis.counterexamples.length === 0;
    step.disabled = mutating || unavailable || readout === null || readout.cursor >= readout.witness.length;
    comparison.setMutating(mutating);
  };
  const showReadout = (next: ProcgenReadout): void => {
    readout = next;
    description.hidden = next.active;
    map.render(next);
    renderRealizationNotice(realizationNotice, next);
    renderAnalysisNotice(analysisNotice, next.analysis);
    renderReadout(detailHost, next);
    readoutNotice.textContent = readoutSummary(next);
    comparison.observe(next);
    updateButtons();
  };
  const refreshReadout = async (announceError: boolean): Promise<void> => {
    if (disposed || !open || working || mutating) return;
    working = true;
    let finishRead!: () => void;
    readFinished = new Promise<void>((resolve) => { finishRead = resolve; });
    updateButtons();
    if (announceError && readout === null) readoutNotice.textContent = 'Reading candidate state…';
    const abort = new AbortController();
    request = abort;
    try {
      const result = await transport.execute('craft.procgen.readout', abort.signal);
      if (disposed || !open || abort.signal.aborted) return;
      if (!result.succeeded) throw new Error(result.message);
      showReadout(parseReadout(result.message));
    } catch (error: unknown) {
      if (!disposed && open && !abort.signal.aborted) {
        const failure = message(error, 'C# procgen readout failed.');
        readout = null;
        map.unavailable(failure);
        realizationNotice.hidden = true;
        analysisNotice.textContent = 'Latest analysis unavailable: ' + failure;
        renderUnavailable(detailHost, failure);
        readoutNotice.textContent = 'Readout unavailable: ' + failure;
        comparison.unavailable(failure);
      }
    } finally {
      if (request === abort) request = null;
      working = false;
      readFinished = null;
      finishRead();
      if (!disposed) updateButtons();
    }
  };
  const mutate = async (label: string, command: string): Promise<boolean> => {
    if (disposed || !open || mutating) return false;
    mutating = true;
    updateButtons();
    receipt.textContent = label + ' pending…';
    try {
      // Serialize behind the current read; never drop an ordinary user action
      // just because the observation timer happened to run first.
      if (readFinished !== null) await readFinished;
      if (disposed) return false;
      const result = await transport.execute(command);
      if (disposed) return false;
      if (!result.succeeded) throw new Error(result.message);
      receipt.textContent = label + ': ' + result.message;
      return true;
    } catch (error: unknown) {
      if (!disposed) receipt.textContent = label + ' failed: ' + message(error, 'Workbench command failed.');
      return false;
    } finally {
      if (!disposed) {
        mutating = false;
        updateButtons();
        void refreshReadout(false);
      }
    }
  };
  const currentRevision = (): string | null => {
    if (readout === null || !readout.active || pendingReadout(readout.status) || !Number.isSafeInteger(readout.revision) || readout.revision < 0) {
      receipt.textContent = 'A candidate revision is unavailable. Load a candidate or refresh.';
      return null;
    }
    return String(readout.revision);
  };
  const openPanel = (): void => {
    open = true;
    panel.hidden = false;
    toggle.setAttribute('aria-expanded', 'true');
    void refreshReadout(true);
    comparison.refreshBank();
    poll = setInterval(() => { void refreshReadout(false); }, READOUT_INTERVAL_MS);
  };
  const closePanel = (): void => {
    open = false;
    panel.hidden = true;
    toggle.setAttribute('aria-expanded', 'false');
    if (poll !== null) { clearInterval(poll); poll = null; }
    request?.abort();
  };

  toggle.addEventListener('click', () => { if (!disposed) open ? closePanel() : openPanel(); });
  sample.addEventListener('change', () => { candidatePath.value = sample.value; });
  expandMap.addEventListener('click', () => {
    mapExpanded = !mapExpanded;
    panel.style.cssText = panelStyle(mapExpanded);
    map.expand(mapExpanded);
    panel.scrollTop = 0;
    expandMap.textContent = mapExpanded ? 'Compact map' : 'Expand map';
    expandMap.setAttribute('aria-pressed', String(mapExpanded));
  });
  load.addEventListener('click', () => {
    const path = candidatePath.value.trim();
    if (path.length === 0 || /\s/u.test(path)) {
      receipt.textContent = 'Candidate path is required and cannot contain spaces.';
      return;
    }
    void mutate('Load candidate', 'craft.procgen.load ' + path);
  });
  enter.addEventListener('click', () => { const revision = currentRevision(); if (revision !== null) void mutate('Enter', 'craft.procgen.enter ' + revision); });
  reset.addEventListener('click', () => { const revision = currentRevision(); if (revision !== null) void mutate('Reset', 'craft.procgen.reset ' + revision); });
  inspectWitness.addEventListener('click', () => { const revision = currentRevision(); if (revision !== null) void mutate('Inspect witness', 'craft.procgen.witness ' + revision); });
  inspectFailure.addEventListener('click', () => { const revision = currentRevision(); if (revision !== null) void mutate('Inspect failure', 'craft.procgen.counterexample ' + revision); });
  step.addEventListener('click', () => { const revision = currentRevision(); if (revision !== null) void mutate('Step trace', 'craft.procgen.step ' + revision); });
  useSwitch.addEventListener('click', () => { const revision = currentRevision(); if (revision !== null) void mutate('Interact', 'craft.procgen.use ' + revision); });
  breach.addEventListener('click', () => { const revision = currentRevision(); if (revision !== null) void mutate('Introduce side bypass', 'craft.procgen.breach ' + revision); });
  repair.addEventListener('click', () => { const revision = currentRevision(); if (revision !== null) void mutate('Repair realization', 'craft.procgen.repair ' + revision); });
  check.addEventListener('click', () => { const revision = currentRevision(); if (revision !== null) void mutate('Check realization', 'craft.procgen.check ' + revision); });
  refresh.addEventListener('click', () => void refreshReadout(true));
  updateButtons();

  return Object.freeze({ dispose: () => {
    disposed = true;
    closePanel();
    for (const buttonElement of actionButtons) buttonElement.disabled = true;
    sample.disabled = true; candidatePath.disabled = true;
    map.dispose();
    comparison.dispose();
    toggle.remove();
    panel.remove();
  } });
}

function renderReadout(host: HTMLElement, readout: ProcgenReadout): void {
  const facts = document.createElement('dl');
  facts.style.cssText = 'display:grid;gap:.2rem .6rem;grid-template-columns:max-content minmax(0,1fr);margin:.45rem 0;';
  const stateLabel = modelReplayMode(readout.mode) ? 'Model state' : 'Walking state';
  const rows: readonly (readonly [string, string])[] = [
    ['Revision', String(readout.revision)], ['Identity', readout.identity], ['Motif', readout.motif], ['Source', readout.source], ['Seed', readout.seed],
    ['Status', readout.status], ['Error', readout.error], ['Active', readout.active ? 'Active' : 'Inactive'], ['Mode', readout.mode],
    [stateLabel, stateText(readout.state)], ['Model state', stateText(readout.modelState)], ['Physical state', stateText(readout.physicalState)], ['Player position', vector(readout.playerPosition)],
    ...realizationRows(readout.realization),
    ...(readout.checks.build !== undefined && readout.checks.build.trim().length > 0 ? [['Voxel build', readout.checks.build] as const] : []),
    [readout.replayLabel + ' cursor', String(readout.cursor) + (readout.completed ? ' · reported complete' : '')],
    ['Legal actions', textList(readout.legalActions)],
  ];
  for (const [label, value] of rows) {
    const term = document.createElement('dt'); term.textContent = label;
    const description = document.createElement('dd'); description.textContent = value || 'Unavailable'; description.style.cssText = 'margin:0;overflow-wrap:anywhere;';
    facts.append(term, description);
  }

  const checkRows: (readonly [string, string])[] = [
    ['Model', readout.checks.model], ['Routes', readout.checks.routes], ['Separations', readout.checks.separations], ['Coverage', readout.checks.coverage], ['Information', readout.checks.information],
  ];
  if (readout.checks.build !== undefined && readout.checks.build.trim().length > 0) checkRows.unshift(['Build', readout.checks.build]);
  if (readout.checks.realization !== undefined && readout.checks.realization.trim().length > 0) checkRows.unshift(['Realization', readout.checks.realization]);
  const checks = table('Reported checks', ['Check', 'Report'], checkRows);
  const probes = renderProbeChecks(readout.checks.probes ?? []);
  const rooms = table('Rooms', ['Room', 'Minimum (x, y, z)', 'Maximum (x, y, z)'], readout.rooms.map((room) => [
    room.id, vector(room.minimum), vector(room.maximum),
  ]));
  const connections = table('Connections', ['Route', 'From', 'To', 'Width', 'Route kind'], readout.routes.map((route) => [
    route.id, route.from, route.to, String(route.width), route.requiresSwitch ? 'conditional' : 'open',
  ]));
  const markers = table('Stations', ['Marker', 'Room', 'Action', 'Position'], readout.layout.markers.map((marker) => [marker.label, marker.room, marker.action, vector(marker.position)]));
  const witnessHeading = document.createElement('h3'); witnessHeading.textContent = 'Abstract trace · ' + readout.replayLabel; witnessHeading.style.cssText = 'font-size:1em;margin:.55rem 0 .2rem;';
  const witness = list(readout.witness, readout.cursor);
  const analysis = renderAnalysis(readout.analysis);
  const historyHeading = document.createElement('h3'); historyHeading.textContent = 'Activity'; historyHeading.style.cssText = 'font-size:1em;margin:.55rem 0 .2rem;';
  const history = list(readout.history);
  host.replaceChildren(facts, checks, probes, analysis, rooms, connections, markers, witnessHeading, witness, historyHeading, history);
}

function renderRealizationNotice(host: HTMLElement, readout: ProcgenReadout): void {
  const realization = readout.realization;
  const probes = readout.checks.probes ?? [];
  const failures = probes.filter((probe) => probe.passed === false).map((probe) => probe.id);
  const unknown = probes.some((probe) => probe.passed === null) || readout.checks.realization?.startsWith('UNAVAILABLE') === true;
  if (realization === undefined && readout.checks.realization === undefined && probes.length === 0) {
    host.hidden = true;
    host.textContent = '';
    return;
  }
  host.hidden = false;
  const gate = realization?.gateState ?? 'Unavailable';
  const treatment = realization?.treatment ?? 'No treatment reported';
  const summary = readout.checks.realization?.trim() || 'No realization summary reported';
  const omitted = readout.checks.omittedProbes ?? 0;
  const failureText = failures.length > 0 ? ' · failing checks: ' + failures.join(', ') : unknown ? ' · some checks unavailable' : ' · no failing probe checks';
  host.textContent = 'Realization · ' + treatment + ' · gate ' + gate + ' · ' + summary + failureText
    + (omitted > 0 ? ' · Showing ' + probes.length + ' probe rows; ' + omitted + ' omitted (failures and unknowns first).' : '');
  host.style.borderLeftColor = failures.length > 0 ? '#ff6c70' : unknown ? '#e4bd72' : '#67d6a4';
  host.style.background = failures.length > 0 ? 'rgb(52 31 34 / 72%)' : unknown ? 'rgb(58 48 28 / 72%)' : 'rgb(24 52 43 / 72%)';
}

function realizationRows(realization: ProcgenReadout['realization']): readonly (readonly [string, string])[] {
  if (realization === undefined) return [];
  return [
    ['Realization identity', realization.identity],
    ['Requirement state identity', realization.stateIdentity ?? 'Unavailable'],
    ['Realization treatment', realization.treatment],
    ['Breach route', realization.breachRoute],
    ['Gate state', realization.gateState],
    ['Spatial revision', realization.spatialRevision],
  ];
}

function renderProbeChecks(probes: readonly ProcgenProbe[]): HTMLElement {
  const rows = probes.map((probe) => [
    probe.id,
    probe.kind,
    vector(probe.from),
    vector(probe.to),
    probe.expected,
    probe.observed,
    probe.passed === true ? 'passed' : probe.passed === false ? 'FAILED' : 'unknown',
  ] as const);
  if (rows.length > 0) return table('Realization probes', ['ID', 'Kind', 'From', 'To', 'Expected', 'Observed', 'Result'], rows);
  const section = document.createElement('section');
  const heading = document.createElement('h3'); heading.textContent = 'Realization probes'; heading.style.cssText = 'font-size:1em;margin:.55rem 0 .2rem;';
  const notice = document.createElement('p'); notice.textContent = 'No named realization probes reported.'; notice.style.margin = '.3rem 0';
  section.append(heading, notice);
  return section;
}

function renderUnavailable(host: HTMLElement, reason: string): void {
  const notice = document.createElement('p');
  notice.textContent = 'Latest C# readout is unavailable: ' + reason;
  notice.style.cssText = 'margin:.45rem 0;overflow-wrap:anywhere;';
  host.replaceChildren(notice);
}

function parseReadout(message: string): ProcgenReadout {
  const parsed: unknown = JSON.parse(message);
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) throw new Error('C# procgen readout was not an object.');
  return parsed as ProcgenReadout;
}

function readoutSummary(readout: ProcgenReadout): string {
  const error = readout.error.trim();
  return error.length > 0 ? 'C# reports: ' + error : 'C# readout status: ' + (readout.status || 'Unavailable');
}

function renderAnalysisNotice(host: HTMLElement, analysis: ProcgenReadout['analysis']): void {
  host.textContent = analysis === null
    ? 'Progression analysis unavailable until a candidate loads.'
    : 'Analysis reports ' + (analysis.completable ? 'completable' : 'not completable') + ' · reachable states ' + String(analysis.reachableStates) + ' · unrecoverable states ' + String(analysis.unrecoverableStates) + ' · counterexamples ' + String(analysis.counterexamples.length) + '.';
}

function renderAnalysis(analysis: ProcgenReadout['analysis']): HTMLElement {
  const section = document.createElement('section');
  const heading = document.createElement('h3'); heading.textContent = 'Progression analysis'; heading.style.cssText = 'font-size:1em;margin:.55rem 0 .2rem;';
  if (analysis === null) {
    const unavailable = document.createElement('p'); unavailable.textContent = 'Unavailable until a candidate loads.'; unavailable.style.margin = '.3rem 0';
    section.append(heading, unavailable); return section;
  }
  const summary = document.createElement('p');
  summary.textContent = 'Reports ' + (analysis.completable ? 'completable' : 'not completable') + ' · reachable states ' + String(analysis.reachableStates) + ' · unrecoverable states ' + String(analysis.unrecoverableStates) + '.';
  summary.style.margin = '.3rem 0';
  const contracts = table('Contract reports', ['Kind', 'Requirement', 'Report', 'Evidence'], analysis.contracts.map((contract) => [
    contract.kind, contract.requirement, contract.passed ? 'reported passed' : 'reported not passed', contract.evidence,
  ]));
  const counterexampleHeading = document.createElement('h4'); counterexampleHeading.textContent = 'Counterexamples'; counterexampleHeading.style.cssText = 'font-size:1em;margin:.45rem 0 .15rem;';
  const counterexamples = document.createElement('ul'); counterexamples.style.cssText = 'margin:.25rem 0;padding-left:1.5rem;';
  for (const item of analysis.counterexamples) { const entry = document.createElement('li'); entry.textContent = item.requirement + ': ' + item.actions.join(' → ') + ' · ' + item.reason; counterexamples.append(entry); }
  if (analysis.counterexamples.length === 0) { const entry = document.createElement('li'); entry.textContent = 'None reported.'; counterexamples.append(entry); }
  const witnessHeading = document.createElement('h4'); witnessHeading.textContent = 'Analysis witness'; witnessHeading.style.cssText = 'font-size:1em;margin:.45rem 0 .15rem;';
  section.append(heading, summary, contracts, counterexampleHeading, counterexamples, witnessHeading, list(analysis.witness));
  return section;
}

function table(title: string, headings: readonly string[], rows: readonly (readonly string[])[]): HTMLElement {
  const heading = document.createElement('h3'); heading.textContent = title; heading.style.cssText = 'font-size:1em;margin:.55rem 0 .2rem;';
  const value = document.createElement('table');
  value.style.cssText = 'border-collapse:collapse;max-width:100%;width:100%;';
  const head = document.createElement('thead'); const headRow = document.createElement('tr');
  for (const label of headings) { const cell = document.createElement('th'); cell.scope = 'col'; cell.textContent = label; cell.style.cssText = 'border-bottom:1px solid #62748a;padding:.15rem;text-align:left;vertical-align:top;'; headRow.append(cell); }
  head.append(headRow);
  const body = document.createElement('tbody');
  for (const row of rows) {
    const tableRow = document.createElement('tr');
    for (const item of row) { const cell = document.createElement('td'); cell.textContent = item || 'Unavailable'; cell.style.cssText = 'border-bottom:1px solid #415165;overflow-wrap:anywhere;padding:.15rem;vertical-align:top;'; tableRow.append(cell); }
    body.append(tableRow);
  }
  if (rows.length === 0) { const row = document.createElement('tr'); const cell = document.createElement('td'); cell.colSpan = headings.length; cell.textContent = 'Unavailable'; row.append(cell); body.append(row); }
  value.append(head, body);
  const wrapper = document.createElement('div'); wrapper.style.cssText = 'overflow:auto;'; wrapper.append(heading, value); return wrapper;
}

function isolateEvents(panel: HTMLElement): void {
  const stop = (event: Event): void => event.stopPropagation();
  for (const type of ['pointerdown', 'pointermove', 'pointerup', 'pointercancel', 'mousedown', 'mousemove', 'mouseup', 'wheel', 'keydown', 'keyup', 'click']) panel.addEventListener(type, stop);
}
function button(text: string): HTMLButtonElement { const value = document.createElement('button'); value.type = 'button'; value.textContent = text; return value; }
function booleanText(value: boolean): string { return value ? 'Open' : 'Closed'; }
function vector(value: { x: number; y: number; z: number }): string { return value.x + ', ' + value.y + ', ' + value.z; }
function textList(values: readonly string[]): string { return values.length > 0 ? values.join(', ') : 'Unavailable'; }
function message(error: unknown, fallback: string): string { return error instanceof Error && error.message.length > 0 ? error.message : fallback; }
function modelReplayMode(mode: string): boolean { return mode === 'inspection' || mode.startsWith('model replay'); }
function pendingReadout(status: string): boolean { return status.trim().toLowerCase().startsWith('pending'); }
function stateText(state: ProcgenReadout['state']): string { return state.room + ' · switch ' + booleanText(state.switchOpen) + ' · token ' + yesNo(state.token) + ' · spent ' + yesNo(state.spent) + ' · recovered ' + yesNo(state.recovered) + ' · observed ' + yesNo(state.observed); }
function yesNo(value: boolean): string { return value ? 'yes' : 'no'; }
function list(values: readonly string[], cursor?: number): HTMLElement { const output = document.createElement('ol'); output.style.cssText = 'margin:.3rem 0;padding-left:1.5rem;'; for (const [index, item] of values.entries()) { const entry = document.createElement('li'); entry.textContent = index === cursor ? '▶ ' + item : item; if (index === cursor) entry.setAttribute('aria-current', 'step'); output.append(entry); } if (values.length === 0) { const entry = document.createElement('li'); entry.textContent = 'Unavailable'; output.append(entry); } return output; }
