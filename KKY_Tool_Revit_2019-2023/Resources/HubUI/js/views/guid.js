import { clear, div, toast, showExcelSavedDialog, chooseExcelMode } from '../core/dom.js';
import { ProgressDialog } from '../core/progress.js';
import { post, onHost } from '../core/bridge.js';

const LS_RVTS = 'kky_guid_rvts';

const HIDDEN_PROJECT_COLS = new Set(['RvtPath']);
const HIDDEN_FAMILY_COLS = new Set(['RvtPath']);
const FAMILY_FILTER = { all: 'all', shared: 'shared', family: 'family' };
const EXCEL_PHASE_WEIGHT = { EXCEL_INIT: 0.05, EXCEL_WRITE: 0.85, EXCEL_SAVE: 0.08, AUTOFIT: 0.02, DONE: 1, ERROR: 1 };

export function renderGuid(root) {
    const target = root || document.getElementById('view-root') || document.getElementById('app');
    clear(target);
    const top = document.querySelector('#topbar-root .topbar') || document.querySelector('.topbar'); if (top) top.classList.add('hub-topbar');

    const initialRvtList = loadRvtList();
    const state = {
        includeFamily: false,
        includeAnnotation: false,
        runId: '',
        rvtList: initialRvtList,
        rvtChecked: new Set(initialRvtList),
        project: { columns: [], rows: [] },
        familyIndex: [],
        family: { columns: [], rows: [] },
        activeTab: 'project',
        activeProjectKey: '',
        activeFamilyDoc: '',
        activeFamily: '',
        familyFilter: FAMILY_FILTER.all,
        busy: false
    };
    let lastExcelPct = 0;

    const page = div('feature-shell guid-page');

    // Header
    const header = div('feature-header');
    const heading = div('feature-heading');
    heading.innerHTML = `
      <span class="feature-kicker">GUID Audit</span>
      <h2 class="feature-title">공유 파라미터 GUID 검토</h2>
      <p class="feature-sub">프로젝트/패밀리 파라미터 GUID를 공유 파라미터 파일과 비교합니다.</p>`;

    const modeOption = buildModeOption();
    const runBtn = cardBtn('검토 시작', onRun);
    const exportBtn = cardBtn('엑셀 내보내기', onExport);
    exportBtn.disabled = true;
    const actions = div('feature-actions');
    const rightActions = div('guid-header-actions');
    rightActions.append(modeOption, runBtn, exportBtn);
    actions.append(rightActions);
    header.append(heading, actions);
    page.append(header);

    const body = div('guid-body');

    // RVT section
    const rvtSection = div('feature-results-panel guid-panel');
    const rvtHeader = document.createElement('div');
    rvtHeader.className = 'feature-results-head';
    const rvtTitle = document.createElement('div');
    rvtTitle.className = 'guid-title';
    rvtTitle.innerHTML = '<h3>대상 RVT 목록</h3><p class="feature-note">비우면 현재 활성 문서를 사용합니다.</p>';
    const rvtActions = div('feature-actions');
    let btnRemove = null;
    const btnAdd = cardBtn('RVT 파일 추가', () => post('guid:add-files', { pick: 'files' }));
    const btnAddFolder = cardBtn('폴더 선택', () => post('guid:add-files', { pick: 'folder' }));
    btnRemove = cardBtn('선택 제거', onRemoveSelected);
    btnRemove.disabled = true;
    const btnClear = cardBtn('목록 지우기', () => { state.rvtList = []; state.rvtChecked.clear(); persistRvts(); renderRvtList(); syncRvtActionState(); });
    rvtActions.append(btnAdd, btnAddFolder, btnRemove, btnClear);
    rvtHeader.append(rvtTitle, rvtActions);
    const rvtTableWrap = div('guid-table-wrap guid-rvt-wrap');
    const rvtTable = document.createElement('table'); rvtTable.className = 'guid-rvt-table';
    rvtTable.innerHTML = '<thead><tr><th><input type="checkbox"></th><th>#</th><th>파일명</th><th>경로</th></tr></thead><tbody></tbody>';
    const rvtBody = rvtTable.querySelector('tbody');
    rvtTableWrap.append(rvtTable);
    rvtSection.append(rvtHeader, rvtTableWrap);
    body.append(rvtSection);

    // Result tabs
    const tabs = div('feature-results-panel guid-results feature-tabs');
    const tabHead = div('feature-results-head');
    const tabBtns = div('pill-tabs');
    const btnTabProject = document.createElement('button'); btnTabProject.type = 'button'; btnTabProject.className = 'pill-tab is-active'; btnTabProject.innerHTML = `<span class="pill-label">RVT 검토결과</span><span class="pill-count">0</span>`;
    const btnTabFamily = document.createElement('button'); btnTabFamily.type = 'button'; btnTabFamily.className = 'pill-tab'; btnTabFamily.innerHTML = `<span class="pill-label">Family(RFA) Parameter</span><span class="pill-count">0</span>`;
    tabBtns.append(btnTabProject, btnTabFamily);
    tabHead.append(tabBtns);
    tabs.append(tabHead);

    const tabPanels = div('guid-tab-panels');

    // Project tab
    const tabPanelProject = div('guid-tab-panel');
    const projectWrap = div('guid-detail-wrap');
    const projectNavPane = div('guid-detail-nav feature-results-panel');
    const projectNav = document.createElement('ul'); projectNav.className = 'guid-nav-list';
    projectNavPane.append(projectNav);
    const projectPane = div('guid-detail-pane');
    const projectTableWrap = div('guid-table-wrap');
    const projectTable = document.createElement('table'); projectTable.className = 'guid-table';
    const projectHead = document.createElement('thead');
    const projectBody = document.createElement('tbody');
    projectTable.append(projectHead, projectBody);
    projectTableWrap.append(projectTable);
    projectPane.append(projectTableWrap);
    projectWrap.append(projectNavPane, projectPane);
    tabPanelProject.append(projectWrap);

    // Family tab
    const tabPanelFamily = div('guid-tab-panel is-hidden');
    const familyWrap = div('guid-detail-wrap');
    const familyNavPane = div('guid-detail-nav feature-results-panel');
    const familyNav = document.createElement('ul'); familyNav.className = 'guid-nav-list';
    familyNavPane.append(familyNav);
    const familyPane = div('guid-detail-pane');
    const familyFilterBox = buildFamilyFilter();
    const familyTableWrap = div('guid-table-wrap');
    const familyTable = document.createElement('table'); familyTable.className = 'guid-table';
    const familyHead = document.createElement('thead');
    const familyBody = document.createElement('tbody');
    familyTable.append(familyHead, familyBody);
    familyTableWrap.append(familyTable);
    familyPane.append(familyFilterBox, familyTableWrap);
    familyWrap.append(familyNavPane, familyPane);
    tabPanelFamily.append(familyWrap);

    tabPanels.append(tabPanelProject, tabPanelFamily);
    tabs.append(tabPanels);
    body.append(tabs);

    page.append(body);
    target.append(page);

    renderRvtList();
    syncTabState();
    syncRvtActionState();

    // Host events
    onHost('guid:files', ({ paths }) => {
        const list = Array.isArray(paths) ? paths : [];
        let added = 0;
        list.forEach(p => {
            const path = normalizeRvtPath(p);
            if (!path) return;
            const exists = state.rvtList.some(x => samePath(x, path));
            if (!exists) { state.rvtList.push(path); added++; }
            state.rvtChecked.add(path);
        });
        state.rvtList = dedupPaths(state.rvtList);
        renderRvtList();
        syncRvtActionState();
    });

    onHost('guid:progress', (payload) => {
        if (payload && payload.phase) {
            handleExcelProgress(payload);
        } else {
            handleRunProgress(payload);
        }
    });

    onHost('guid:done', (payload) => {
        ProgressDialog.hide();
        setBusy(false);
        lastExcelPct = 0;
        const proj = payload?.project || {};
        const famIndex = Array.isArray(payload?.familyIndex) ? payload.familyIndex : [];
        state.runId = payload?.runId || '';
        state.includeFamily = !!payload?.includeFamily;
        state.project = {
            columns: Array.isArray(proj.columns) ? proj.columns : [],
            rows: Array.isArray(proj.rows) ? proj.rows : []
        };
        state.familyIndex = famIndex;
        state.family = { columns: [], rows: [] };
        state.activeTab = 'project';
        state.activeProjectKey = '';
        state.activeFamilyDoc = '';
        state.activeFamily = '';
        state.familyFilter = FAMILY_FILTER.all;
        if (typeof modeOption.sync === 'function') modeOption.sync();
        exportBtn.disabled = !hasRowsForExport();
        updateTabCounts();
        paintProject();
        paintFamily();
        syncTabState();
        toast('검토 완료', 'ok');
    });

    onHost('guid:warn', ({ message }) => {
        if (message) toast(message, 'warn');
    });

    onHost('guid:exported', ({ path }) => {
        ProgressDialog.hide();
        setBusy(false);
        lastExcelPct = 0;
        if (path) {
            showExcelSavedDialog('엑셀로 내보냈습니다.', path, (p) => post('excel:open', { path: p }));
        } else {
            toast('엑셀 내보내기 완료', 'ok');
        }
    });

    onHost('guid:family-detail', (payload) => {
        ProgressDialog.hide();
        setBusy(false);
        lastExcelPct = 0;
        if (payload?.runId && state.runId && payload.runId !== state.runId) {
            toast('이전 실행 결과입니다. 다시 실행하세요.', 'warn');
            return;
        }
        const cols = Array.isArray(payload?.columns) ? payload.columns : [];
        const rows = Array.isArray(payload?.rows) ? payload.rows : [];
        state.family = { columns: cols, rows: rows };
        paintFamily();
    });

    const handleError = ({ message }) => {
        ProgressDialog.hide();
        setBusy(false);
        lastExcelPct = 0;
        if (message) toast(message, 'err');
    };
    onHost('guid:error', handleError);
    onHost('revit:error', handleError);
    onHost('host:error', handleError);

    // UI handlers
    btnTabProject.onclick = () => { state.activeTab = 'project'; syncTabState(); };
    btnTabFamily.onclick = () => {
        if (state.busy) return;
        state.activeTab = 'family';
        syncTabState();
    };

    function buildModeOption() {
        const wrap = div('guid-mode');
        const base = document.createElement('div');
        base.className = 'guid-mode-base';
        base.innerHTML = `<div class="mode-title">Project(RVT) Parameter</div><div class="mode-sub">기본(항상 실행)</div>`;

        const famWrap = document.createElement('label');
        famWrap.className = 'guid-mode-option';
        const ck = document.createElement('input'); ck.type = 'checkbox';
        ck.checked = state.includeFamily;
        ck.onchange = () => { state.includeFamily = !!ck.checked; syncTabState(); };
        const text = document.createElement('span'); text.textContent = 'Family(RFA) Parameter 추가 검토';
        famWrap.append(ck, text);

        const annWrap = document.createElement('label');
        annWrap.className = 'guid-mode-option';
        const ckAnn = document.createElement('input'); ckAnn.type = 'checkbox';
        ckAnn.checked = state.includeAnnotation;
        ckAnn.onchange = () => { state.includeAnnotation = !!ckAnn.checked; };
        const textAnn = document.createElement('span'); textAnn.textContent = 'Annotation 포함';
        annWrap.append(ckAnn, textAnn);

        wrap.append(base, famWrap, annWrap);
        wrap.sync = () => {
            ck.checked = !!state.includeFamily;
            ckAnn.checked = !!state.includeAnnotation;
            ckAnn.disabled = !state.includeFamily;
            annWrap.classList.toggle('is-disabled', ckAnn.disabled);
        };
        return wrap;
    }

    function buildFamilyFilter() {
        const wrap = div('guid-family-filter');
        const label = document.createElement('div'); label.className = 'guid-filter-label'; label.textContent = '표시 대상';
        const btns = div('segmented');
        const mk = (key, text) => {
            const b = document.createElement('button'); b.type = 'button'; b.textContent = text; b.className = 'seg-btn';
            b.onclick = () => { state.familyFilter = key; paintFamily(); };
            return b;
        };
        const btnAll = mk(FAMILY_FILTER.all, '전체');
        const btnShared = mk(FAMILY_FILTER.shared, 'Shared Parameter만');
        const btnFamily = mk(FAMILY_FILTER.family, 'Family Parameter만');
        btns.append(btnAll, btnShared, btnFamily);
        wrap.append(label, btns);
        const sync = () => {
            [btnAll, btnShared, btnFamily].forEach(b => b.classList.remove('is-active'));
            if (state.familyFilter === FAMILY_FILTER.shared) btnShared.classList.add('is-active');
            else if (state.familyFilter === FAMILY_FILTER.family) btnFamily.classList.add('is-active');
            else btnAll.classList.add('is-active');
        };
        wrap.sync = sync;
        sync();
        return wrap;
    }

    function onRun() {
        if (state.busy) return;
        state.rvtList = dedupPaths(state.rvtList);
        const targets = dedupPaths(state.rvtList.filter(p => state.rvtChecked.has(p)));
        if (state.rvtList.length > 0 && targets.length === 0) {
            toast('선택된 RVT가 없습니다.', 'warn');
            return;
        }

        const includeFamily = !!state.includeFamily;
        const payload = {
            mode: includeFamily ? 2 : 1,
            includeFamily,
            includeAnnotation: !!state.includeAnnotation,
            rvtPaths: state.rvtList.length === 0 ? [] : targets
        };
        persistRvts();

        setBusy(true);
        state.activeTab = 'project';
        state.runId = '';
        ProgressDialog.show('GUID Audit', '준비 중…');
        post('guid:run', payload);
    }

    function onExport() {
        if (state.busy) return;
        if (!hasRowsForExport()) { toast('저장할 결과가 없습니다.', 'warn'); return; }
        chooseExcelMode((mode) => {
            const excelMode = mode || 'fast';
            lastExcelPct = 0;
            setBusy(true);
            ProgressDialog.show('엑셀 내보내기', '엑셀 파일을 만드는 중…');
            post('guid:export', { excelMode });
        });
    }

    function renderRvtList() {
        state.rvtList = dedupPaths(state.rvtList);
        state.rvtChecked = new Set(state.rvtList.filter(p => state.rvtChecked.has(p)));
        const master = rvtTable.querySelector('thead input[type="checkbox"]');
        const allChecked = state.rvtList.length > 0 && state.rvtList.every(p => state.rvtChecked.has(p));
        master.checked = allChecked;
        master.indeterminate = state.rvtList.length > 0 && !allChecked && state.rvtChecked.size > 0;
        master.onchange = () => {
            if (master.checked) state.rvtChecked = new Set(state.rvtList);
            else state.rvtChecked.clear();
            persistRvts();
            renderRvtList();
        };

        rvtBody.innerHTML = '';
        if (!state.rvtList.length) {
            const tr = document.createElement('tr');
            const td = document.createElement('td'); td.colSpan = 4; td.textContent = '등록된 RVT가 없습니다.';
            tr.append(td); rvtBody.append(tr); return;
        }
        state.rvtList.forEach((p, i) => {
            const tr = document.createElement('tr');
            const tdCk = document.createElement('td');
            const ck = document.createElement('input'); ck.type = 'checkbox'; ck.checked = state.rvtChecked.has(p);
            ck.onchange = () => {
                if (ck.checked) state.rvtChecked.add(p); else state.rvtChecked.delete(p);
                persistRvts();
                renderRvtList();
            };
            tdCk.append(ck);
            const name = p?.split(/[\\/]/).pop() || '(Doc)';
            const tdIdx = document.createElement('td'); tdIdx.textContent = i + 1;
            const tdName = document.createElement('td'); tdName.textContent = name;
            const tdPath = document.createElement('td'); tdPath.className = 'path-cell'; tdPath.textContent = p;
            tr.append(tdCk, tdIdx, tdName, tdPath);
            rvtBody.append(tr);
        });
        syncRvtActionState();
    }

    function paintProject() {
        buildHead(projectHead, state.project.columns, HIDDEN_PROJECT_COLS);
        paintVirtualRows(projectBody, state.project.columns, filteredProjectRows(), HIDDEN_PROJECT_COLS);
        buildProjectNav();
    }

    function paintFamily() {
        buildHead(familyHead, state.family.columns, HIDDEN_FAMILY_COLS);
        paintVirtualRows(familyBody, state.family.columns, filteredFamilyRows(), HIDDEN_FAMILY_COLS);
        buildFamilyNav();
        if (typeof familyFilterBox.sync === 'function') familyFilterBox.sync();
        if (!state.family.rows.length && state.includeFamily) {
            const empty = document.createElement('div');
            empty.className = 'guid-nav-empty';
            empty.textContent = '패밀리를 선택하세요.';
            familyBody.innerHTML = '';
            const tr = document.createElement('tr');
            const td = document.createElement('td');
            td.colSpan = Math.max(1, state.family.columns.filter(c => !HIDDEN_FAMILY_COLS.has(c)).length || 1);
            td.append(empty);
            tr.append(td);
            familyBody.append(tr);
        }
    }

    function buildProjectNav() {
        projectNav.innerHTML = '';
        if (!state.project.rows.length) {
            const empty = document.createElement('li');
            empty.className = 'guid-nav-empty';
            empty.textContent = '결과가 없습니다.';
            projectNav.append(empty);
            return;
        }
        const idxPath = colIndex(state.project.columns, 'RvtPath');
        const idxName = colIndex(state.project.columns, 'RvtName');
        const map = new Map();
        state.project.rows.forEach(row => {
            const path = safe(row[idxPath]);
            const name = safe(row[idxName]) || path || '(Doc)';
            const key = path || name;
            if (!map.has(key)) map.set(key, { name, path: path || '' });
        });
        Array.from(map.entries()).sort((a, b) => a[1].name.localeCompare(b[1].name)).forEach(([key, info]) => {
            const li = document.createElement('li');
            li.className = 'guid-nav-doc';
            const btn = document.createElement('button'); btn.type = 'button'; btn.className = 'nav-doc-title';
            btn.textContent = info.name;
            btn.title = info.path || info.name;
            btn.onclick = () => { state.activeProjectKey = key; paintProject(); };
            if (state.activeProjectKey === key) btn.classList.add('is-active');
            li.append(btn);
            projectNav.append(li);
        });
    }

    function buildFamilyNav() {
        familyNav.innerHTML = '';
        if (!state.includeFamily) {
            const empty = document.createElement('li');
            empty.className = 'guid-nav-empty';
            empty.textContent = 'Family(RFA) Parameter 추가 검토를 선택 후 실행하세요.';
            familyNav.append(empty);
            return;
        }
        if (!state.familyIndex.length) {
            const empty = document.createElement('li');
            empty.className = 'guid-nav-empty';
            empty.textContent = '패밀리 결과가 없습니다.';
            familyNav.append(empty);
            return;
        }
        const map = new Map();
        state.familyIndex.forEach(item => {
            const path = safe(item.RvtPath);
            const docName = safe(item.RvtName) || path || '(Doc)';
            const fam = safe(item.FamilyName);
            const key = path || docName;
            if (!map.has(key)) map.set(key, { name: docName, families: new Set(), path: path || '' });
            if (fam) map.get(key).families.add({ name: fam, cat: item.FamilyCategory });
        });
        Array.from(map.entries()).sort((a, b) => a[1].name.localeCompare(b[1].name)).forEach(([key, info]) => {
            const docItem = document.createElement('li');
            docItem.className = 'guid-nav-doc';
            const docTitle = document.createElement('div');
            docTitle.className = 'nav-doc-title';
            docTitle.textContent = info.name;
            docTitle.title = info.path || info.name;
            docTitle.onclick = () => { state.activeFamilyDoc = key; state.activeFamily = ''; paintFamily(); };
            if (state.activeFamilyDoc === key && !state.activeFamily) docTitle.classList.add('is-active');
            docItem.append(docTitle);

            const famList = document.createElement('ul');
            famList.className = 'guid-nav-fams';

            Array.from(info.families).sort((a, b) => a.name.localeCompare(b.name)).forEach(f => {
                const li = document.createElement('li');
                const btn = document.createElement('button'); btn.type = 'button'; btn.className = 'nav-fam-item'; btn.textContent = f.name;
                btn.title = f.name;
                btn.onclick = () => { state.activeFamilyDoc = key; state.activeFamily = f.name; requestFamilyDetail(info.path, f.name); };
                if (state.activeFamilyDoc === key && state.activeFamily === f.name) btn.classList.add('is-active');
                li.append(btn);
                famList.append(li);
            });

            docItem.append(famList);
            familyNav.append(docItem);
        });
    }

    function filteredProjectRows() {
        if (!state.project.rows.length) return [];
        const idxPath = colIndex(state.project.columns, 'RvtPath');
        const idxName = colIndex(state.project.columns, 'RvtName');
        const key = state.activeProjectKey;
        if (!key) return state.project.rows;
        return state.project.rows.filter(row => {
            const path = safe(row[idxPath]);
            const name = safe(row[idxName]);
            return (!!path && path === key) || (!path && name === key) || (!path && !key && !name);
        });
    }

    function filteredFamilyRows() {
        if (!state.family.rows.length) return [];
        const idxShared = colIndex(state.family.columns, 'IsShared');
        return state.family.rows.filter(row => {
            const isShared = safe(row[idxShared]).toUpperCase() === 'Y';
            const filterMatch = state.familyFilter === FAMILY_FILTER.all || (state.familyFilter === FAMILY_FILTER.shared && isShared) || (state.familyFilter === FAMILY_FILTER.family && !isShared);
            return filterMatch;
        });
    }

    function colIndex(columns, name) {
        return columns.findIndex(c => c === name);
    }

    function buildHead(thead, columns, hidden) {
        thead.innerHTML = '';
        const tr = document.createElement('tr');
        columns.forEach(c => {
            if (hidden.has(c)) return;
            const th = document.createElement('th');
            th.textContent = c;
            tr.append(th);
        });
        thead.append(tr);
    }

    function requestFamilyDetail(rvtPath, familyName) {
        if (!state.includeFamily || !familyName) return;
        if (!state.runId) { toast('실행 정보(runId)가 없습니다. 다시 실행하세요.', 'warn'); return; }
        state.family = { columns: [], rows: [] };
        paintFamily();
        ProgressDialog.show('GUID Audit', '패밀리 데이터를 불러오는 중…');
        post('guid:request-family-detail', { runId: state.runId, rvtPath, familyName });
    }

    function paintVirtualRows(tbody, columns, rows, hidden) {
        tbody.innerHTML = '';
        let idx = 0;
        const chunk = () => {
            const frag = document.createDocumentFragment();
            for (let i = 0; i < 200 && idx < rows.length; i++, idx++) {
                const row = rows[idx];
                const tr = document.createElement('tr');
                columns.forEach((c, ci) => {
                    if (hidden.has(c)) return;
                    const td = document.createElement('td');
                    td.textContent = safe(row[ci]);
                    tr.append(td);
                });
                frag.append(tr);
            }
            tbody.append(frag);
            if (idx < rows.length) setTimeout(chunk, 0);
        };
        chunk();
    }

    function hasRowsForExport() {
        return (state.project.rows || []).length > 0;
    }

    function syncTabState() {
        btnTabProject.classList.toggle('is-active', state.activeTab === 'project');
        btnTabFamily.classList.toggle('is-active', state.activeTab === 'family');
        btnTabFamily.disabled = !state.includeFamily;
        btnTabFamily.classList.toggle('is-disabled', !state.includeFamily);
        tabPanelProject.classList.toggle('is-hidden', state.activeTab !== 'project');
        const disableFamilyView = state.activeTab === 'family' && !state.includeFamily;
        tabPanelFamily.classList.toggle('is-hidden', state.activeTab !== 'family');
        familyWrap.classList.toggle('is-disabled', disableFamilyView);
        if (disableFamilyView) {
            state.activeTab = 'project';
        }
        exportBtn.disabled = state.busy || !hasRowsForExport();
        updateTabCounts();
    }

    function setBusy(on) {
        state.busy = on;
        runBtn.disabled = on;
        exportBtn.disabled = on || !hasRowsForExport();
    }

    function persistRvts() {
        state.rvtList = dedupPaths(state.rvtList);
        try { localStorage.setItem(LS_RVTS, JSON.stringify(state.rvtList || [])); } catch { }
    }

    function loadRvtList() {
        try {
            const raw = localStorage.getItem(LS_RVTS);
            const arr = JSON.parse(raw || '[]');
            if (Array.isArray(arr)) return dedupPaths(arr);
        } catch { }
        return [];
    }

    function handleRunProgress(payload) {
        const percent = typeof payload?.pct === 'number' ? payload.pct : 0;
        const message = payload?.text || '';
        if (!state.busy && percent <= 0) return;
        if (!state.busy) setBusy(true);
        ProgressDialog.show('GUID Audit', message || '진행 중…');
        ProgressDialog.update(percent, message || '', '');
    }

    function handleExcelProgress(payload) {
        const phase = normalizeExcelPhase(payload?.phase);
        const total = Number(payload?.total) || 0;
        const current = Number(payload?.current) || 0;
        const percent = computeExcelPercent(phase, current, total, payload?.phaseProgress);
        const subtitle = buildExcelSubtitle(phase, current, total);
        const detail = formatExcelDetail(phase, payload?.message);

        const exporting = phase !== 'DONE' && phase !== 'ERROR';
        if (!state.busy && exporting) setBusy(true);

        ProgressDialog.show('엑셀 내보내기', subtitle || '엑셀 내보내기 중…');
        ProgressDialog.update(percent, subtitle, detail);

        if (!exporting) {
            setTimeout(() => { ProgressDialog.hide(); lastExcelPct = 0; setBusy(false); }, 260);
        }
    }

    function normalizeExcelPhase(phase) {
        return String(phase || '').trim().toUpperCase() || 'EXCEL_WRITE';
    }

    function computeExcelPercent(phase, current, total, phaseProgress) {
        const norm = normalizeExcelPhase(phase);
        if (norm === 'DONE') { lastExcelPct = 100; return 100; }
        if (norm === 'ERROR') return lastExcelPct;

        const completed = ['EXCEL_INIT', 'EXCEL_WRITE', 'EXCEL_SAVE', 'AUTOFIT'].reduce((acc, key) => {
            if (key === norm) return acc;
            return acc + (EXCEL_PHASE_WEIGHT[key] || 0);
        }, 0);
        const weight = EXCEL_PHASE_WEIGHT[norm] || 0;
        const ratio = total > 0 ? Math.max(0, Math.min(1, current / total)) : 0;
        const staged = Math.max(ratio, clamp01(phaseProgress));
        const pct = (completed + weight * staged) / (completed + weight) * 100;
        lastExcelPct = Math.max(lastExcelPct, Math.min(100, pct));
        return lastExcelPct;
    }

    function clamp01(v) { const n = Number(v); if (Number.isFinite(n)) return Math.max(0, Math.min(1, n)); return 0; }

    function buildExcelSubtitle(phase, current, total) {
        const norm = normalizeExcelPhase(phase);
        switch (norm) {
            case 'EXCEL_INIT': return '엑셀 워크북 준비 중';
            case 'EXCEL_WRITE': return `엑셀 데이터 작성 중 (${current}/${Math.max(total, current || 1)})`;
            case 'EXCEL_SAVE': return '엑셀 내보내기 중';
            case 'AUTOFIT': return '열 너비 자동 조정 중…';
            case 'DONE': return '엑셀 내보내기 완료';
            case 'ERROR': return '엑셀 내보내기 오류';
            default: return '엑셀 내보내기 중…';
        }
    }

    function formatExcelDetail(phase, message) {
        const norm = normalizeExcelPhase(phase);
        if (norm === 'AUTOFIT') return '열 너비 자동 조정 중…';
        return message || '';
    }

    function onRemoveSelected() {
        if (!state.rvtChecked.size) { toast('제거할 RVT를 선택하세요.', 'warn'); return; }
        state.rvtList = state.rvtList.filter(p => !state.rvtChecked.has(p));
        state.rvtChecked = new Set(state.rvtList);
        persistRvts();
        renderRvtList();
    }

    function syncRvtActionState() {
        if (btnRemove) btnRemove.disabled = state.rvtChecked.size === 0;
    }

    function updateTabCounts() {
        setTabCount(btnTabProject, state.project.rows.length || 0);
        setTabCount(btnTabFamily, state.familyIndex.length || 0);
    }
}

function normalizeRvtPath(entry) {
    if (!entry) return '';
    if (typeof entry === 'string') return entry.trim();
    if (typeof entry === 'object') {
        if (typeof entry.path === 'string') return entry.path.trim();
        if (typeof entry.fullPath === 'string') return entry.fullPath.trim();
    }
    return '';
}

function dedupPaths(list) {
    const seen = new Set();
    const clean = [];
    (list || []).forEach(item => {
        const path = normalizeRvtPath(item);
        if (!path) return;
        const key = path.toLowerCase();
        if (seen.has(key)) return;
        seen.add(key);
        clean.push(path);
    });
    return clean;
}

function setTabCount(btn, count) {
    if (!btn) return;
    const badge = btn.querySelector('.pill-count');
    if (!badge) return;
    badge.textContent = Number.isFinite(count) ? count : 0;
}

function safe(v) {
    if (v === null || v === undefined) return '';
    return String(v);
}

function samePath(a, b) {
    if (!a || !b) return false;
    return a.toLowerCase() === b.toLowerCase();
}

function cardBtn(text, onClick) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn card-btn';
    btn.textContent = text;
    btn.onclick = onClick;
    return btn;
}
