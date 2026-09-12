import type { LiveDebugTransport } from '@rusty-engine/live-debug';
import { mountProcgenWorkbenchMap, type ProcgenReadout } from './workbench-map.js';

type BankEntry = Readonly<{
  path: string;
  identity: string;
  motif: string;
  seed: string;
  rooms: number;
  routes: number;
  completable: boolean;
  failures: readonly string[];
  error: string;
}>;

type RepairReceipt = Readonly<{
  parentIdentity: string;
  resultIdentity: string;
  operation: string;
  cost: number;
  changedField: string;
}>;

type Comparison = Readonly<{
  identity: string;
  revision: number;
  reference: ProcgenReadout | null;
  differences: readonly string[];
  operations: readonly string[];
  repair: RepairReceipt | null;
}>;

type Capture = Readonly<{ identity: string; revision: number; url: string; name: string }>;

type Commands = Readonly<{
  load(path: string): void;
  pin(revision: number): void;
  mend(revision: number, operation: string): void;
  export(revision: number): Promise<string | null>;
  bank?(entries: readonly BankEntry[]): void;
}>;

/** Presents C#-owned bank/comparison facts and queues named workbench commands. */
export function mountProcgenWorkbenchComparison(host: HTMLElement, transport: LiveDebugTransport, commands: Commands): Readonly<{
  observe(readout: ProcgenReadout): void;
  refreshBank(): void;
  setMutating(value: boolean): void;
  unavailable(reason: string): void;
  dispose(): void;
}> {
  const details = document.createElement('details');
  details.setAttribute('aria-label', 'Candidate comparison and targeted repair');
  details.style.cssText = 'border:1px solid #4f687f;border-radius:.4rem;margin:.45rem 0;padding:.35rem .45rem;';
  const summary = document.createElement('summary');
  summary.textContent = 'Candidate comparison and targeted repair';
  const notice = document.createElement('p');
  notice.setAttribute('aria-live', 'polite');
  notice.textContent = 'Pin a candidate, then load another to compare. A repair automatically retains its parent as the baseline.';
  notice.style.cssText = 'margin:.35rem 0;overflow-wrap:anywhere;';

  const controls = document.createElement('div');
  controls.style.cssText = 'align-items:center;display:flex;flex-wrap:wrap;gap:.35rem;margin:.35rem 0;';
  const pin = button('Pin current baseline');
  pin.setAttribute('aria-label', 'Freeze the current C# candidate readout as the comparison baseline');
  const refreshBank = button('Refresh candidate bank');
  refreshBank.setAttribute('aria-label', 'Read the bounded C# candidate bank');
  const prepareReceipt = button('Prepare repair receipt');
  prepareReceipt.setAttribute('aria-label', 'Read the C# repair receipt and prepare its browser download');
  const receiptDownload = document.createElement('a');
  receiptDownload.hidden = true;
  receiptDownload.textContent = 'Download repair receipt';
  receiptDownload.style.cssText = 'color:#bfe5ff;cursor:pointer;';
  controls.append(pin, refreshBank, prepareReceipt, receiptDownload);

  const repairControls = document.createElement('div');
  repairControls.style.cssText = 'align-items:center;display:flex;flex-wrap:wrap;gap:.35rem;margin:.35rem 0;';
  const repairStatus = document.createElement('p');
  repairStatus.style.cssText = 'margin:.35rem 0;overflow-wrap:anywhere;';

  const differenceHost = document.createElement('div');
  differenceHost.setAttribute('aria-label', 'C# candidate differences and progression reports');
  const bankHost = document.createElement('div');
  bankHost.setAttribute('aria-label', 'Bounded candidate bank');

  const captures = document.createElement('details');
  const capturesTitle = document.createElement('summary'); capturesTitle.textContent = 'Optional decision-point captures'; capturesTitle.style.cssText = 'font-size:1em;margin:.55rem 0 .2rem;';
  const capturesNotice = document.createElement('p');
  capturesNotice.textContent = 'Compare your own before/after screenshots. Attachments are temporary and manually associated with the observed candidate; their contents are not verified.';
  capturesNotice.style.cssText = 'margin:.3rem 0;';
  const captureGrid = document.createElement('div'); captureGrid.style.cssText = 'display:grid;gap:.45rem;grid-template-columns:repeat(auto-fit,minmax(14rem,1fr));';
  const referenceCapture = captureSlot('Reference capture');
  const currentCapture = captureSlot('Current capture');
  captureGrid.append(referenceCapture.host, currentCapture.host); captures.append(capturesTitle, capturesNotice, captureGrid);

  const maps = document.createElement('div');
  maps.hidden = true;
  maps.style.cssText = 'display:none;gap:.45rem;grid-template-columns:repeat(auto-fit,minmax(18rem,1fr));margin:.45rem 0;';
  const referenceMapHost = document.createElement('div');
  const currentMapHost = document.createElement('div');
  maps.append(referenceMapHost, currentMapHost);
  const referenceMap = mountProcgenWorkbenchMap(referenceMapHost, { title: 'Frozen reference plan', compact: true, captionPrefix: 'Frozen · ' });
  const currentMap = mountProcgenWorkbenchMap(currentMapHost, { title: 'Current plan', compact: true, captionPrefix: 'Current · ' });

  details.append(summary, notice, controls, repairControls, repairStatus, differenceHost, maps, captures, bankHost);
  host.append(details);

  let disposed = false;
  let mutating = false;
  let current: ProcgenReadout | null = null;
  let comparison: Comparison | null = null;
  let comparisonRequest: AbortController | null = null;
  let bankRequest: AbortController | null = null;
  let receiptUrl: string | null = null;
  let referenceImage: Capture | null = null;
  let currentImage: Capture | null = null;
  let repairControlsSignature = '';

  const signature = (readout: ProcgenReadout): string => readout.identity + ':' + String(readout.revision);
  const updateControls = (): void => {
    const usable = current !== null && current.active && !pending(current.status) && !mutating;
    pin.disabled = !usable;
    prepareReceipt.disabled = !usable || comparison?.repair == null;
    refreshBank.disabled = mutating;
    for (const value of Array.from(repairControls.querySelectorAll('button'))) value.disabled = !usable;
  };
  const clearReceipt = (): void => {
    if (receiptUrl !== null) URL.revokeObjectURL(receiptUrl);
    receiptUrl = null; receiptDownload.hidden = true; receiptDownload.removeAttribute('href'); receiptDownload.removeAttribute('download');
  };
  const clearCapture = (slot: ReturnType<typeof captureSlot>, currentCapture: Capture | null): null => {
    if (currentCapture !== null) URL.revokeObjectURL(currentCapture.url);
    slot.input.value = ''; slot.image.hidden = true; slot.image.removeAttribute('src'); slot.caption.textContent = 'No attachment.';
    return null;
  };
  const validateCaptures = (clearMissingReference = false): void => {
    const reference = comparison?.reference ?? null;
    if (referenceImage !== null && ((clearMissingReference && reference === null) || (reference !== null && signature(reference) !== referenceImage.identity + ':' + String(referenceImage.revision))))
      referenceImage = clearCapture(referenceCapture, referenceImage);
    if (currentImage !== null && (current === null || signature(current) !== currentImage.identity + ':' + String(currentImage.revision)))
      currentImage = clearCapture(currentCapture, currentImage);
  };
  const render = (): void => {
    const reference = comparison?.reference ?? null;
    maps.hidden = reference === null || current === null;
    maps.style.display = maps.hidden ? "none" : "grid";
    if (reference !== null && current !== null) { referenceMap.render(reference); currentMap.render(current); }
    renderDifferences(differenceHost, current, comparison);
    const nextRepairSignature = current === null || comparison === null ? '' : comparison.identity + ':' + String(comparison.revision) + ':' + comparison.operations.join('\u001f');
    if (nextRepairSignature !== repairControlsSignature) {
      repairControlsSignature = nextRepairSignature;
      renderRepairControls(repairControls, current, comparison, (operation) => {
        if (current !== null && !mutating) commands.mend(current.revision, operation);
      });
    }
    renderRepairStatus(repairStatus, comparison);
    updateControls();
  };
  const readComparison = async (observed: ProcgenReadout): Promise<void> => {
    comparisonRequest?.abort();
    const request = new AbortController(); comparisonRequest = request;
    try {
      const result = await transport.execute('craft.procgen.comparison', request.signal);
      if (disposed || request.signal.aborted || current === null || signature(current) !== signature(observed)) return;
      if (!result.succeeded) throw new Error(result.message);
      const parsed = parseComparison(result.message);
      if (parsed.identity !== observed.identity || parsed.revision !== observed.revision) return;
      comparison = parsed;
      validateCaptures(true); render();
    } catch (error: unknown) {
      if (!disposed && !request.signal.aborted && current !== null && signature(current) === signature(observed)) {
        comparison = null;
        notice.textContent = 'Comparison unavailable: ' + errorMessage(error, 'C# comparison read failed.');
        validateCaptures(true); render();
      }
    } finally { if (comparisonRequest === request) comparisonRequest = null; }
  };
  const readBank = async (): Promise<void> => {
    bankRequest?.abort();
    const request = new AbortController(); bankRequest = request;
    refreshBank.disabled = true;
    try {
      const result = await transport.execute('craft.procgen.bank', request.signal);
      if (disposed || request.signal.aborted) return;
      if (!result.succeeded) throw new Error(result.message);
      const entries = parseBank(result.message);
      renderBank(bankHost, entries, (path) => commands.load(path));
      commands.bank?.(entries);
    } catch (error: unknown) {
      if (!disposed && !request.signal.aborted) renderBankFailure(bankHost, errorMessage(error, 'C# candidate bank read failed.'));
    } finally { if (bankRequest === request) bankRequest = null; updateControls(); }
  };
  const attach = (slot: ReturnType<typeof captureSlot>, target: 'reference' | 'current'): void => {
    const file = slot.input.files?.[0];
    const readout = target === 'reference' ? comparison?.reference ?? null : current;
    if (file === undefined || readout === null) { slot.input.value = ''; return; }
    if (target === 'reference') referenceImage = clearCapture(slot, referenceImage);
    else currentImage = clearCapture(slot, currentImage);
    const capture: Capture = { identity: readout.identity, revision: readout.revision, url: URL.createObjectURL(file), name: file.name };
    slot.image.src = capture.url; slot.image.hidden = false;
    slot.caption.textContent = 'Manually attached/unverified capture · ' + capture.name + ' · ' + capture.identity + ' r' + String(capture.revision) + ' · transient browser image, not world proof.';
    if (target === 'reference') referenceImage = capture; else currentImage = capture;
  };

  pin.addEventListener('click', () => { if (current !== null && !mutating) commands.pin(current.revision); });
  refreshBank.addEventListener('click', () => { void readBank(); });
  prepareReceipt.addEventListener('click', () => {
    if (current === null || mutating || comparison?.repair == null) return;
    prepareReceipt.disabled = true; repairStatus.textContent = 'Reading the C# repair receipt…';
    const requested = current;
    void commands.export(requested.revision).then((json) => {
      if (disposed || json === null) return;
      const receipt = parseReceipt(json);
      if (current === null || signature(current) !== signature(requested) || receipt.resultIdentity !== requested.identity) {
        repairStatus.textContent = 'Repair receipt no longer matches the observed candidate. Refresh before preparing a download.';
        return;
      }
      clearReceipt(); receiptUrl = URL.createObjectURL(new Blob([json], { type: 'application/json' }));
      receiptDownload.href = receiptUrl; receiptDownload.download = 'procgen-repair-' + safeName(requested.identity) + '-r' + String(requested.revision) + '.json';
      receiptDownload.hidden = false; repairStatus.textContent = 'Receipt is ready. Select Download repair receipt to save this browser copy.';
    }).catch((error: unknown) => { if (!disposed) repairStatus.textContent = 'Repair receipt unavailable: ' + errorMessage(error, 'C# export read failed.');
    }).finally(() => { if (!disposed) updateControls(); });
  });
  referenceCapture.input.addEventListener('change', () => attach(referenceCapture, 'reference'));
  currentCapture.input.addEventListener('change', () => attach(currentCapture, 'current'));

  return Object.freeze({
    observe: (readout) => {
      const changed = current === null || signature(current) !== signature(readout);
      current = readout;
      if (changed) { comparison = null; clearReceipt(); }
      validateCaptures(); render();
      if (readout.active && !pending(readout.status)) void readComparison(readout);
      else { comparison = null; validateCaptures(true); render(); }
    },
    refreshBank: () => { void readBank(); },
    setMutating: (value) => { mutating = value; updateControls(); },
    unavailable: (reason) => { current = null; comparison = null; clearReceipt(); validateCaptures(); maps.hidden = true; maps.style.display = "none"; renderDifferences(differenceHost, null, null); notice.textContent = 'Comparison unavailable: ' + reason; updateControls(); },
    dispose: () => {
      disposed = true; comparisonRequest?.abort(); bankRequest?.abort(); clearReceipt();
      referenceImage = clearCapture(referenceCapture, referenceImage); currentImage = clearCapture(currentCapture, currentImage);
      referenceMap.dispose(); currentMap.dispose(); details.remove();
    },
  });
}

