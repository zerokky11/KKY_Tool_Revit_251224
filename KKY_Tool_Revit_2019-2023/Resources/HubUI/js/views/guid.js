import { clear, div, toast, showExcelSavedDialog } from '../core/dom.js';
import { ProgressDialog } from '../core/progress.js';
import { post, onHost } from '../core/bridge.js';

const LS_RVTS = 'kky_guid_rvts';

const HIDDEN_DETAIL_COLS = new Set(['RvtPath']);

export function renderGuid(root) {
    const target = root || document.getElementById('view-root') || document.getElementById('app');
    clear(target);
    const top = document.querySelector('#topbar-root .topbar') || document.querySelector('.topbar'); if (top) top.classList.add('hub-topbar');

    const state = {
        mode: 1,
        rvtList: loadRvtList(),
        summary: { columns: [], rows: [] },
        detail: { columns: [], rows: [] },
        activeTab: 'summary',
        activeDocKey: '',
        activeFamily: '',
        busy: false,
        excelMode: 'fast'
    };

    const page = div('feature-shell guid-page');

    // Header
    const header = div('feature-header');
    const heading = div('feature-heading');
    heading.innerHTML = `
      <span class="feature-kicker">GUID Audit</span>
      <h2 class="feature-title">공유 파라미터 GUID 검토</h2>
      <p class="feature-sub">프로젝트/패밀리 파라미터 GUID를 공유 파라미터 파일과 비교합니다.</p>`;

    const modeToggle = buildModeToggle();
    const excelModeToggle = buildExcelModeToggle();
    const runBtn = cardBtn('검토 시작', onRun);
    const exportBtn = cardBtn('엑셀 저장...', onExport);
    exportBtn.disabled = true;
    const actions = div('feature-actions');
    const rightActions = div('guid-header-actions');
    rightActions.append(modeToggle, excelModeToggle, runBtn, exportBtn);
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
    const btnAdd = cardBtn('RVT 추가...', () => post('guid:add-files', {}));
    const btnClear = cardBtn('목록 지우기', () => { state.rvtList = []; persistRvts(); renderRvtList(); });
    rvtActions.append(btnAdd, btnClear);
    rvtHeader.append(rvtTitle, rvtActions);
    const rvtTableWrap = div('guid-table-wrap');
    const rvtTable = document.createElement('table'); rvtTable.className = 'guid-rvt-table';
    rvtTable.innerHTML = '<thead><tr><th>#</th><th>파일명</th><th>경로</th></tr></thead><tbody></tbody>';
    const rvtBody = rvtTable.querySelector('tbody');
    rvtTableWrap.append(rvtTable);
    rvtSection.append(rvtHeader, rvtTableWrap);
    body.append(rvtSection);

    // Result tabs
    const tabs = div('guid-tabs feature-results-panel');
    const tabBtns = div('guid-tab-buttons');
    const btnTabSummary = document.createElement('button'); btnTabSummary.type = 'button'; btnTabSummary.className = 'tab-btn is-active'; btnTabSummary.textContent = '요약';
    const btnTabDetail = document.createElement('button'); btnTabDetail.type = 'button'; btnTabDetail.className = 'tab-btn'; btnTabDetail.textContent = '패밀리/파라미터';
    tabBtns.append(btnTabSummary, btnTabDetail);
    tabs.append(tabBtns);

    const tabPanelSummary = div('guid-tab-panel');
    const summaryTableWrap = div('guid-table-wrap');
    const summaryTable = document.createElement('table'); summaryTable.className = 'guid-table';
    const summaryHead = document.createElement('thead');
    const summaryBody = document.createElement('tbody');
    summaryTable.append(summaryHead, summaryBody);
    summaryTableWrap.append(summaryTable);
    tabPanelSummary.append(summaryTableWrap);

    const tabPanelDetail = div('guid-tab-panel is-hidden');
    const detailWrap = div('guid-detail-wrap');
    const navPane = div('guid-detail-nav feature-results-panel');
    const navList = document.createElement('ul'); navList.className = 'guid-nav-list';
    navPane.append(navList);
    const detailPane = div('guid-detail-pane');
    const detailTableWrap = div('guid-table-wrap');
    const detailTable = document.createElement('table'); detailTable.className = 'guid-table';
    const detailHead = document.createElement('thead');
    const detailBody = document.createElement('tbody');
    detailTable.append(detailHead, detailBody);
    detailTableWrap.append(detailTable);
    detailPane.append(detailTableWrap);
    detailWrap.append(navPane, detailPane);
    tabPanelDetail.append(detailWrap);

    tabs.append(tabPanelSummary, tabPanelDetail);
    body.append(tabs);

    page.append(body);
    target.append(page);

    renderRvtList();
    syncTabState();

    // Host events
    onHost('guid:files', ({ paths }) => {
        const list = Array.isArray(paths) ? paths : [];
        let added = 0;
        list.forEach(p => {
            if (!p || typeof p !== 'string') return;
            const exists = state.rvtList.some(x => samePath(x, p));
            if (!exists) { state.rvtList.push(p); added++; }
        });
        if (added) {
            persistRvts();
            renderRvtList();
        }
    });

    onHost('guid:progress', ({ pct, text }) => {
        const percent = typeof pct === 'number' ? pct : 0;
        const message = text || '';
        if (!state.busy && percent <= 0) return;
        if (!state.busy) setBusy(true);
        ProgressDialog.show('GUID Audit', message || '진행 중…');
        ProgressDialog.update(percent, message || '', '');
    });

    onHost('guid:done', (payload) => {
        ProgressDialog.hide();
        setBusy(false);
        const sum = payload?.summary || {};
        const det = payload?.detail || {};
        state.summary = {
            columns: Array.isArray(sum.columns) ? sum.columns : [],
            rows: Array.isArray(sum.rows) ? sum.rows : []
        };
        state.detail = {
            columns: Array.isArray(det.columns) ? det.columns : [],
            rows: Array.isArray(det.rows) ? det.rows : []
        };
        state.activeDocKey = '';
        state.activeFamily = '';
        exportBtn.disabled = !hasRowsForExport();
        paintSummary();
        paintDetail();
        syncTabState();
        toast('검토 완료', 'ok');
    });

    onHost('guid:exported', ({ path }) => {
        ProgressDialog.hide();
        setBusy(false);
        if (path) {
            showExcelSavedDialog('엑셀로 저장했습니다.', path, (p) => post('excel:open', { path: p }));
        } else {
            toast('엑셀 저장 완료', 'ok');
        }
    });

    const handleError = ({ message }) => {
        ProgressDialog.hide();
        setBusy(false);
        if (message) toast(message, 'err');
    };
    onHost('guid:error', handleError);
    onHost('revit:error', handleError);
    onHost('host:error', handleError);

    // UI handlers
    btnTabSummary.onclick = () => { state.activeTab = 'summary'; syncTabState(); };
    btnTabDetail.onclick = () => {
        if (state.mode !== 2) return;
        state.activeTab = 'detail';
        syncTabState();
    };

    function onRun() {
        if (state.busy) return;
        setBusy(true);
        if (state.mode !== 2) state.activeTab = 'summary';
        ProgressDialog.show('GUID Audit', '준비 중…');
        const payload = {
            mode: state.mode,
            rvtPaths: state.rvtList
        };
        post('guid:run', payload);
    }

    function onExport() {
        if (state.busy) return;
        if (!hasRowsForExport()) { toast('저장할 결과가 없습니다.', 'warn'); return; }
        setBusy(true);
        const which = state.activeTab === 'detail' ? 'detail' : 'summary';
        const excelMode = state.excelMode || 'fast';
        ProgressDialog.show('엑셀 저장', '엑셀 파일을 만드는 중…');
        post('guid:export', { which, excelMode });
    }

    function buildModeToggle() {
        const wrap = div('guid-mode');
        const btnM1 = document.createElement('button'); btnM1.type = 'button'; btnM1.className = 'mode-btn is-active'; btnM1.textContent = 'Mode 1: 프로젝트 파라미터';
        const btnM2 = document.createElement('button'); btnM2.type = 'button'; btnM2.className = 'mode-btn'; btnM2.textContent = 'Mode 2: 패밀리 공유 파라미터';
        btnM1.onclick = () => { if (state.mode === 1) return; state.mode = 1; state.activeTab = 'summary'; syncModeButtons(); syncTabState(); };
        btnM2.onclick = () => { if (state.mode === 2) return; state.mode = 2; syncModeButtons(); syncTabState(); };
        wrap.append(btnM1, btnM2);
        function syncModeButtons() {
            btnM1.classList.toggle('is-active', state.mode === 1);
            btnM2.classList.toggle('is-active', state.mode === 2);
        }
        return wrap;
    }

    function buildExcelModeToggle() {
        const wrap = div('guid-excelmode');
        const label = document.createElement('span');
        label.className = 'guid-excelmode-label';
        label.textContent = '엑셀 저장 모드';
        const btnFast = document.createElement('button');
        btnFast.type = 'button';
        btnFast.className = 'mode-btn is-active';
        btnFast.textContent = '빠른(권장)';
        const btnNormal = document.createElement('button');
        btnNormal.type = 'button';
        btnNormal.className = 'mode-btn';
        btnNormal.textContent = '일반(열 너비 자동)';

        const sync = () => {
            btnFast.classList.toggle('is-active', state.excelMode === 'fast');
            btnNormal.classList.toggle('is-active', state.excelMode === 'normal');
        };

        btnFast.onclick = () => { state.excelMode = 'fast'; sync(); };
        btnNormal.onclick = () => { state.excelMode = 'normal'; sync(); };
        wrap.append(label, btnFast, btnNormal);
        return wrap;
    }

    function renderRvtList() {
        rvtBody.innerHTML = '';
        if (!state.rvtList.length) {
            const tr = document.createElement('tr');
            const td = document.createElement('td'); td.colSpan = 3; td.textContent = '등록된 RVT가 없습니다.';
            tr.append(td); rvtBody.append(tr); return;
        }
        state.rvtList.forEach((p, i) => {
            const tr = document.createElement('tr');
            const name = p?.split(/[\\/]/).pop() || '(Doc)';
            tr.innerHTML = `<td>${i + 1}</td><td>${name}</td><td class="path-cell">${p}</td>`;
            rvtBody.append(tr);
        });
    }

    function paintSummary() {
        buildHead(summaryHead, state.summary.columns, new Set());
        paintVirtualRows(summaryBody, state.summary.columns, state.summary.rows, new Set());
    }

    function paintDetail() {
        buildHead(detailHead, state.detail.columns, HIDDEN_DETAIL_COLS);
        paintVirtualRows(detailBody, state.detail.columns, filteredDetailRows(), HIDDEN_DETAIL_COLS);
        buildNav();
    }

    function buildNav() {
        navList.innerHTML = '';
        if (!state.detail.rows.length) {
            const empty = document.createElement('li');
            empty.className = 'guid-nav-empty';
            empty.textContent = '상세 결과가 없습니다.';
            navList.append(empty);
            return;
        }
        const idxPath = colIndex('RvtPath');
        const idxName = colIndex('RvtName');
        const idxFam = colIndex('FamilyName');
        const map = new Map();
        state.detail.rows.forEach(row => {
            const path = (row[idxPath] || '').toString();
            const rname = (row[idxName] || path || '(Doc)').toString();
            const fam = (row[idxFam] || '').toString();
            const key = path || rname;
            if (!map.has(key)) map.set(key, { name: rname, families: new Set() });
            if (fam) map.get(key).families.Add(fam);
        });
        Array.from(map.entries()).sort((a, b) => a[1].name.localeCompare(b[1].name)).forEach(([key, info]) => {
            const docItem = document.createElement('li');
            docItem.className = 'guid-nav-doc';
            const docTitle = document.createElement('div');
            docTitle.className = 'nav-doc-title';
            docTitle.textContent = info.name;
            docItem.append(docTitle);

            const famList = document.createElement('ul');
            famList.className = 'guid-nav-fams';

            Array.from(info.families).sort((a, b) => a.localeCompare(b)).forEach(f => {
                const li = document.createElement('li');
                const btn = document.createElement('button'); btn.type = 'button'; btn.className = 'nav-fam-item'; btn.textContent = f;
                btn.title = f;
                btn.onclick = () => { state.activeDocKey = key; state.activeFamily = f; paintDetail(); };
                if (state.activeDocKey === key && state.activeFamily === f) btn.classList.add('is-active');
                li.append(btn);
                famList.append(li);
            });

            docItem.append(famList);
            navList.append(docItem);
        });
    }

    function filteredDetailRows() {
        if (!state.detail.rows.length) return [];
        const idxPath = colIndex('RvtPath');
        const idxFam = colIndex('FamilyName');
        const key = state.activeDocKey;
        const fam = state.activeFamily;
        if (!key && !fam) return state.detail.rows;
        return state.detail.rows.filter(row => {
            const path = (row[idxPath] || '').toString();
            const famName = (row[idxFam] || '').toString();
            const docMatch = !key || ((path || '') === key);
            const famMatch = !fam || (famName === fam);
            return docMatch && famMatch;
        });
    }

    function colIndex(name) {
        return state.detail.columns.findIndex(c => c === name);
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
        if (state.activeTab === 'detail' && state.mode === 2) {
            return (state.detail.rows || []).length > 0;
        }
        return (state.summary.rows || []).length > 0;
    }

    function syncTabState() {
        btnTabSummary.classList.toggle('is-active', state.activeTab === 'summary');
        btnTabDetail.classList.toggle('is-active', state.activeTab === 'detail');
        btnTabDetail.disabled = (state.mode !== 2);
        tabPanelSummary.classList.toggle('is-hidden', state.activeTab !== 'summary');
        tabPanelDetail.classList.toggle('is-hidden', state.activeTab !== 'detail' || state.mode !== 2);
        if (state.activeTab === 'detail' && state.mode !== 2) {
            state.activeTab = 'summary';
        }
        exportBtn.disabled = !hasRowsForExport();
    }

    function setBusy(on) {
        state.busy = on;
        runBtn.disabled = on;
        exportBtn.disabled = on || !hasRowsForExport();
    }

    function persistRvts() {
        try { localStorage.setItem(LS_RVTS, JSON.stringify(state.rvtList || [])); } catch { }
    }

    function loadRvtList() {
        try {
            const raw = localStorage.getItem(LS_RVTS);
            const arr = JSON.parse(raw || '[]');
            if (Array.isArray(arr)) return arr;
        } catch { }
        return [];
    }
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
