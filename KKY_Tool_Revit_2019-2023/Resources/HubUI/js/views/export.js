import { clear, div, tdText, toast, setBusy, showExcelSavedDialog } from '../core/dom.js';
import { renderTopbar } from '../core/topbar.js';
import { post, onHost } from '../core/bridge.js';

const state = { files: [], rowsRaw: [], folder: '', unit: 'ft' };
const FT_TO_M = 0.3048;
const HEADERS = [
  { key: 'ProjectPoint_E(mm)', label: 'E/W', group: 'project' },
  { key: 'ProjectPoint_N(mm)', label: 'N/S', group: 'project' },
  { key: 'ProjectPoint_Z(mm)', label: 'Elev', group: 'project' },
  { key: 'SurveyPoint_E(mm)', label: 'E/W', group: 'survey' },
  { key: 'SurveyPoint_N(mm)', label: 'N/S', group: 'survey' },
  { key: 'SurveyPoint_Z(mm)', label: 'Elev', group: 'survey' }
];

export function renderExport() {
    const root = document.getElementById('app'); clear(root);
    renderTopbar(root, true, () => { location.hash = ''; });

    const page = div('feature-shell');

    const header = div('feature-header');
    const heading = div('feature-heading');
    heading.innerHTML = `
      <span class="feature-kicker">Export Points with Angle</span>
      <h2 class="feature-title">좌표/북각 추출</h2>
      <p class="feature-sub">RVT 폴더를 선택해 포인트/북각을 미리보기 후 Excel로 저장합니다.</p>`;

    const pick = cardBtn('폴더 선택', () => post('export:browse-folder', {}));
    const preview = cardBtn('추출 시작', () => {
      const targets = selectedFilePaths();
      if (!targets.length) { toast('미리볼 파일을 선택하세요.', 'warn'); return; }
      setBusy(true, '미리보기 생성…');
      post('export:preview', { files: targets, unit: state.unit });
    });
    preview.id = 'btnExPreview'; preview.disabled = true;
    const save = cardBtn('엑셀 내보내기', () => post('export:save-excel', { rows: convertRowsForSave(), unit: state.unit, files: selectedFilePaths() }));
    save.id = 'btnExSave'; save.disabled = true;
    const actions = div('feature-actions');
    actions.append(pick, preview, save);
    header.append(heading, actions);
    page.append(header);

    const wrap = div('kkyt-stack');

    const left = div('kkyt-left feature-results-panel');
    const lbar = div('kkyt-toolbar');
    const clearBtn = cardBtn('목록 지우기', () => {
      state.files = [];
      state.folder = '';
      state.rowsRaw = [];
      renderFiles();
      repaintRows();
      syncSaveState();
    });
    const unitToggle = buildUnitToggle();
    lbar.append(clearBtn, unitToggle);
    const listWrap = document.createElement('div'); listWrap.className = 'kkyt-list export-file-list';
    const tblWrap = document.createElement('table'); tblWrap.className = 'kkyt-file-table';
    const filesHead = document.createElement('thead');
    const filesBody = document.createElement('tbody');
    tblWrap.append(filesHead, filesBody);
    listWrap.append(tblWrap);
    const info = div('kkyt-hint'); info.textContent = '파일 0개';
    left.append(lbar, listWrap, info);

    const right = div('kkyt-right feature-results-panel');
    const tbl = document.createElement('table'); tbl.className = 'kkyt-table';
    const thead = document.createElement('thead');
    paintHead(thead);
    const tbody = document.createElement('tbody'); tbl.append(thead, tbody);

    right.append(tbl);
    wrap.append(left, right);
    page.append(wrap);
    root.append(page);

    // === 파일 선택 응답 ===
    onHost('export:files', ({ files, folder }) => {
        const list = Array.isArray(files) ? files : [];
        const root = folder || commonDir(list);
        state.folder = root || '';
        state.files = list.map(path => ({
          path,
          rel: relPath(path, root),
          checked: true
        }));
        renderFiles();
    });

    // === 미리보기 결과 ===
    onHost('export:previewed', ({ rows }) => {
        setBusy(false);
        state.rowsRaw = Array.isArray(rows) ? rows : [];
        repaintRows();
        syncSaveState();
        toast(`미리보기 ${state.rowsRaw.length}행`, 'ok');
    });

    // === 저장 결과 ===
    onHost('export:saved', ({ path }) => {
        const p = path || '';
        if (p) {
            showExcelSavedDialog('엑셀 파일을 저장했습니다.', p, (fp) => {
                if (fp) post('excel:open', { path: fp });
            });
        } else {
            toast('엑셀 파일이 저장되었습니다.', 'ok', 2600);
        }
    });

    // === 에러 공통 처리(중요) ===
    onHost('revit:error', ({ message }) => { setBusy(false); toast(message || 'Revit 오류가 발생했습니다.', 'err', 3200); });
    onHost('host:error', ({ message }) => { setBusy(false); toast(message || '호스트 오류가 발생했습니다.', 'err', 3200); });

    function renderFiles() {
        while (filesBody.firstChild) filesBody.removeChild(filesBody.firstChild);
        const allChecked = state.files.length && state.files.every(f => f.checked);
        filesHead.innerHTML = '';
        const headRow = document.createElement('tr');
        const masterCell = document.createElement('th');
        const master = document.createElement('input'); master.type = 'checkbox'; master.checked = allChecked;
        master.onchange = () => { state.files = state.files.map(f => ({ ...f, checked: master.checked })); renderFiles(); };
        masterCell.append(master);
        headRow.append(masterCell);
        ['파일명', '상대 경로'].forEach(text => { const th = document.createElement('th'); th.textContent = text; headRow.append(th); });
        filesHead.append(headRow);

        state.files.forEach((f, idx) => {
          const row = document.createElement('tr');
          const ckCell = document.createElement('td');
          const ck = document.createElement('input'); ck.type = 'checkbox'; ck.checked = !!f.checked;
          ck.onchange = () => { state.files[idx].checked = ck.checked; updateSelectionSummary(); syncPreviewState(); };
          ckCell.append(ck); row.append(ckCell);
          row.append(tdText(f.path.split(/[/\\]/).pop() || '—'));
          row.append(tdText(f.rel || ''));
          filesBody.append(row);
        });
        updateSelectionSummary();
        syncPreviewState();
    }

    function updateSelectionSummary() {
        const folder = state.folder || (state.files[0] ? state.files[0].path?.replace?.(/[/\\][^/\\]+$/, '') : '');
        const picked = state.files.filter(f => f.checked).length;
        info.textContent = `${folder ? ('경로: ' + folder + ' · ') : ''}파일 ${state.files.length}개 중 ${picked}개 선택`;
    }
}

