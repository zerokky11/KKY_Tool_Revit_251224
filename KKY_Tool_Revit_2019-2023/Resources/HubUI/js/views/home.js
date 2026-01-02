// Resources/HubUI/js/views/home.js
import { clear, div, debounce, toast } from '../core/dom.js';
import { getFavs, toggleFav, getLast, saveCardOrder, getCardOrder } from '../core/state.js';

const CATS = { dup: '검토', conn: '진단', export: '좌표', paramprop: '속성', segmentpms: '속성' };
const QKEY = 'kky_q';
const LAYOUT_KEY = 'kky_home_layout';
const CAT_KEY = 'kky_home_cat';

const GROUPS = {
    fav: { icon: '★', label: '즐겨찾기' },
    all: { icon: '●', label: '전체' },
    modeling: { icon: '▣', label: '모델링 검토' },
    property: { icon: '◆', label: '속성 검토' },
    utility: { icon: '▲', label: '유틸리티' }
};

const CARD_GROUP = {
    dup: 'modeling',
    conn: 'property',
    export: 'utility',
    paramprop: 'property',
    segmentpms: 'property'
};

const FEATURE_META = {
    dup: {
        icon: 'dup',
        title: '중복검토',
        subtitle: 'Duplicate Inspector',
        desc: '중복 객체 요소를 그룹별로 확인하고 삭제/되돌리기를 관리합니다.'
    },
    conn: {
        icon: 'conn',
        title: '위치기반 Parameter값 일치 여부 검토',
        subtitle: 'Connector Diagnostics',
        desc: '허용범위 내에 있는 객체(커넥터)를 대상으로 지정, Parameter 값을 검토합니다..'
    },
    export: {
        icon: 'export',
        title: 'Project/Survey Point 추출',
        subtitle: 'Export Points with Angle',
        desc: '선택한 RVT 파일에서 Point 좌표와 진북각을 추출 후 Excel 스키마로 저장합니다.'
    },
    paramprop: {
        icon: 'paramprop',
        title: '공유 파라미터 연동',
        subtitle: 'Shared Parameter Propagator',
        desc: '복합 패밀리의 공유 파라미터를 추가/연동/검증하여 일관된 속성 관리를 돕습니다.'
    },
    segmentpms: {
        icon: 'paramprop',
        title: 'Segment ↔ PMS 매핑',
        subtitle: 'PipeType Segment Check',
        desc: 'PipeType별 Segment를 PMS Segment와 매핑 후 ND별 ID/OD를 검증합니다.'
    }
};

const TOTAL_FEATURES = Object.keys(FEATURE_META).length;

const STAR_SVG = '<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M12 4.5l2.3 4.9 5.4.8-3.9 3.9.9 5.4L12 16.8 7.3 19.5l.9-5.4-3.9-3.9 5.4-.8Z" stroke-linecap="round" stroke-linejoin="round" fill="none" stroke-width="1.6"/></svg>';