function renderDifferences(host: HTMLElement, current: ProcgenReadout | null, comparison: Comparison | null): void {
  const diagnosticsOpen = host.querySelector("details")?.open ?? false;
  const section = document.createElement('section');
  const heading = document.createElement('h3'); heading.textContent = 'Candidate differences'; heading.style.cssText = 'font-size:1em;margin:.55rem 0 .2rem;';
  const summary = document.createElement('p'); summary.style.cssText = 'margin:.3rem 0;';
  if (current === null) summary.textContent = 'No current candidate readout.';
  else if (comparison?.reference === null || comparison === null) summary.textContent = 'No baseline yet. Pin this candidate, load another, or apply a repair.';
  else summary.textContent = referenceDescription(comparison.reference) + ' → current ' + shortIdentity(current.identity) + ' r' + String(current.revision) + '.';
  const differences = document.createElement('ul'); differences.style.cssText = 'margin:.25rem 0;padding-left:1.5rem;';
  for (const difference of comparison?.differences ?? []) { const item = document.createElement('li'); item.textContent = difference; differences.append(item); }
  if (differences.childElementCount === 0) { const item = document.createElement('li'); item.textContent = comparison === null ? 'Comparison facts unavailable.' : 'No differences reported.'; differences.append(item); }
  const progression = document.createElement('p'); progression.style.cssText = 'margin:.3rem 0;overflow-wrap:anywhere;';
  progression.textContent = 'Progression · reference: ' + progressionText(comparison?.reference?.analysis ?? null) + ' · current: ' + progressionText(current?.analysis ?? null);
  section.append(heading, summary, differences, progression);
  if (current !== null && comparison?.reference !== null && comparison !== null) {
    const facts = document.createElement('details'); facts.open = diagnosticsOpen;
    const title = document.createElement('summary'); title.textContent = 'Diagnostics and full provenance';
    facts.append(title);
    for (const [label, observed] of [['Reference', comparison.reference], ['Current', current]] as const) {
      const report = document.createElement('p'); report.style.cssText = 'margin:.35rem 0;overflow-wrap:anywhere;';
      const failures = observed.analysis?.contracts.filter((contract) => !contract.passed).map((contract) => contract.requirement) ?? [];
      report.textContent = label + ' · ' + observed.identity + ' r' + String(observed.revision) +
        ' · ' + observed.source + ' · failed requirements: ' + (failures.join('; ') || 'none reported') +
        ' · body checks: ' + (observed.checks.realization ?? 'unavailable') + ' · sightline: ' + observed.checks.information;
      facts.append(report);
    }
    section.append(facts);
  }
  host.replaceChildren(section);
}

