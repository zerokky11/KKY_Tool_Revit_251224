// Resources/HubUI/js/core/topbar.js
import { div, toast } from './dom.js';
import { toggleTheme } from './theme.js';
import { setConn, ping, post } from './bridge.js';

export const APP_VERSION = 'v0.9.3';

let _docNameEl = null;
let _docSelectEl = null;
let _docList = [];
let _activeDoc = { name: '', path: '' };

export function renderTopbar(root, withBack = false, onBack = null) {
    const topbar = div('topbar');
    const left = div('topbar-left');

    if (withBack) {
        const backBtn = document.createElement('button');
        backBtn.className = 'btn btn-ghost';
        backBtn.type = 'button';
        backBtn.textContent = '허브 홈으로';

        const smartGoHome = () => {
            try { window.dispatchEvent(new CustomEvent('kkyt:go-home')); } catch (_) { /* noop */ }
            const before = location.href;
            try { history.back(); } catch (_) { /* ignore */ }
            setTimeout(() => {
                if (location.href === before) {
                    const url = new URL(location.href);
                    const parts = url.pathname.split('/');
                    parts[parts.length - 1] = 'index.html';
                    url.pathname = parts.join('/');
                    location.href = url.toString();
                }
            }, 80);
        };

        backBtn.onclick = () => {
            if (typeof onBack === 'function') {
                try { onBack(); } catch (_) { smartGoHome(); }
            } else {
                smartGoHome();
            }
        };
        left.append(backBtn);
    }

    left.append(buildBrand());

    const center = div('topbar-center');
    center.innerHTML = '<p class="topbar-tagline">Revit 워크플로우를 하나의 허브에서 관리하세요.</p>';

    const right = div('topbar-right');
    topbar.append(left, center, right);
    root.append(topbar);

    renderTopbarChips();
    setConn(true);
}

function buildBrand() {
    const wrap = div('topbar-brand');
    const logo = document.createElement('span');
    logo.className = 'topbar-logo';

    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 48 48');
    svg.setAttribute('aria-hidden', 'true');
    svg.setAttribute('focusable', 'false');
    svg.classList.add('hub-logo');

    const NS = svg.namespaceURI;
    const defs = document.createElementNS(NS, 'defs');
    const lg = document.createElementNS(NS, 'linearGradient');
    lg.setAttribute('id', 'kkygrad');
    lg.setAttribute('x1', '0'); lg.setAttribute('y1', '0');
    lg.setAttribute('x2', '1'); lg.setAttribute('y2', '1');
    const s1 = document.createElementNS(NS, 'stop'); s1.setAttribute('offset', '0'); s1.setAttribute('stop-color', 'currentColor');
    const s2 = document.createElementNS(NS, 'stop'); s2.setAttribute('offset', '1'); s2.setAttribute('stop-color', 'currentColor'); s2.setAttribute('stop-opacity', '0.5');
    lg.append(s1, s2); defs.append(lg);

    const hex = document.createElementNS(NS, 'path');
    hex.setAttribute('d', ['M', 24, 6, 'L', 38, 14, 'L', 38, 30, 'L', 24, 38, 'L', 10, 30, 'L', 10, 14, 'Z'].join(' '));
    hex.setAttribute('fill', 'none');
    hex.setAttribute('stroke', 'url(#kkygrad)');
    hex.setAttribute('stroke-width', '2.8');
    hex.setAttribute('stroke-linejoin', 'round');

    const mono = document.createElementNS(NS, 'g');
    mono.setAttribute('stroke', 'currentColor');
    mono.setAttribute('stroke-width', '2.6');
    mono.setAttribute('stroke-linecap', 'round');
    mono.setAttribute('stroke-linejoin', 'round');
    mono.setAttribute('fill', 'none');

    const k1a = document.createElementNS(NS, 'path'); k1a.setAttribute('d', 'M12 16 L12 32');
    const k1b = document.createElementNS(NS, 'path'); k1b.setAttribute('d', 'M12 24 L18 17');
    const k1c = document.createElementNS(NS, 'path'); k1c.setAttribute('d', 'M12 24 L18 31');

    const k2a = document.createElementNS(NS, 'path'); k2a.setAttribute('d', 'M22 16 L22 32');
    const k2b = document.createElementNS(NS, 'path'); k2b.setAttribute('d', 'M22 24 L28 17');
    const k2c = document.createElementNS(NS, 'path'); k2c.setAttribute('d', 'M22 24 L28 31');

    const y1 = document.createElementNS(NS, 'path'); y1.setAttribute('d', 'M34 16 L29 21');
    const y2 = document.createElementNS(NS, 'path'); y2.setAttribute('d', 'M34 16 L39 21');
    const y3 = document.createElementNS(NS, 'path'); y3.setAttribute('d', 'M34 21 L34 32');

    mono.append(k1a, k1b, k1c, k2a, k2b, k2c, y1, y2, y3);
    const shadow = document.createElementNS(NS, 'ellipse');
    shadow.setAttribute('cx', '24'); shadow.setAttribute('cy', '40');
    shadow.setAttribute('rx', '10'); shadow.setAttribute('ry', '1.5');
    shadow.setAttribute('fill', 'currentColor'); shadow.setAttribute('opacity', '0.12');

    svg.append(defs, hex, mono, shadow);
    logo.append(svg);

    const text = document.createElement('div');
    text.className = 'topbar-brand-text';
    text.innerHTML = '<strong>KKY Tool Hub</strong><span>Revit 작업 보조 통합 도구</span>';

    const ver = document.createElement('span');
    ver.className = 'topbar-version';
    ver.textContent = APP_VERSION;

    wrap.append(logo, text, ver);
    return wrap;
}

