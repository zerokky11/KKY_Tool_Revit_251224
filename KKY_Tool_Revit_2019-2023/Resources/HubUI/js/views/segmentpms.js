import { clear, div, toast, setBusy, showExcelSavedDialog } from '../core/dom.js';
import { renderTopbar } from '../core/topbar.js';
import { post, onHost } from '../core/bridge.js';

const LS_RVT_LIST = 'kky_segmentpms_rvt_list';
const LS_OPTS = 'kky_segmentpms_opts';
const MAX_UI_ROWS = 200;

function loadRvtList() {
  try {
    const arr = JSON.parse(localStorage.getItem(LS_RVT_LIST) || '[]');
    if (Array.isArray(arr)) return arr;
  } catch { }
  return [];
}

function saveRvtList(list) {
  localStorage.setItem(LS_RVT_LIST, JSON.stringify(list || []));
}

function loadOpts() {
  try {
    return Object.assign({ tolMm: 0.01, ndRound: 3, unit: 'mm', classMatch: false }, JSON.parse(localStorage.getItem(LS_OPTS) || '{}'));
  } catch {
    return { tolMm: 0.01, ndRound: 3, unit: 'mm', classMatch: false };
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
    groups: [],
    suggestions: new Map(), // key: groupKey -> {cls, segment}
    selections: new Map(), // groupKey -> {cls, segment, source}
    results: null,
    busy: false
  };

  const page = div('feature-shell segmentpms-page');
  const header = div('feature-header');
  const heading = div('feature-heading');
  heading.innerHTML = `<span class="feature-kicker">PipeType - PMS</span><h2 class="feature-title">Segment 매핑/검증</h2><p class="feature-sub">추출(Excel)과 PMS를 분리하여 그룹 단위 매핑 후 비교합니다.</p>`;
  header.append(heading);
  page.append(header);

  /* Options */
  const control = div('segmentpms-control section');
  control.innerHTML = `<div class="segmentpms-row">
    <label>ND Join Round</label><input type="number" value="${opts.ndRound || 3}" min="0" max="6" step="1">
    <label>허용오차(mm)</label><input type="number" value="${opts.tolMm || 0.01}" step="0.01">
    <label>PMS 단위</label><select><option value="mm">mm</option><option value="inch">inch</option></select>
    <label class="segmentpms-toggle"><input type="checkbox"> Class 재질 매칭 검토</label>
  </div>`;
  const numRound = control.querySelector('input[type="number"]');
  const tolBox = control.querySelectorAll('input[type="number"]')[1];
  const unitSel = control.querySelector('select'); unitSel.value = opts.unit || 'mm';
  const classMatchCk = control.querySelector('input[type="checkbox"]'); classMatchCk.checked = !!opts.classMatch;
  numRound.addEventListener('change', commitOpts); tolBox.addEventListener('change', commitOpts); unitSel.addEventListener('change', commitOpts); classMatchCk.addEventListener('change', commitOpts);
  function commitOpts() { saveOpts({ ndRound: parseInt(numRound.value || '3', 10), tolMm: parseFloat(tolBox.value || '0.01'), unit: String(unitSel.value || 'mm'), classMatch: !!classMatchCk.checked }); }
  page.append(control);

  /* Extract section */
  const extractSection = div('section segmentpms-extract');
  const exHeader = document.createElement('div'); exHeader.className = 'section-header';
  exHeader.innerHTML = '<h3>1단계: 추출 (RVT → Excel)</h3>';
  const exActions = div('segmentpms-actions-row');
  const btnAddRvt = cardBtn('RVT 파일 추가', () => post('segmentpms:rvt-pick-files', {}));
  const btnAddFolder = cardBtn('RVT 폴더 추가', () => post('segmentpms:rvt-pick-folder', {}));
  const btnRemoveSel = cardBtn('선택 제거', removeCheckedRvt);
  const btnClearAll = cardBtn('등록 목록 비우기', () => { state.rvtList = []; state.rvtChecked.clear(); persistRvt(); renderRvtList(); updateButtons(); });
  const btnExtract = cardBtn('추출 시작', onExtract);
  const btnSaveExtract = cardBtn('추출 결과 저장(Excel)', () => post('segmentpms:save-extract', {}));
  exActions.append(btnAddRvt, btnAddFolder, btnRemoveSel, btnClearAll, btnExtract, btnSaveExtract);
  exHeader.append(exActions);

  const rvtTable = document.createElement('table'); rvtTable.className = 'segmentpms-table';
  rvtTable.innerHTML = '<thead><tr><th><input type="checkbox"></th><th>파일 경로</th></tr></thead><tbody></tbody>';
  const rvtBody = rvtTable.querySelector('tbody');
  const extractInfo = div('segmentpms-summary'); extractInfo.textContent = '추출 상태: 미실행';
  const extractLoadRow = div('segmentpms-actions-row');
  const btnLoadExtract = cardBtn('추출 결과 불러오기', () => post('segmentpms:load-extract', {}));
  extractLoadRow.append(btnLoadExtract);
  extractSection.append(exHeader, rvtTable, extractLoadRow, extractInfo);
  page.append(extractSection);

  /* Check section */
  const checkSection = div('section segmentpms-check');
  const chHeader = document.createElement('div'); chHeader.className = 'section-header';
  chHeader.innerHTML = '<h3>2단계: 검토 (추출 Excel + PMS)</h3>';
  const chActions = div('segmentpms-actions-row');
  const btnRegisterPms = cardBtn('PMS 등록/업데이트', () => { setBusy(true, 'PMS 불러오는 중'); state.busy = true; updateButtons(); post('segmentpms:register-pms', { unit: unitSel.value }); });
  const btnPrepare = cardBtn('매핑 준비', onPrepareMapping);
  const btnRun = cardBtn('검토 시작', onRun);
  const btnSave = cardBtn('검토 결과 저장', () => {
    if (!state.results) { toast('저장할 결과가 없습니다.', 'err'); return; }
    post('segmentpms:save-result', {});
  });
  chActions.append(btnLoadExtract, btnRegisterPms, btnPrepare, btnRun, btnSave);
  chHeader.append(chActions);
  checkSection.append(chHeader);

  const groupTable = document.createElement('table'); groupTable.className = 'segmentpms-table';
  groupTable.innerHTML = '<thead><tr><th>Revit Segment 그룹</th><th>사용처</th><th>PMS Segment</th><th>추천</th></tr></thead><tbody></tbody>';
  const groupBody = groupTable.querySelector('tbody');
  checkSection.append(groupTable);

  const resInfo = div('segmentpms-summary'); resInfo.textContent = '결과 없음';
  const resTable = document.createElement('table'); resTable.className = 'segmentpms-table';
  resTable.innerHTML = '<thead><tr><th>파일</th><th>PipeType</th><th>Rule</th><th>Revit Segment</th><th>CLASS</th><th>PMS Segment</th><th>ND(mm)</th><th>Revit ID/OD</th><th>PMS ID/OD</th><th>Status</th><th>Class 매칭</th></tr></thead><tbody></tbody>';
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
    post('segmentpms:extract', { files: targets, options: { ndRound: parseInt(numRound.value || '3', 10), tolMm: parseFloat(tolBox.value || '0.01') } });
  }

  function onPrepareMapping() {
    if (!state.extractLoaded) { toast('추출 결과를 먼저 불러오세요.', 'err'); return; }
    setBusy(true, '매핑 준비'); state.busy = true; updateButtons();
    post('segmentpms:prepare-mapping', {});
  }

  function buildGroupTable() {
    groupBody.innerHTML = '';
    state.selections.clear();
    if (!state.groups.length) {
      const tr = document.createElement('tr'); const td1 = document.createElement('td'); td1.colSpan = 4; td1.textContent = '추출 결과와 PMS를 불러와 매핑을 준비하세요.'; tr.append(td1); groupBody.append(tr); updateButtons(); return;
    }
    state.groups.forEach(g => {
      const tr = document.createElement('tr');
      tr.append(td(g.displayKey || g.groupKey));
      tr.append(td(g.usageSummary || ''));

      const pmsSel = document.createElement('select');
      fillPmsOptions(pmsSel);
      const suggLabel = document.createElement('small'); suggLabel.className = 'segmentpms-suggest';

      const applySuggestion = () => {
        const sug = state.suggestions.get(g.groupKey);
        if (sug && sug.pmsSegmentKey) {
          pmsSel.value = `${sug.pmsClass}|||${sug.pmsSegmentKey}`;
          suggLabel.textContent = '추천 적용';
        } else if (g.suggestedSegmentKey) {
          pmsSel.value = `${g.suggestedClass}|||${g.suggestedSegmentKey}`;
          suggLabel.textContent = '추천 적용';
        } else {
          suggLabel.textContent = '';
        }
      };
      applySuggestion();
      commitSelection(g.groupKey, pmsSel.value, suggLabel.textContent ? 'Suggest' : 'Manual');

      pmsSel.onchange = () => {
        commitSelection(g.groupKey, pmsSel.value, 'Manual');
        suggLabel.textContent = '';
      };

      const tdPms = document.createElement('td'); tdPms.append(pmsSel);
      const tdSug = document.createElement('td'); tdSug.append(suggLabel);
      tr.append(tdPms, tdSug);
      groupBody.append(tr);
    });
    updateButtons();
  }

  function commitSelection(groupKey, pmsVal, source) {
    const val = String(pmsVal || '');
    const parts = val.split('|||');
    const cls = parts[0] || '';
    const seg = parts[1] || '';
    state.selections.set(groupKey, { groupKey, cls, segment: seg, source: source || 'Manual' });
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
    const groups = [...state.selections.values()];
    setBusy(true, '검토 실행'); state.busy = true; updateButtons();
    post('segmentpms:run', { groups, ndRound: parseInt(numRound.value || '3', 10), tolMm: parseFloat(tolBox.value || '0.01'), classMatch: !!classMatchCk.checked });
  }

  function paintResults(payload) {
    state.results = payload || { hasResult: true };
    resBody.innerHTML = '';
    const rows = payload?.compare || [];
    const total = payload?.totalCount || rows.length;
    const show = rows.slice(0, MAX_UI_ROWS);
    if (total === 0) {
      resInfo.textContent = '결과 없음';
    } else if (total <= MAX_UI_ROWS) {
      resInfo.textContent = `총 ${total}건`;
    } else {
      resInfo.textContent = `총 ${total}건 (화면에는 ${MAX_UI_ROWS}건만 표시, 전체는 엑셀 저장에서 확인)`;
    }
    show.forEach(r => {
      const tr = document.createElement('tr');
      tr.append(td(r.File), td(r.PipeTypeName), td(r.SegmentRuleIndex), td(r.RevitSegmentKey), td(r.CLASS), td(r.PMS_SegmentKey), td(r.ND_mm));
      tr.append(td(`${r.Revit_ID}/${r.Revit_OD}`));
      tr.append(td(`${r.PMS_ID}/${r.PMS_OD}`));
      const status = td(r.Status); status.dataset.status = String(r.Status || ''); tr.append(status);
      tr.append(td(r.ClassMatchStatus));
      resBody.append(tr);
    });
  }

  function updateButtons() {
    btnRemoveSel.disabled = state.busy || state.rvtChecked.size === 0;
    btnClearAll.disabled = state.busy || state.rvtList.length === 0;
    btnExtract.disabled = state.busy || state.rvtChecked.size === 0;
    btnSaveExtract.disabled = state.busy || !state.extractLoaded;
    btnLoadExtract.disabled = state.busy;
    btnRegisterPms.disabled = state.busy;
    btnPrepare.disabled = state.busy || !state.extractLoaded;
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
        state.extractLoaded = true;
        state.extractSummary = msg.payload?.summary || '';
        state.extractPath = msg.payload?.path || '';
        state.results = null;
        extractInfo.textContent = `추출 완료: ${state.extractSummary} (${state.extractPath})`;
        toast('추출을 완료했습니다.', 'ok');
        post('segmentpms:prepare-mapping', {});
        updateButtons();
        break;
      case 'segmentpms:extract-loaded':
        setBusy(false); state.busy = false;
        state.extractLoaded = true;
        state.extractSummary = msg.payload?.summary || '';
        state.extractPath = msg.payload?.path || '';
        state.results = null;
        extractInfo.textContent = `추출 로드: ${state.extractSummary} (${state.extractPath})`;
        state.groups = msg.payload?.groups || [];
        state.pmsOpts = msg.payload?.pms || state.pmsOpts;
        state.suggestions = buildSuggestionMap(msg.payload?.suggestions || []);
        buildGroupTable();
        updateButtons();
        break;
      case 'segmentpms:mapping-ready':
        setBusy(false); state.busy = false;
        state.groups = msg.payload?.groups || [];
        state.pmsOpts = msg.payload?.pms || state.pmsOpts;
        state.suggestions = buildSuggestionMap(msg.payload?.suggestions || []);
        buildGroupTable();
        updateButtons();
        break;
      case 'segmentpms:pms-registered':
        setBusy(false); state.busy = false;
        state.pmsLoaded = true;
        state.pmsOpts = msg.payload?.options || [];
        state.suggestions = buildSuggestionMap(msg.payload?.suggestions || []);
        fillPmsOptions(document.createElement('select'));
        if (state.extractLoaded) post('segmentpms:prepare-mapping', {});
        toast('PMS를 등록했습니다.', 'ok');
        updateButtons();
        break;
      case 'segmentpms:result':
        setBusy(false); state.busy = false;
        paintResults(msg.payload || {});
        updateButtons();
        break;
      case 'segmentpms:saved':
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
    const key = s.groupKey || s.GroupKey || s.segmentKey || s.SegmentKey || s.file;
    if (!key) return;
    map.set(String(key), { pmsClass: s.pmsClass || s.PmsClass, pmsSegmentKey: s.pmsSegmentKey || s.PmsSegmentKey });
  });
  return map;
}

function cardBtn(label, onclick) {
  const b = document.createElement('button');
  b.type = 'button'; b.className = 'btn card-btn'; b.textContent = label; b.onclick = onclick; return b;
}

function td(v) { const t = document.createElement('td'); t.textContent = v == null ? '' : v; return t; }