function cardBtn(text, onClick) {
    const b = document.createElement('button');
    b.textContent = text;
    b.className = 'card-action-btn';
    if (typeof onClick === 'function') b.addEventListener('click', onClick);
    return b;
}

function buildUnitToggle() {
    const wrap = document.createElement('div');
    wrap.className = 'unit-toggle';
    wrap.setAttribute('role', 'radiogroup');
    wrap.innerHTML = `
      <label><input type="radio" name="unit" value="ft" checked> Decimal Feet</label>
      <label><input type="radio" name="unit" value="m"> Meters (m)</label>`;
    wrap.querySelectorAll('input[type="radio"]').forEach(r => {
      r.checked = r.value === state.unit;
      r.onchange = () => { state.unit = r.value; paintHead(); repaintRows(); };
    });
    return wrap;
}

function selectedFilePaths() {
    return state.files.filter(f => f.checked).map(f => f.path);
}

function syncPreviewState() {
    const hasChecked = selectedFilePaths().length > 0;
    const previewBtn = document.getElementById('btnExPreview');
    if (previewBtn) previewBtn.disabled = !hasChecked;
}

function commonDir(list) {
    if (!list || !list.length) return '';
    const norm = list.map(p => String(p || '').replace(/\\/g, '/'));
    const parts = norm[0].split('/'); parts.pop();
    let prefix = parts.join('/');
    for (const p of norm.slice(1)) {
      while (prefix && !p.startsWith(prefix)) {
        prefix = prefix.split('/').slice(0, -1).join('/');
      }
    }
    return prefix;
}

function relPath(path, root) {
    const normRoot = String(root || '').replace(/[\\/]+$/, '').replace(/\\/g, '/');
    const normPath = String(path || '').replace(/\\/g, '/');
    if (!normRoot) return normPath;
    if (normPath.startsWith(normRoot)) return normPath.slice(normRoot.length + 1);
    return normPath;
}

function paintHead(target) {
    const unitLabel = state.unit === 'm' ? '(m)' : '(ft)';
    const project = HEADERS.filter(h => h.group === 'project');
    const survey = HEADERS.filter(h => h.group === 'survey');
    const head = `
      <tr>
        <th rowspan="2">File</th>
        <th class="group" colspan="${project.length}">Project Point ${unitLabel}</th>
        <th class="group" colspan="${survey.length}">Survey Point ${unitLabel}</th>
        <th rowspan="2">Angle (True North)</th>
      </tr>
      <tr>
        ${project.map(h => `<th>${h.label}</th>`).join('')}
        ${survey.map(h => `<th>${h.label}</th>`).join('')}
      </tr>`;
    const thead = target || document.querySelector('.kkyt-table thead');
    if (thead) thead.innerHTML = head;
}

function formatCoord(v) {
    const n = Number(v);
    if (!Number.isFinite(n)) return v ?? '';
    const scaled = state.unit === 'm' ? n * FT_TO_M : n;
    return scaled.toFixed(4);
}

function formatAngle(v) {
    const n = Number(v);
    return Number.isFinite(n) ? n.toFixed(3) : (v ?? '');
}

function repaintRows() {
    const tbody = document.querySelector('.kkyt-table tbody');
    if (!tbody) return;
    while (tbody.firstChild) tbody.removeChild(tbody.firstChild);
    state.rowsRaw.forEach(r => {
        const tr = document.createElement('tr');
        tr.append(tdText(r.File));
        HEADERS.forEach(h => tr.append(tdText(formatCoord(r[h.key]))));
        tr.append(tdText(formatAngle(r.TrueNorthAngle_deg ?? r['TrueNorthAngle(deg)'])));
        tbody.append(tr);
    });
}

function convertRowsForSave() {
    return (state.rowsRaw || []).map(r => ({ ...r }));
}

function syncSaveState() {
    const saveBtn = document.getElementById('btnExSave');
    if (saveBtn) saveBtn.disabled = !state.rowsRaw.length;
}