export function renderTopbarChips() {
    const actions = document.querySelector('.topbar-right');
    if (!actions) return;
    actions.innerHTML = '';

    const docCtrl = createDocControl();
    actions.append(docCtrl);

    const chipRow = document.createElement('div');
    chipRow.className = 'chip-row';

    const conn = createControlButton({
        id: 'connChip',
        label: '연결됨',
        icon: 'plug',
        classes: 'chip-toggle chip-connection',
        statusDot: true
    });
    conn.addEventListener('click', ping);
    chipRow.append(conn);

    const pin = createControlButton({
        id: 'pinChip',
        label: '항상 위',
        icon: 'pin',
        classes: 'chip-toggle pin-chip'
    });
    pin.setAttribute('aria-pressed', 'false');
    pin.classList.add('is-off');
    pin.onclick = () => { try { post('ui:toggle-topmost'); } catch (e) { console.error(e); } };
    chipRow.append(pin);

    const themeBtn = createControlButton({
        label: '테마',
        icon: 'theme',
        classes: 'chip-btn theme-chip'
    });
    themeBtn.setAttribute('aria-pressed', 'false');
    const applyThemeState = () => {
        const cur = document.documentElement.dataset.theme || 'dark';
        themeBtn.classList.toggle('is-dark', cur === 'dark');
        themeBtn.classList.toggle('is-active', cur === 'dark');
        themeBtn.setAttribute('aria-pressed', cur === 'dark' ? 'true' : 'false');
    };
    themeBtn.onclick = () => { toggleTheme(); applyThemeState(); };
    applyThemeState();
    chipRow.append(themeBtn);

    const help = createControlButton({
        label: '설정',
        icon: 'gear',
        classes: 'chip-btn settings-chip'
    });
    help.setAttribute('aria-expanded', 'false');
    help.addEventListener('click', () => toggleHelpPanel(help));
    chipRow.append(help);

    actions.append(chipRow);

    applyActiveDocumentState();
}

export function updateTopMost(on) {
    const pin = document.querySelector('#pinChip');
    if (!pin) return;
    const active = !!on;
    pin.classList.toggle('is-active', active);
    pin.classList.toggle('is-off', !active);
    pin.setAttribute('aria-pressed', active ? 'true' : 'false');
    const label = pin.querySelector('.chip-text');
    if (label) label.textContent = '항상 위';
}

function applyActiveDocumentState() {
    if (_docNameEl) {
        const hasDoc = !!_activeDoc.name;
        _docNameEl.textContent = hasDoc ? _activeDoc.name : '활성 문서 없음';
        _docNameEl.title = _activeDoc.path || _activeDoc.name || '';
        _docNameEl.classList.toggle('is-empty', !hasDoc);
    }
    updateConnectionChipLabel();
    rebuildDocSelect();
}