function renderRepairControls(host: HTMLElement, current: ProcgenReadout | null, comparison: Comparison | null, mend: (operation: string) => void): void {
  host.replaceChildren();
  const operations = comparison?.operations ?? [];
  if (operations.length === 0) return;
  const label = document.createElement('span'); label.textContent = 'Applicable semantic repair:'; host.append(label);
  for (const operation of operations) {
    const action = button(repairLabel(operation)); action.setAttribute('aria-label', 'Auto-pin the current baseline, then queue semantic repair ' + operation);
    action.addEventListener('click', () => { if (current !== null) mend(operation); }); host.append(action);
  }
}

function renderRepairStatus(status: HTMLElement, comparison: Comparison | null): void {
  if (comparison === null) { status.textContent = 'Comparison facts unavailable.'; return; }
  status.textContent = comparison.repair === null ? (comparison.operations.length > 0 ? 'Choose an applicable repair above. Session edits must be exported to retain them.' : 'No semantic repair is applicable to this candidate.') : repairText(comparison.repair);
}

function renderBank(host: HTMLElement, entries: readonly BankEntry[], load: (path: string) => void): void {
  const section = document.createElement('section');
  const heading = document.createElement('h3'); heading.textContent = 'Candidate bank'; heading.style.cssText = 'font-size:1em;margin:.55rem 0 .2rem;';
  const note = document.createElement('p'); note.textContent = 'C# reports at most 16 admitted existing candidate paths. Select one to load it into the current workbench session.'; note.style.margin = '.3rem 0';
  const table = document.createElement('table'); table.style.cssText = 'border-collapse:collapse;max-width:100%;width:100%;';
  const header = document.createElement('tr');
  for (const value of ['Candidate', 'Plan', 'Progression / diagnostics', '']) { const cell = document.createElement('th'); cell.scope = 'col'; cell.textContent = value; cell.style.cssText = 'border-bottom:1px solid #62748a;padding:.15rem;text-align:left;vertical-align:top;'; header.append(cell); }
  const body = document.createElement('tbody'); body.append(header);
  for (const entry of entries.slice(0, 16)) {
    const row = document.createElement('tr');
    const candidate = document.createElement('td'); candidate.textContent = entry.path + ' · ' + entry.motif + ' · seed ' + entry.seed; candidate.style.cssText = cellStyle();
    const plan = document.createElement('td'); plan.textContent = entry.rooms + ' rooms · ' + entry.routes + ' routes'; plan.style.cssText = cellStyle();
    const diagnostic = document.createElement('td'); diagnostic.textContent = (entry.completable ? 'completable' : 'not completable') + diagnostics(entry); diagnostic.style.cssText = cellStyle();
    const actionCell = document.createElement('td'); actionCell.style.cssText = cellStyle(); const action = button('Load'); action.addEventListener('click', () => load(entry.path)); actionCell.append(action);
    row.append(candidate, plan, diagnostic, actionCell); body.append(row);
  }
  if (entries.length === 0) { const row = document.createElement('tr'); const cell = document.createElement('td'); cell.colSpan = 4; cell.textContent = 'No bank entries reported.'; row.append(cell); body.append(row); }
  table.append(body); const wrapper = document.createElement('div'); wrapper.style.cssText = 'overflow:auto;'; wrapper.append(table); section.append(heading, note, wrapper); host.replaceChildren(section);
}

