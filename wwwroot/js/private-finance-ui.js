/* PKG-015h-UI-01 — czytelniejsza nawigacja i prezentacja Private Finance */
(() => {
    const path = (window.location.pathname || '').toLowerCase();
    if (!path.includes('/privatefinance')) return;

    const root = document.querySelector('.pf-ui-root');
    if (!root) return;

    document.body.classList.add('pf-ui-v2');

    const modules = [
        { key: 'all', label: 'Wszystko' },
        { key: 'accounts', label: 'Konta' },
        { key: 'income', label: 'Dochody' },
        { key: 'benefits', label: 'Świadczenia' },
        { key: 'contributions', label: 'Wpłaty do domu' },
        { key: 'expenses', label: 'Wydatki' },
        { key: 'subscriptions', label: 'Subskrypcje' }
    ];

    const panels = Array.from(root.querySelectorAll('[data-pf-module]'));
    panels.forEach((panel, index) => {
        panel.classList.add('pf-ui-panel');
        if (!panel.id) panel.id = `pf-ui-${panel.dataset.pfModule}-${index + 1}`;

        // Bezpieczny poziomy scroll tabel bez ingerencji w ich zawartość.
        panel.querySelectorAll('table').forEach(table => {
            if (table.parentElement?.classList.contains('pf-ui-table-wrap')) return;
            const wrap = document.createElement('div');
            wrap.className = 'pf-ui-table-wrap';
            table.parentNode.insertBefore(wrap, table);
            wrap.appendChild(table);
        });

        // Czytelny licznik obiektów w nagłówku sekcji.
        const heading = panel.querySelector(':scope > h2');
        if (heading && !heading.parentElement?.classList.contains('pf-ui-module-title')) {
            const titleWrap = document.createElement('div');
            titleWrap.className = 'pf-ui-module-title';
            const headingBox = document.createElement('div');
            heading.parentNode.insertBefore(titleWrap, heading);
            titleWrap.appendChild(headingBox);
            headingBox.appendChild(heading);

            const tableRows = panel.querySelectorAll('tbody tr').length;
            const dataRows = panel.querySelectorAll('.data-row').length;
            const count = Math.max(tableRows, dataRows);
            if (count > 0) {
                const countEl = document.createElement('span');
                countEl.className = 'pf-ui-module-count';
                countEl.textContent = `${count} ${count === 1 ? 'pozycja' : 'pozycji'}`;
                titleWrap.appendChild(countEl);
            }
        }
    });

    // Statusy jako subtelne, kolorystyczne wskaźniki.
    const statusGroups = {
        ok: ['active', 'received', 'approved', 'zatwierdzona / zaksięgowana', 'aktywne', 'zaksięgowana'],
        warn: ['pending', 'planned', 'oczekuje na zatwierdzenie', 'oczekuje', 'planowane'],
        bad: ['rejected', 'odrzucona', 'failed', 'błąd'],
        muted: ['inactive', 'archived', 'nieaktywne', 'usunięte', 'anulowane']
    };
    panels.forEach(panel => panel.querySelectorAll('tbody td').forEach(cell => {
        const value = (cell.textContent || '').trim().toLowerCase();
        if (!value || value.length > 42) return;
        const group = Object.entries(statusGroups).find(([, values]) => values.some(v => value === v || value.startsWith(v)));
        if (!group) return;
        cell.classList.add('pf-ui-status-cell', `pf-ui-status-${group[0]}`);
    }));

    // Ujednolicone puste stany.
    panels.forEach(panel => {
        Array.from(panel.querySelectorAll(':scope > p.muted')).forEach(p => {
            const text = (p.textContent || '').trim().toLowerCase();
            if (!text.startsWith('nie masz') && !text.startsWith('brak ')) return;
            p.classList.add('pf-ui-empty');
        });
    });

    const n = name => Number(root.dataset[name] || 0) || 0;
    const stats = [
        ['▣', n('accountCount'), 'konta prywatne'],
        ['↓', n('incomeCount'), 'aktywne źródła dochodu'],
        ['↑', n('expenseCount'), 'wydatki'],
        ['↻', n('subscriptionCount'), 'subskrypcje'],
        ['⌂', n('contributionPendingCount'), 'wpłaty oczekujące']
    ];

    const overview = document.createElement('section');
    overview.className = 'pf-ui-overview';
    overview.setAttribute('aria-label', 'Szybkie podsumowanie finansów');
    stats.forEach(([icon, value, label]) => {
        const card = document.createElement('div');
        card.className = 'pf-ui-stat';
        card.innerHTML = `<span class="pf-ui-stat-icon">${icon}</span><span class="pf-ui-stat-copy"><strong>${value}</strong><span>${label}</span></span>`;
        overview.appendChild(card);
    });

    const dashboard = document.querySelector('.pf-bank-dashboard');
    if (dashboard) dashboard.insertAdjacentElement('afterend', overview);
    else root.insertAdjacentElement('beforebegin', overview);

    const switcher = document.createElement('nav');
    switcher.className = 'pf-ui-switcher';
    switcher.setAttribute('aria-label', 'Widok modułu Moje finanse');
    switcher.innerHTML = '<span class="pf-ui-switcher-label">Pokaż</span>';
    modules.forEach(module => {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'pf-ui-switch';
        button.dataset.module = module.key;
        button.textContent = module.label;
        switcher.appendChild(button);
    });
    overview.insertAdjacentElement('afterend', switcher);

    const storageKey = 'edom.privatefinance.view';
    const validKeys = new Set(modules.map(x => x.key));

    const setActive = (key, options = {}) => {
        if (!validKeys.has(key)) key = 'all';
        panels.forEach(panel => panel.classList.toggle('pf-ui-hidden', key !== 'all' && panel.dataset.pfModule !== key));
        switcher.querySelectorAll('.pf-ui-switch').forEach(button => {
            const active = button.dataset.module === key;
            button.classList.toggle('is-active', active);
            button.setAttribute('aria-pressed', active ? 'true' : 'false');
        });
        try { localStorage.setItem(storageKey, key); } catch { }

        if (options.scroll && key !== 'all') {
            const first = panels.find(panel => panel.dataset.pfModule === key);
            first?.scrollIntoView({ behavior: 'smooth', block: 'start' });
        }
    };

    switcher.addEventListener('click', event => {
        const button = event.target.closest('.pf-ui-switch');
        if (!button) return;
        setActive(button.dataset.module, { scroll: button.dataset.module !== 'all' });
    });

    // Rozszerzenie istniejącego dashboardu o „Wpłaty do domu”.
    const tileHost = document.querySelector('.pf-bank-tiles');
    if (tileHost && !tileHost.querySelector('.pf-bank-tile-contributions')) {
        const tile = document.createElement('a');
        tile.href = '#pf-ui-contributions';
        tile.className = 'pf-bank-tile pf-bank-tile-contributions';
        tile.dataset.pfUiModule = 'contributions';
        tile.innerHTML = '<span class="pf-bank-tile-icon">⌂</span><span><strong>Wpłaty do domu</strong><small>Historia i status wpłat</small></span><span class="pf-bank-tile-arrow">›</span>';
        tileHost.appendChild(tile);
    }

    // Kafelki na górze przełączają teraz widok, a nie tylko przewijają stronę.
    const tileModuleMap = {
        'pf-bank-tile-accounts': 'accounts',
        'pf-bank-tile-income': 'income',
        'pf-bank-tile-benefits': 'benefits',
        'pf-bank-tile-expenses': 'expenses',
        'pf-bank-tile-subscriptions': 'subscriptions',
        'pf-bank-tile-contributions': 'contributions'
    };
    document.querySelectorAll('.pf-bank-tile').forEach(tile => {
        const module = Object.entries(tileModuleMap).find(([className]) => tile.classList.contains(className))?.[1];
        if (!module) return;
        tile.classList.remove('is-disabled');
        tile.addEventListener('click', event => {
            event.preventDefault();
            setActive(module, { scroll: true });
        });
    });

    let initial = 'all';
    const hash = (window.location.hash || '').toLowerCase();
    const hashModule = modules.find(x => x.key !== 'all' && hash.includes(x.key))?.key;
    if (hashModule) initial = hashModule;
    else {
        try {
            const stored = localStorage.getItem(storageKey);
            if (stored && validKeys.has(stored)) initial = stored;
        } catch { }
    }
    setActive(initial);
})();