function updateConnectionChipLabel() {
    const label = document.querySelector('#connChip .chip-text');
    if (!label) return;
    label.textContent = _activeDoc.name ? `연결됨 · ${_activeDoc.name}` : '연결됨';
}

function normalizeDocs(payload) {
    let list = [];
    if (Array.isArray(payload?.docs)) list = payload.docs;
    if (Array.isArray(payload)) list = payload;
    return list
        .map(doc => ({ name: doc?.name || '', path: doc?.path || '' }))
        .filter(doc => !!doc.path);
}

function rebuildDocSelect() {
    if (!_docSelectEl) return;
    const activePath = (_activeDoc.path || '').toLowerCase();

    _docSelectEl.innerHTML = '';
    const placeholder = document.createElement('option');
    placeholder.value = '';
    placeholder.textContent = _docList.length ? '다른 문서로 전환' : '열린 Revit 문서가 없습니다';
    placeholder.disabled = !_docList.length;
    placeholder.selected = true;
    _docSelectEl.append(placeholder);

    let hasSelected = false;

    for (const doc of _docList) {
        const opt = document.createElement('option');
        opt.value = doc?.path || '';
        opt.textContent = doc?.name || doc?.path || '(이름 없는 문서)';
        opt.title = doc?.path || opt.textContent;
        if (opt.value && opt.value.toLowerCase() === activePath) {
            opt.selected = true;
            hasSelected = true;
        }
        _docSelectEl.append(opt);
    }

    if (hasSelected) placeholder.selected = false;
    _docSelectEl.disabled = !_docList.length;
}

function createDocControl() {
    const wrap = document.createElement('div');
    wrap.className = 'doc-chip';

    const glyph = document.createElement('span');
    glyph.className = 'chip-glyph';
    glyph.innerHTML = iconSvg('doc');

    const meta = document.createElement('div');
    meta.className = 'doc-meta';
    const label = document.createElement('span');
    label.className = 'doc-label';
    label.textContent = '연결 문서';
    const name = document.createElement('span');
    name.className = 'doc-name';
    name.textContent = '활성 문서 없음';
    meta.append(label, name);

    const select = document.createElement('select');
    select.className = 'doc-select';
    select.addEventListener('change', () => {
        const path = select.value;
        if (path && path !== _activeDoc.path) {
            try { toast?.('문서 전환은 Revit에서 프로젝트를 선택해 주세요.'); } catch (_) { console.warn('문서 전환은 Revit에서 프로젝트를 선택해 주세요.'); }
        }
        if (_activeDoc.path) {
            select.value = _activeDoc.path;
        } else {
            select.value = '';
        }
    });

    _docNameEl = name;
    _docSelectEl = select;
    rebuildDocSelect();

    wrap.append(glyph, meta, select);
    return wrap;
}

export function setActiveDocument(doc = {}) {
    _activeDoc = {
        name: doc?.name || '',
        path: doc?.path || ''
    };
    applyActiveDocumentState();
}

export function setDocList(payload) {
    const list = normalizeDocs(payload);
    _docList = list;
    rebuildDocSelect();
}

