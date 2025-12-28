import { clear, div, toast, setBusy, showExcelSavedDialog } from '../core/dom.js';
import { renderTopbar } from '../core/topbar.js';
import { post, onHost } from '../core/bridge.js';

const LS_RVT_LIST = 'kky_segmentpms_rvt_list';
const LS_OPTS = 'kky_segmentpms_opts';

function loadRvtList() {
  try {
    const arr = JSON.parse(localStorage.getItem(LS_RVT_LIST) || '[]');
    if (Array.isArray(arr)) return arr;
    return [];
  } catch {
    return [];
  }
}

function saveRvtList(list) {
  localStorage.setItem(LS_RVT_LIST, JSON.stringify(list || []));
}

function loadOpts() {
  try {
    return Object.assign({ tolMm: 0.01, ndRound: 3, unit: 'mm' }, JSON.parse(localStorage.getItem(LS_OPTS) || '{}'));
  } catch {
    return { tolMm: 0.01, ndRound: 3, unit: 'mm' };
  }
}

function saveOpts(o) {
  localStorage.setItem(LS_OPTS, JSON.stringify(o));
}

export function renderSegmentPms() {
  const root = document.getElementById('app'); clear(root);
  renderTopbar(root, true); const top = root.firstElementChild; if (top) top.classList.add('hub-topbar');

  const opts = loadOpts();
  const state = {
    rvtList: loadRvtList(),
    rvtChecked: new Set(loadRvtList()),
    extractLoaded: false,
    extractSummary: '',
    extractPath: '',
    pmsLoaded: false,
    pmsOpts: [],
    pipes: [],
    suggestions: new Map(), // key: file|pipe -> {cls, segment}
    mappings: new Map(),
    results: null,
    busy: false
  };

  const page = div('feature-shell segmentpms-page');
  const header = div('feature-header');
  const heading = div('feature-heading');
  heading.innerHTML = `<span class="feature-kicker">PipeType - PMS</span><h2 class="feature-title">Segment 매핑/검증</h2><p class="feature-sub">RVT를 추출한 뒤 PMS와 매핑하여 ND별 ID/OD를 비교합니다.</p>`;
  header.append(heading);
  page.append(header);

  /* Options */
  const control = div('segmentpms-control section');
  control.innerHTML = `<div class="segmentpms-row">
    <label>ND Join Round</label><input type="number" value="${opts.ndRound || 3}" min="0" max="6" step="1">
    <label>허용오차(mm)</label><input type="number" value="${opts.tolMm || 0.01}" step="0.01">
    <label>PMS 단위</label><select><option value="mm">mm</option><option value="inch">inch</option></select>
  </div>`;
  const numRound = control.querySelector('input[type="number"]');
  const tolBox = control.querySelectorAll('input[type="number"]')[1];
  const unitSel = control.querySelector('select'); unitSel.value = opts.unit || 'mm';
  numRound.addEventListener('change', commitOpts); tolBox.addEventListener('change', commitOpts); unitSel.addEventListener('change', commitOpts);
  function commitOpts() { saveOpts({ ndRound: parseInt(numRound.value || '3'), tolMm: parseFloat(tolBox.value || '0.01'), unit: String(unitSel.value || 'mm') }); }
  page.append(control);

  /* Extract section */
  const extractSection = div('section segmentpms-extract');
  const exHeader = document.createElement('div'); exHeader.className = 'section-header';
  exHeader.innerHTML = '<h3>1단계: 추출 (RVT → Excel)</h3>';
  const exActions = div('segmentpms-actions-row');
  const btnAddRvt = cardBtn('RVT 추가(파일)', () => post('segmentpms:rvt-pick-files', {}));
  const btnAddFolder = cardBtn('RVT 폴더 추가', () => post('segmentpms:rvt-pick-folder', {}));
  const btnRemoveSel = cardBtn('선택 제거', removeCheckedRvt);
  const btnClearAll = cardBtn('전체 비우기', () => { state.rvtList = []; state.rvtChecked.clear(); persistRvt(); renderRvtList(); updateButtons(); });
  const btnExtract = cardBtn('추출 시작', onExtract);
  exActions.append(btnAddRvt, btnAddFolder, btnRemoveSel, btnClearAll, btnExtract);
  exHeader.append(exActions);

  const rvtTable = document.createElement('table'); rvtTable.className = 'segmentpms-table';
  rvtTable.innerHTML = '<thead><tr><th><input type="checkbox"></th><th>파일 경로</th></tr></thead><tbody></tbody>';
  const rvtBody = rvtTable.querySelector('tbody');
  const extractInfo = div('segmentpms-summary'); extractInfo.textContent = '추출 상태: 미실행';
  extractSection.append(exHeader, rvtTable, extractInfo);
  page.append(extractSection);

  /* Check section */
  const checkSection = div('section segmentpms-check');
  const chHeader = document.createElement('div'); chHeader.className = 'section-header';
  chHeader.innerHTML = '<h3>2단계: 검토 (추출 Excel + PMS)</h3>';
  const chActions = div('segmentpms-actions-row');
  const btnLoadExtract = cardBtn('추출 Excel 불러오기', () => post('segmentpms:load-extract', {}));
  const btnRegisterPms = cardBtn('PMS Excel 등록/업데이트', () => post('segmentpms:register-pms', { unit: unitSel.value }));
  const btnRun = cardBtn('검토 시작', onRun);
  const btnSave = cardBtn('결과 엑셀 저장', () => {
    if (!state.results) { toast('저장할 결과가 없습니다.', 'err'); return; }
    post('segmentpms:save-excel', state.results);
  });
  chActions.append(btnLoadExtract, btnRegisterPms, btnRun, btnSave);
  chHeader.append(chActions);
  checkSection.append(chHeader);

  const mapTable = document.createElement('table'); mapTable.className = 'segmentpms-table';
  mapTable.innerHTML = '<thead><tr><th>파일</th><th>PipeType</th><th>Revit Segment</th><th>PMS Segment</th><th>추천</th></tr></thead><tbody></tbody>';
  const mapBody = mapTable.querySelector('tbody');
  checkSection.append(mapTable);

  const resInfo = div('segmentpms-summary'); resInfo.textContent = '결과 없음';
  const resTable = document.createElement('table'); resTable.className = 'segmentpms-table';
  resTable.innerHTML = '<thead><tr><th>파일</th><th>PipeType</th><th>Rule</th><th>Revit Segment</th><th>CLASS</th><th>PMS Segment</th><th>ND(mm)</th><th>Revit ID/OD</th><th>PMS ID/OD</th><th>Status</th></tr></thead><tbody></tbody>';
  const resBody = resTable.querySelector('tbody');
  checkSection.append(resInfo, resTable);
  page.append(checkSection);

  root.append(page);

  renderRvtList();
  updateButtons();
  onHost(handleHost);

  function persistRvt() { saveRvtList(state.rvtList); }

  function renderRvtList() {
    const allChecked = state.rvtList.length > 0 && state.rvtList.every(f => state.rvtChecked.has(f));
    const master = rvtTable.querySelector('thead input[type="checkbox"]');
    master.checked = allChecked;
    master.onchange = () => {
      if (master.checked) state.rvtChecked = new Set(state.rvtList);
      else state.rvtChecked.clear();
      renderRvtList();
      updateButtons();
    };
    rvtBody.innerHTML = '';
    state.rvtList.forEach(p => {
      const tr = document.createElement('tr');
      const ck = document.createElement('input'); ck.type = 'checkbox'; ck.checked = state.rvtChecked.has(p);
      ck.onchange = () => { if (ck.checked) state.rvtChecked.add(p); else state.rvtChecked.delete(p); updateButtons(); };
      const tdCk = document.createElement('td'); tdCk.append(ck);
      const tdPath = document.createElement('td'); tdPath.textContent = p;
      tr.append(tdCk, tdPath);
      rvtBody.append(tr);
    });
  }

  function removeCheckedRvt() {
    if (!state.rvtChecked.size) { toast('제거할 항목을 선택하세요.', 'err'); return; }
    state.rvtList = state.rvtList.filter(p => !state.rvtChecked.has(p));
    state.rvtChecked.clear(); persistRvt(); renderRvtList(); updateButtons();
  }

  function onExtract() {
    const targets = state.rvtList.filter(p => state.rvtChecked.has(p));
    if (!targets.length) { toast('추출할 RVT를 선택하세요.', 'err'); return; }
    setBusy(true, '추출 중'); state.busy = true; updateButtons();
    post('segmentpms:extract-start', { files: targets, options: { ndRound: parseInt(numRound.value || '3', 10) } });
  }

  function buildMapTable() {
    mapBody.innerHTML = '';
    state.mappings.clear();
    if (!state.pipes.length) {
      const tr = document.createElement('tr'); const td1 = document.createElement('td'); td1.colSpan = 5; td1.textContent = '추출 결과를 불러오세요.'; tr.append(td1); mapBody.append(tr); updateButtons(); return;
    }
    state.pipes.forEach(row => {
      const tr = document.createElement('tr');
      tr.append(td(shortFile(row.file)), td(row.pipeType));

      const revSel = document.createElement('select');
      row.candidates.forEach(c => revSel.append(new Option(`${c.ruleIndex} | ${c.segmentKey || '(미지정)'}`, `${c.ruleIndex}`)));
      revSel.value = String(row.defaultRuleIndex ?? row.candidates[0]?.ruleIndex ?? 0);

      const pmsSel = document.createElement('select');
      fillPmsOptions(pmsSel);

      const suggLabel = document.createElement('small'); suggLabel.className = 'segmentpms-suggest';

      const mapKey = `${row.file}|${row.pipeType}`;
      const sugKey = () => `${row.file}|${row.pipeType}|${revSel.value}`;
      const applySuggestion = () => {
        const sug = state.suggestions.get(sugKey());
        if (sug && sug.pmsSegmentKey) {
          pmsSel.value = `${sug.pmsClass}|||${sug.pmsSegmentKey}`;
          suggLabel.textContent = '추천 적용';
        } else {
          suggLabel.textContent = '';
        }
      };

      const currentCand = () => row.candidates.find(c => String(c.ruleIndex) === revSel.value) || row.candidates[0];
      applySuggestion();
      commitMap(mapKey, currentCand(), pmsSel.value, suggLabel.textContent ? 'Suggest' : 'Manual');

      revSel.onchange = () => {
        applySuggestion();
        commitMap(mapKey, currentCand(), pmsSel.value, suggLabel.textContent ? 'Suggest' : 'Manual');
      };
      pmsSel.onchange = () => {
        commitMap(mapKey, currentCand(), pmsSel.value, 'Manual');
        suggLabel.textContent = '';
      };

      const tdRevit = document.createElement('td'); tdRevit.append(revSel);
      const tdPms = document.createElement('td'); tdPms.append(pmsSel);
      const tdSug = document.createElement('td'); tdSug.append(suggLabel);
      tr.append(tdRevit, tdPms, tdSug);
      mapBody.append(tr);
    });
    updateButtons();
  }

  function commitMap(mapKey, cand, pmsVal, source) {
    const chosen = cand || {};
    const val = String(pmsVal || '');
    const parts = val.split('|||');
    const cls = parts[0] || '';
    const seg = parts[1] || '';
    const mapObj = {
      file: mapKey.split('|')[0],
      pipeType: mapKey.split('|')[1],
      ruleIndex: chosen.ruleIndex || 0,
      segmentId: chosen.segmentId || 0,
      segmentKey: chosen.segmentKey || '',
      cls: cls,
      segment: seg,
      source: source || 'Manual'
    };
    state.mappings.set(mapKey, mapObj);
  }

  function fillPmsOptions(sel) {
    const oldVal = sel.value;
    sel.innerHTML = '';
    sel.append(new Option('(선택)', ''));
    const added = new Set();
    state.pmsOpts.forEach(o => {
      const key = `${o.cls}|||${o.segment}`;
      if (added.has(key)) return;
      added.add(key);
      sel.append(new Option(o.label, key));
    });
    sel.value = oldVal;
  }

  function onRun() {
    if (!state.extractLoaded) { toast('추출 데이터를 먼저 불러오세요.', 'err'); return; }
    if (!state.pmsLoaded) { toast('PMS를 등록하세요.', 'err'); return; }
    const mappings = [...state.mappings.values()];
    setBusy(true, '검토 실행'); state.busy = true; updateButtons();
    post('segmentpms:run', { mappings, ndRound: parseInt(numRound.value || '3', 10), tolMm: parseFloat(tolBox.value || '0.01') });
  }

  function paintResults(payload) {
    state.results = payload;
    resBody.innerHTML = '';
    const rows = payload?.compare || [];
    resInfo.textContent = `총 ${rows.length}건`;
    rows.slice(0, 400).forEach(r => {
      const tr = document.createElement('tr');
      tr.append(td(r.File), td(r.PipeTypeName), td(r.SegmentRuleIndex), td(r.RevitSegmentKey), td(r.CLASS), td(r.PMS_SegmentKey), td(r.ND_mm));
      tr.append(td(`${r.Revit_ID}/${r.Revit_OD}`));
      tr.append(td(`${r.PMS_ID}/${r.PMS_OD}`));
      const status = td(r.Status); status.dataset.status = String(r.Status || ''); tr.append(status); resBody.append(tr);
    });
  }

  function updateButtons() {
    btnRemoveSel.disabled = state.busy || state.rvtChecked.size === 0;
    btnClearAll.disabled = state.busy || state.rvtList.length === 0;
    btnExtract.disabled = state.busy || state.rvtChecked.size === 0;
    btnLoadExtract.disabled = state.busy;
    btnRegisterPms.disabled = state.busy;
    btnRun.disabled = state.busy || !state.extractLoaded || !state.pmsLoaded;
    btnSave.disabled = state.busy || !state.results;
  }

  function handleHost(msg) {
    if (!msg || !msg.ev) return;
    switch (msg.ev) {
      case 'segmentpms:rvt-picked-files':
      case 'segmentpms:rvt-picked-folder': {
        const files = Array.isArray(msg.payload?.paths) ? msg.payload.paths : [];
        files.forEach(f => { if (!state.rvtList.some(x => x.toLowerCase() === String(f).toLowerCase())) state.rvtList.push(f); });
        state.rvtChecked = new Set(files);
        persistRvt(); renderRvtList(); updateButtons();
        break;
      }
      case 'segmentpms:extract-saved':
        setBusy(false); state.busy = false;
        state.extractLoaded = false;
        state.extractSummary = msg.payload?.summary || '';
        state.extractPath = msg.payload?.path || '';
        state.pipes = [];
        state.mappings.clear();
        state.results = null;
        extractInfo.textContent = `추출 완료: ${state.extractSummary} (${state.extractPath})`;
        toast('추출을 완료했습니다.', 'ok');
        updateButtons();
        break;
      case 'segmentpms:extract-loaded':
        setBusy(false); state.busy = false;
        state.extractLoaded = true;
        state.extractSummary = msg.payload?.summary || '';
        state.extractPath = msg.payload?.path || '';
        state.results = null;
        extractInfo.textContent = `추출 로드: ${state.extractSummary} (${state.extractPath})`;
        state.pipes = msg.payload?.pipes || [];
        state.pmsOpts = msg.payload?.pms || state.pmsOpts;
        state.suggestions = buildSuggestionMap(msg.payload?.suggestions || []);
        buildMapTable();
        updateButtons();
        break;
      case 'segmentpms:pms-registered':
        setBusy(false); state.busy = false;
        state.pmsLoaded = true;
        state.pmsOpts = msg.payload?.options || [];
        state.suggestions = buildSuggestionMap(msg.payload?.suggestions || []);
        fillPmsOptions(document.createElement('select'));
        if (state.pipes.length) buildMapTable();
        toast('PMS를 등록했습니다.', 'ok');
        updateButtons();
        break;
      case 'segmentpms:suggestion':
        state.suggestions = buildSuggestionMap(msg.payload?.suggestions || []);
        if (state.pipes.length) buildMapTable();
        break;
      case 'segmentpms:result':
        setBusy(false); state.busy = false;
        paintResults(msg.payload || {});
        updateButtons();
        break;
      case 'segmentpms:result-saved':
        showExcelSavedDialog('결과를 저장했습니다.', msg.payload?.path);
        break;
      case 'segmentpms:error':
        setBusy(false); state.busy = false;
        toast(msg.payload?.message || '오류가 발생했습니다.', 'err');
        updateButtons();
        break;
      default: break;
    }
  }
}

function buildSuggestionMap(list) {
  const map = new Map();
  if (!Array.isArray(list)) return map;
  list.forEach(s => {
    const key = `${s.file}|${s.pipeTypeName || s.pipeType}|${s.ruleIndex}`;
    map.set(key, { pmsClass: s.pmsClass || s.PmsClass, pmsSegmentKey: s.pmsSegmentKey || s.PmsSegmentKey });
  });
  return map;
}

function cardBtn(label, onclick) {
  const b = document.createElement('button');
  b.type = 'button'; b.className = 'btn card-btn'; b.textContent = label; b.onclick = onclick; return b;
}

function td(v) { const t = document.createElement('td'); t.textContent = v == null ? '' : v; return t; }
function shortFile(p) { return String(p || '').split(/[/\\]/).pop(); }