function renderBankFailure(host: HTMLElement, reason: string): void { const notice = document.createElement('p'); notice.textContent = 'Candidate bank unavailable: ' + reason; notice.style.cssText = 'margin:.35rem 0;overflow-wrap:anywhere;'; host.replaceChildren(notice); }
function captureSlot(label: string): Readonly<{ host: HTMLElement; input: HTMLInputElement; image: HTMLImageElement; caption: HTMLElement }> {
  const host = document.createElement('div'); host.style.cssText = 'border:1px solid #40566f;border-radius:.3rem;padding:.35rem;';
  const field = document.createElement('label'); field.textContent = label + ' (image file) '; const input = document.createElement('input'); input.type = 'file'; input.accept = 'image/*'; field.append(input);
  const image = document.createElement('img'); image.hidden = true; image.alt = label + ' manually attached capture'; image.style.cssText = 'max-height:12rem;max-width:100%;object-fit:contain;margin:.35rem 0;';
  const caption = document.createElement('p'); caption.textContent = 'No attachment.'; caption.style.cssText = 'margin:.25rem 0;overflow-wrap:anywhere;'; host.append(field, image, caption); return { host, input, image, caption };
}
function parseComparison(message: string): Comparison {
  const value: unknown = JSON.parse(message);
  if (!isRecord(value)) throw new Error('C# comparison was not an object.');
  const reference = value.reference;
  if (reference !== null && !isRecord(reference)) throw new Error('C# comparison reference was invalid.');
  return { identity: text(value.identity), revision: number(value.revision), reference: reference === null ? null : reference as ProcgenReadout, differences: strings(value.differences), operations: strings(value.operations), repair: value.repair === null ? null : parseRepair(value.repair) };
}
function parseRepair(value: unknown): RepairReceipt | null {
  if (!isRecord(value)) return null;
  return { parentIdentity: text(value.parentIdentity), resultIdentity: text(value.resultIdentity), operation: text(value.operation), cost: number(value.cost), changedField: text(value.changedField) };
}
function parseReceipt(message: string): Readonly<{ resultIdentity: string }> {
  const value: unknown = JSON.parse(message);
  if (!isRecord(value) || text(value.resultIdentity).length === 0) throw new Error('C# repair receipt was not a strict receipt object.');
  return { resultIdentity: text(value.resultIdentity) };
}
function parseBank(message: string): readonly BankEntry[] { const value: unknown = JSON.parse(message); if (!Array.isArray(value)) throw new Error('C# candidate bank was not an array.'); return value.slice(0, 16).filter(isRecord).map((entry) => ({ path: text(entry.path), identity: text(entry.identity), motif: text(entry.motif), seed: text(entry.seed), rooms: number(entry.rooms), routes: number(entry.routes), completable: entry.completable === true, failures: strings(entry.failures), error: text(entry.error) })); }
function isRecord(value: unknown): value is Record<string, unknown> { return typeof value === 'object' && value !== null && !Array.isArray(value); }
function strings(value: unknown): readonly string[] { return Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string') : []; }
function text(value: unknown): string { return typeof value === 'string' ? value : ''; }
function number(value: unknown): number { return typeof value === 'number' && Number.isFinite(value) ? value : 0; }
function diagnostics(entry: BankEntry): string { const values = [...entry.failures, entry.error].filter((value) => value.length > 0); return values.length === 0 ? '' : ' · ' + values.join('; '); }
function progressionText(analysis: ProcgenReadout['analysis'] | null): string { return analysis === null ? 'unavailable' : (analysis.completable ? 'completable' : 'not completable') + ', ' + analysis.counterexamples.length + ' counterexamples, ' + analysis.unrecoverableStates + ' unrecoverable'; }
function repairText(repair: RepairReceipt): string { return 'Applied ' + repair.operation + ' · cost ' + repair.cost + ' · changed ' + repair.changedField + '.'; }
function referenceDescription(reference: ProcgenReadout): string {
  return reference.status === 'offline parent' || reference.physicalState.room === 'unobserved'
    ? 'Offline parent ' + shortIdentity(reference.identity) + ' (physical checks unobserved)'
    : 'Frozen observation ' + shortIdentity(reference.identity) + ' r' + String(reference.revision);
}
function repairLabel(operation: string): string { return operation.split('-').map((part) => part.length > 0 ? part[0].toUpperCase() + part.slice(1) : part).join(' '); }
function pending(status: string): boolean { return status.trim().toLowerCase().startsWith('pending'); }
function safeName(value: string): string { return value.replace(/[^a-zA-Z0-9._-]/gu, '_').slice(0, 48) || 'candidate'; }
function errorMessage(error: unknown, fallback: string): string { return error instanceof Error && error.message.length > 0 ? error.message : fallback; }
function button(value: string): HTMLButtonElement { const element = document.createElement('button'); element.type = 'button'; element.textContent = value; return element; }
function cellStyle(): string { return 'border-bottom:1px solid #415165;overflow-wrap:anywhere;padding:.15rem;vertical-align:top;'; }

function shortIdentity(value: string): string { return value.slice(0, 12) + "…"; }