function toggleHelpPanel(trigger) {
    const existing = document.querySelector('.settings-backdrop');
    if (existing) {
        if (existing._escListener) {
            document.removeEventListener('keydown', existing._escListener);
        }
        existing.remove();
        if (trigger) trigger.setAttribute('aria-expanded', 'false');
        return;
    }

    const backdrop = document.createElement('div');
    backdrop.className = 'settings-backdrop';
    const panel = document.createElement('section');
    panel.className = 'settings-panel';

    const header = document.createElement('header');
    header.innerHTML = '<span>도움말 — KKY Tool Hub</span>';

    const body = document.createElement('div');
    body.className = 'body';
    body.innerHTML = `
        <div><strong>단축키</strong></div>
        <ul>
            <li><code>/</code> 검색 포커스</li>
            <li>카드 선택 후 <code>Enter</code> 실행, <code>F</code> 즐겨찾기</li>
            <li><code>Ctrl</code>+<code>Shift</code>+<code>L</code> 테마 전환</li>
            <li><code>Ctrl</code>+<code>Shift</code>+<code>T</code> 항상 위</li>
            <li><code>F1</code> 또는 <code>?</code> 도움말</li>
        </ul>`;

    const actions = document.createElement('div');
    actions.className = 'actions';
    const closeBtn = document.createElement('button');
    closeBtn.className = 'btn';
    closeBtn.type = 'button';
    closeBtn.textContent = '닫기';

    const closePanel = () => {
        backdrop.remove();
        if (trigger) trigger.setAttribute('aria-expanded', 'false');
        document.removeEventListener('keydown', escListener);
    };

    const escListener = (e) => { if (e.key === 'Escape') closePanel(); };

    document.addEventListener('keydown', escListener);
    backdrop._escListener = escListener;
    closeBtn.onclick = closePanel;
    actions.append(closeBtn);

    panel.append(header, body, actions);
    backdrop.append(panel);
    document.body.append(backdrop);
    trigger?.setAttribute('aria-expanded', 'true');

    backdrop.addEventListener('click', (e) => { if (e.target === backdrop) closePanel(); });
}

function createControlButton({ id, label, icon, classes = '', statusDot = false }) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = `control-chip ${classes}`.trim();
    if (id) btn.id = id;
    if (statusDot) {
        btn.classList.add('has-status-dot');
        const status = document.createElement('span');
        status.className = 'status-dot';
        status.setAttribute('aria-hidden', 'true');
        btn.append(status);
    }
    const glyph = document.createElement('span');
    glyph.className = 'chip-glyph';
    glyph.setAttribute('aria-hidden', 'true');
    glyph.innerHTML = iconSvg(icon);
    const text = document.createElement('span');
    text.className = 'chip-text';
    text.textContent = label;
    btn.append(glyph, text);
    return btn;
}

function iconSvg(name) {
    switch (name) {
        case 'plug':
            return '<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M8 3v5m8-5v5" stroke-linecap="round"/><path d="M6 8h12v5a6 6 0 1 1-12 0Z" stroke-linejoin="round"/><path d="M12 18v3" stroke-linecap="round"/></svg>';
        case 'pin':
            return '<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M8 4h8l-1 5 3 3-6 6-6-6 3-3z" fill="none"/><path d="M12 18v4" stroke-linecap="round"/></svg>';
        case 'theme':
            return '<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path class="half-dark" d="M12 3a9 9 0 0 1 0 18V3Z" fill="currentColor"/><path class="half-light" d="M12 21a9 9 0 0 1 0-18v18Z" fill="currentColor"/><circle cx="12" cy="12" r="8.5" fill="none"/></svg>';
        case 'gear':
            return '<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M20 13.5v-3l-2.1-.6a6.1 6.1 0 0 0-.6-1.4l1.2-1.8-2.1-2.1-1.8 1.2a6.1 6.1 0 0 0-1.4-.6L13.5 2h-3l-.6 2.1a6.1 6.1 0 0 0-1.4.6L6.7 3.5 4.6 5.6l1.2 1.8c-.26.44-.47.91-.6 1.4L3 10.5v3l2.1.6c.13.49.34.96.6 1.4l-1.2 1.8 2.1 2.1 1.8-1.2c.44.26.91.47 1.4.6l.6 2.1h3l.6-2.1c.49-.13.96-.34 1.4-.6l1.8 1.2 2.1-2.1-1.2-1.8c.26-.44.47-.91.6-1.4Z" fill="none"/><circle cx="12" cy="12" r="3.2" fill="none"/></svg>';
        case 'help':
            return '<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><circle cx="12" cy="12" r="9"/><path d="M10.89 9.05a1.11 1.11 0 0 1 2.22 0c0 1.11-1.67 1.11-1.67 2.78" stroke-linecap="round"/><circle cx="12" cy="15.5" r="0.5" fill="currentColor" stroke="none"/></svg>';
        case 'doc':
            return '<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M6 3h7l5 5v13H6z" fill="none"/><path d="M13 3v6h5" fill="none"/><path d="M9 13h6m-6 3h6" stroke-linecap="round"/></svg>';
        default:
            return '<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><circle cx="12" cy="12" r="8"/></svg>';
    }
}