export function renderHome(root) {
    const target = root || document.getElementById('view-root') || document.getElementById('app');
    clear(target);

    const view = div('home-view');
    view.dataset.layout = 'card';

    const last = getLast();
    view.append(buildHero(last));

    const controls = buildControls();
    view.append(controls.wrap);

    const filterRow = div('home-filter-row');
    const filterWrap = div('filter-chips');
    const filterButtons = [];
    Object.entries(GROUPS).forEach(([id, meta]) => {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'filter-chip';
        btn.dataset.sid = id;
        btn.innerHTML = `<span class="chip-icon">${meta.icon}</span><span>${meta.label}</span>`;
        btn.onclick = () => { setCat(id); applyFilter(q.value); syncFilterActive(); };
        filterButtons.push(btn);
        filterWrap.append(btn);
    });
    filterRow.append(filterWrap);
    view.append(filterRow);

    const grid = div('card-grid');
    const iconPanel = document.createElement('section');
    iconPanel.className = 'home-icon-panel panel';
    iconPanel.hidden = true;
    const iconHead = document.createElement('div');
    iconHead.className = 'icon-panel-head';
    iconHead.innerHTML = '<h3>아이콘 빠른 실행</h3><p>아이콘을 클릭하면 해당 기능으로 이동합니다.</p>';
    const iconBoard = div('home-icon-board');
    iconPanel.append(iconHead, iconBoard);

    const empty = div('home-empty');
    empty.innerHTML = '<span>조건에 맞는 기능이 없습니다. 검색어나 필터를 조정해 보세요.</span>';

    const features = {};
    const baseOrder = Object.keys(FEATURE_META);
    const order = getCardOrder(baseOrder);
    const appendFeature = (viewId) => {
        if (features[viewId]) return;
        const meta = FEATURE_META[viewId];
        if (!meta) return;
        const card = toolCard(meta, viewId, last);
        const iconItem = iconTile(meta, viewId);
        features[viewId] = { view: viewId, card, icon: iconItem };
        grid.append(card);
        iconBoard.append(iconItem);
    };
    order.forEach(appendFeature);
    baseOrder.forEach(appendFeature);

    view.append(grid, iconPanel, empty);
    target.append(view);

    const q = controls.input;
    const segCard = controls.segCard;
    const segList = controls.segList;

    if (!localStorage.getItem(CAT_KEY)) setCat('all');
    const savedQ = localStorage.getItem(QKEY) || '';
    if (savedQ) q.value = savedQ;

    const layoutPref = localStorage.getItem(LAYOUT_KEY);
    const savedLayout = (layoutPref === 'icon' || layoutPref === 'list') ? 'icon' : 'card';
    setLayout(savedLayout);

    const debounced = debounce(() => applyFilter(q.value), 200);
    q.addEventListener('input', debounced);
    segCard.addEventListener('click', () => setLayout('card'));
    segList.addEventListener('click', () => setLayout('icon'));

    enableCardDnD(grid, iconBoard);
    syncFavorites();
    applyFilter(savedQ);
    syncFilterActive();

    function buildHero(lastRun) {
        const hero = document.createElement('section');
        hero.className = 'home-hero';
        hero.innerHTML = `
            <div class="hero-text">
                <p class="eyebrow">KKY Tool Hub</p>
                <h2>Revit 작업을 위한 통합 허브</h2>
                <p>기능에 대한 문의는 kkykiki89@nate.com 으로 보내주세요!!</p>
            </div>
            <div class="hero-meta">
                <div class="meta-card">
                    <span>총 기능</span>
                    <strong>${TOTAL_FEATURES}</strong>
                </div>
                <div class="meta-card" data-role="last"></div>
            </div>`;
        if (lastRun && hero.querySelector('[data-role="last"]')) {
            const lastBox = hero.querySelector('[data-role="last"]');
            const name = CATS[lastRun.view] || '기능';
            lastBox.innerHTML = `<span>최근 실행</span><strong>${name}</strong>`;
        } else {
            const box = hero.querySelector('[data-role="last"]');
            if (box) box.innerHTML = '<span>최근 실행</span><strong>—</strong>';
        }
        return hero;
    }

    function buildControls() {
        const wrap = div('home-controls');
        const search = document.createElement('label');
        search.className = 'search-field';
        search.innerHTML = '<span class="search-ico"></span>';
        const input = document.createElement('input');
        input.type = 'search';
        input.placeholder = '기능 검색 (예: 커넥터, 북각)';
        search.append(input);

        const toggle = div('layout-toggle');
        const segCardBtn = document.createElement('button');
        segCardBtn.type = 'button';
        segCardBtn.className = 'layout-btn is-active';
        segCardBtn.innerHTML = '<span>카드</span>';
        const segListBtn = document.createElement('button');
        segListBtn.type = 'button';
        segListBtn.className = 'layout-btn';
        segListBtn.innerHTML = '<span>아이콘</span>';
        toggle.append(segCardBtn, segListBtn);

        wrap.append(search, toggle);
        return { wrap, input, segCard: segCardBtn, segList: segListBtn };
    }

    function getCat() {
        const c = localStorage.getItem(CAT_KEY);
        return (c === 'fav' || c === 'all' || c === 'modeling' || c === 'property' || c === 'utility') ? c : 'all';
    }

    function setCat(value) {
        const v = (value === 'fav' || value === 'all' || value === 'modeling' || value === 'property' || value === 'utility') ? value : 'all';
        localStorage.setItem(CAT_KEY, v);
    }

    function setLayout(mode) {
        const m = (mode === 'icon') ? 'icon' : 'card';
        view.dataset.layout = m;
        grid.hidden = (m === 'icon');
        iconPanel.hidden = (m !== 'icon');
        segCard.classList.toggle('is-active', m === 'card');
        segList.classList.toggle('is-active', m === 'icon');
        segCard.setAttribute('aria-pressed', String(m === 'card'));
        segList.setAttribute('aria-pressed', String(m === 'icon'));
        localStorage.setItem(LAYOUT_KEY, m);
    }

    function syncFilterActive() {
        const cat = getCat();
        filterButtons.forEach(btn => btn.classList.toggle('is-active', btn.dataset.sid === cat));
    }

    function syncFavorites() {
        const favs = new Set(getFavs());
        Object.values(features).forEach(({ view, card, icon }) => {
            const on = favs.has(view);
            [card?._favBtn, icon?._favBtn].forEach(btn => {
                if (!btn) return;
                btn.classList.toggle('is-active', on);
                btn.setAttribute('aria-pressed', on ? 'true' : 'false');
            });
            if (card) card.classList.toggle('is-favorite', on);
            if (icon) icon.classList.toggle('is-favorite', on);
        });
    }

    function handleFavToggle(viewId) {
        toggleFav(viewId);
        syncFavorites();
        applyFilter(q.value);
    }

    function goTo(viewId) {
        if (!viewId) return;
        location.hash = `#${viewId}`;
        window.dispatchEvent(new HashChangeEvent('hashchange'));
    }

    function escapeHtml(s) {
        return String(s ?? '').replace(/[&<>"']/g, m => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[m]));
    }

    function highlight(text, needle) {
        if (!needle) return escapeHtml(text);
        const re = new RegExp(`(${needle.replace(/[.*+?^${}()|[\\]\\]/g, '\\$&')})`, 'ig');
        return escapeHtml(text).replace(re, '<mark>$1</mark>');
    }

    function applyFilter(needle) {
        const n = (needle || '').trim().toLowerCase();
        localStorage.setItem(QKEY, n);
        const favs = new Set(getFavs());
        const cat = getCat();
        let visible = 0;
        Object.values(features).forEach(({ view, card, icon }) => {
            if (!card || !icon) return;
            const isFav = favs.has(view);
            const byCat = (cat === 'all') ? true : (cat === 'fav' ? isFav : (card.dataset.group === cat));
            const matchQ = !n || (card.dataset.search || '').includes(n);
            const show = byCat && matchQ;
            card.style.display = show ? '' : 'none';
            icon.style.display = show ? '' : 'none';
            if (show) {
                const t = card.querySelector('[data-title]');
                const d = card.querySelector('[data-desc]');
                const c = card.querySelector('[data-cat]');
                if (t) t.innerHTML = highlight(t.dataset.orig, n);
                if (d) d.innerHTML = highlight(d.dataset.orig, n);
                if (c) c.innerHTML = '# ' + highlight(c.dataset.orig, n);
                visible++;
            }
        });
        empty.style.display = visible ? 'none' : '';
    }

    function toolCard(meta, view, lastRun) {
        const card = div('card feature-card');
        card.dataset.view = view;
        card.dataset.group = CARD_GROUP[view] || 'modeling';
        card.dataset.cat = view;
        card.dataset.search = [meta.title, meta.subtitle, meta.desc, CATS[view] || ''].join(' ').toLowerCase();
        card.draggable = true;

        const head = div('card-head');
        const icon = div('card-icon');
        icon.setAttribute('aria-hidden', 'true');
        icon.innerHTML = featureIcon(meta.icon);

        const title = div('card-title');
        const name = document.createElement('span');
        name.className = 'card-name';
        name.dataset.title = '';
        name.dataset.orig = meta.title;
        name.textContent = meta.title;
        const subtitle = document.createElement('span');
        subtitle.className = 'card-sub';
        subtitle.textContent = meta.subtitle;
        title.append(name, subtitle);

        if (lastRun && lastRun.view === view) {
            const recent = document.createElement('span');
            recent.className = 'recent-badge';
            recent.textContent = '최근 실행';
            title.append(recent);
        }

        const actions = div('card-actions');
        const star = document.createElement('button');
        star.type = 'button';
        star.className = 'fav-toggle card-fav';
        star.setAttribute('aria-pressed', 'false');
        star.title = '즐겨찾기';
        star.innerHTML = STAR_SVG;
        star.onclick = e => { e.stopPropagation(); handleFavToggle(view); };
        actions.append(star);
        head.append(icon, title, actions);

        const body = document.createElement('p');
        body.className = 'card-desc';
        body.dataset.desc = '';
        body.dataset.orig = meta.desc;
        body.innerHTML = meta.desc;

        const foot = div('card-foot');
        const catBadge = document.createElement('span');
        catBadge.className = 'cat-badge';
        catBadge.dataset.cat = '';
        catBadge.dataset.orig = CATS[view] || '기능';
        catBadge.innerHTML = '# ' + catBadge.dataset.orig;

        const open = document.createElement('button');
        open.type = 'button';
        open.className = 'btn btn-primary open-btn';
        open.textContent = '열기';
        open.onclick = e => { e.stopPropagation(); goTo(view); };
        foot.append(catBadge, open);

        card.append(head, body, foot);
        card._favBtn = star;
        return card;
    }

    function iconTile(meta, view) {
        const wrap = div('home-icon-item');
        wrap.dataset.view = view;
        wrap.dataset.group = CARD_GROUP[view] || 'modeling';
        wrap.dataset.cat = view;
        wrap.dataset.search = [meta.title, meta.subtitle, meta.desc, CATS[view] || ''].join(' ').toLowerCase();

        const fav = document.createElement('button');
        fav.type = 'button';
        fav.className = 'fav-toggle home-icon-fav';
        fav.setAttribute('aria-pressed', 'false');
        fav.title = '즐겨찾기';
        fav.innerHTML = STAR_SVG;
        fav.onclick = e => { e.stopPropagation(); handleFavToggle(view); };

        const tile = document.createElement('button');
        tile.type = 'button';
        tile.className = 'home-icon-hit';
        tile.innerHTML = `<span class="icon">${featureIcon(meta.icon)}</span><span class="home-icon-label">${meta.title}</span>`;
        tile.onclick = () => goTo(view);

        wrap.append(fav, tile);
        wrap._favBtn = fav;
        return wrap;
    }

    function enableCardDnD(gridEl, iconBoardEl) {
        let dragEl = null, placeholder = null;
        function persist() {
            const ids = [...gridEl.querySelectorAll('.card')].map(x => x.dataset.view);
            saveCardOrder(ids);
            toast('카드 순서를 저장했습니다.', 'ok');
            if (iconBoardEl) {
                ids.forEach(id => {
                    const icon = features[id]?.icon;
                    if (icon) iconBoardEl.appendChild(icon);
                });
            }
        }
        gridEl.addEventListener('dragstart', e => {
            const card = e.target.closest('.card'); if (!card) return;
            dragEl = card; dragEl.classList.add('dragging');
            placeholder = document.createElement('div');
            placeholder.className = 'card placeholder';
            placeholder.style.height = `${card.offsetHeight}px`;
            e.dataTransfer.effectAllowed = 'move';
            e.dataTransfer.setData('text/plain', card.dataset.view);
        });
        gridEl.addEventListener('dragend', () => {
            if (dragEl) { dragEl.classList.remove('dragging'); dragEl = null; }
            if (placeholder) { placeholder.remove(); placeholder = null; }
            persist();
        });
        gridEl.addEventListener('dragover', e => {
            e.preventDefault();
            const after = getAfter(gridEl, e.clientY);
            if (!placeholder) return;
            if (after == null) gridEl.appendChild(placeholder);
            else gridEl.insertBefore(placeholder, after);
        });
        gridEl.addEventListener('drop', e => {
            e.preventDefault();
            if (dragEl && placeholder) { gridEl.insertBefore(dragEl, placeholder); }
        });
        function getAfter(container, y) {
            const els = [...container.querySelectorAll('.card:not(.dragging)')];
            return els.reduce((closest, child) => {
                const box = child.getBoundingClientRect();
                const offset = y - box.top - box.height / 2;
                return (offset < 0 && offset > closest.offset) ? { offset, element: child } : closest;
            }, { offset: -Infinity }).element || null;
        }
    }
}

function featureIcon(kind) {
    switch (kind) {
        case 'dup':
            return '<svg viewBox="0 0 32 32" aria-hidden="true" focusable="false"><rect x="6.5" y="9" width="11.5" height="11.5" rx="2.4"/><rect x="12.5" y="5.5" width="11.5" height="11.5" rx="2.4"/><circle cx="21" cy="20.5" r="4.2"/><path d="M23.8 23.2 28 27.5" stroke-linecap="round"/></svg>';
        case 'conn':
            return '<svg viewBox="0 0 32 32" aria-hidden="true" focusable="false"><circle cx="9" cy="10" r="3.1"/><circle cx="23" cy="8.5" r="3.1"/><circle cx="16" cy="22.5" r="3.1"/><path d="M9 10 23 8.5" stroke-linecap="round"/><path d="M9 10 16 22.5" stroke-linecap="round"/><path d="M23 8.5 16 22.5" stroke-linecap="round"/><path d="M16 22.5l5.5 5" stroke-linecap="round"/><circle cx="24.8" cy="24.2" r="2.2"/></svg>';
        case 'export':
            return '<svg viewBox="0 0 32 32" aria-hidden="true" focusable="false"><path d="M9 25.5h15" stroke-linecap="round"/><path d="M13 28V8.5" stroke-linecap="round"/><path d="M13 8.5 17.5 4" stroke-linecap="round"/><circle cx="21.5" cy="12" r="3.3"/><path d="M21.5 8.7a6.5 6.5 0 0 1 6.5 6.5" stroke-linecap="round"/><path d="M18.8 14.5 26 21.7" stroke-linecap="round"/><path d="M9 18l4 4" stroke-linecap="round"/></svg>';
        case 'paramprop':
            return '<svg viewBox="0 0 32 32" aria-hidden="true" focusable="false"><path d="M7 24.5h18" stroke-linecap="round"/><rect x="8.5" y="9" width="8" height="8" rx="1.8"/><rect x="15.5" y="14.5" width="8" height="8" rx="1.8"/><path d="M16 13.5 20.5 9" stroke-linecap="round"/><path d="M12.5 17.5 17 13" stroke-linecap="round"/></svg>';
        default:
            return '<svg viewBox="0 0 32 32" aria-hidden="true" focusable="false"><circle cx="16" cy="16" r="6"/></svg>';
    }
}
