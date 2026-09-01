(() => {
    const root = document.documentElement;
    const body = document.body;
    const collapseButton = document.querySelector('[data-sidebar-collapse]');
    const openButton = document.querySelector('[data-sidebar-open]');
    const closeTargets = document.querySelectorAll('[data-sidebar-close]');
    const scrollTop = document.querySelector('[data-scroll-top]');
    const storageKey = 'edom.sidebar.collapsed';

    const setCollapsed = (collapsed) => {
        body.classList.toggle('sidebar-collapsed', collapsed);
        if (collapseButton) {
            collapseButton.setAttribute('aria-label', collapsed ? 'Rozwiń pasek boczny' : 'Zwiń pasek boczny');
            collapseButton.setAttribute('title', collapsed ? 'Rozwiń pasek boczny' : 'Zwiń pasek boczny');
        }
        try { localStorage.setItem(storageKey, collapsed ? '1' : '0'); } catch { }
    };

    try {
        if (localStorage.getItem(storageKey) === '1') setCollapsed(true);
    } catch { }

    collapseButton?.addEventListener('click', () => setCollapsed(!body.classList.contains('sidebar-collapsed')));
    openButton?.addEventListener('click', () => body.classList.add('sidebar-mobile-open'));
    closeTargets.forEach(x => x.addEventListener('click', () => body.classList.remove('sidebar-mobile-open')));

    document.querySelectorAll('.sidebar a').forEach(link => {
        link.addEventListener('click', () => body.classList.remove('sidebar-mobile-open'));
    });

    document.querySelectorAll('details.crud-details').forEach(details => {
        details.addEventListener('toggle', () => details.classList.toggle('is-open', details.open));
    });

    document.querySelectorAll('form').forEach(form => {
        form.addEventListener('submit', () => {
            const button = form.querySelector('button[type="submit"]');
            if (!button || button.disabled) return;
            button.classList.add('is-busy');
            button.setAttribute('aria-busy', 'true');
        });
    });

    const updateScrollButton = () => {
        if (!scrollTop) return;
        scrollTop.classList.toggle('visible', window.scrollY > 500);
    };
    window.addEventListener('scroll', updateScrollButton, { passive: true });
    updateScrollButton();
    scrollTop?.addEventListener('click', () => window.scrollTo({ top: 0, behavior: 'smooth' }));

    root.classList.add('js-ready');
})();

/* PKG-015f — bank-style Private Finance presentation */
(() => {
    const path = (window.location.pathname || '').toLowerCase();
    if (!path.includes('/privatefinance')) return;

    const body = document.body;
    const main = document.querySelector('.main-content, main, [role="main"]') || document.body;
    body.classList.add('private-finance-bank-view');

    const normalize = value => (value || '').trim().toLowerCase();
    const definitions = [
        { key: 'accounts', icon: '▣', title: 'Konta', hint: 'Salda i rachunki', words: ['konta', 'rachunki', 'konto finansowe'] },
        { key: 'income', icon: '↓', title: 'Dochody', hint: 'Wynagrodzenia i wpływy', words: ['dochody', 'wynagrodzenie', 'źródła dochodu'] },
        { key: 'benefits', icon: '＋', title: 'Świadczenia', hint: 'Zasiłki i świadczenia', words: ['świadczenia', 'zasiłki'] },
        { key: 'expenses', icon: '↑', title: 'Wydatki', hint: 'Koszty i płatności', words: ['wydatki', 'koszty'] },
        { key: 'subscriptions', icon: '↻', title: 'Subskrypcje', hint: 'Płatności cykliczne', words: ['subskrypcje', 'abonamenty'] }
    ];

    const headings = Array.from(main.querySelectorAll('h1,h2,h3,h4,summary'));
    const usedSections = new Set();

    const findContainer = heading => {
        let node = heading;
        for (let i = 0; i < 5 && node; i++, node = node.parentElement) {
            if (!node || node === main || node === document.body) break;
            if (node.matches('section,.panel,.card,details,article')) return node;
        }
        return heading.parentElement;
    };

    definitions.forEach(def => {
        const heading = headings.find(h => {
            const text = normalize(h.textContent);
            return def.words.some(word => text === word || text.includes(word));
        });
        if (!heading) return;
        const container = findContainer(heading);
        if (!container || usedSections.has(container)) return;
        usedSections.add(container);
        container.classList.add('pf-bank-section', `pf-bank-section-${def.key}`);
        if (!container.id) container.id = `pf-${def.key}`;
        def.target = `#${container.id}`;
    });

    const firstContent = main.querySelector('.hero, .page-header, h1')?.closest('.hero,.page-header,section') || main.firstElementChild;
    const dashboard = document.createElement('section');
    dashboard.className = 'pf-bank-dashboard';
    dashboard.innerHTML = `
        <div class="pf-bank-dashboard-head">
            <div>
                <span class="pf-bank-eyebrow">Centrum finansów osobistych</span>
                <h2>Moje finanse</h2>
                <p>Konta, wpływy, wydatki i płatności cykliczne w jednym miejscu.</p>
            </div>
            <div class="pf-bank-privacy">Prywatny obszar użytkownika</div>
        </div>
        <div class="pf-bank-tiles"></div>
        <div class="pf-bank-quick-actions" aria-label="Szybkie akcje"></div>`;

    if (firstContent?.parentNode) firstContent.parentNode.insertBefore(dashboard, firstContent.nextSibling);
    else main.prepend(dashboard);

    const tileHost = dashboard.querySelector('.pf-bank-tiles');
    definitions.forEach(def => {
        const tile = document.createElement(def.target ? 'a' : 'div');
        tile.className = `pf-bank-tile pf-bank-tile-${def.key}${def.target ? '' : ' is-disabled'}`;
        if (def.target) tile.href = def.target;
        tile.innerHTML = `<span class="pf-bank-tile-icon">${def.icon}</span><span><strong>${def.title}</strong><small>${def.hint}</small></span><span class="pf-bank-tile-arrow">›</span>`;
        tileHost.appendChild(tile);
    });

    const actionPatterns = [
        { label: '+ Dodaj konto', words: ['dodaj konto', 'nowe konto'] },
        { label: '+ Dodaj dochód', words: ['dodaj dochód', 'dodaj wynagrodzenie', 'nowy dochód'] },
        { label: '+ Dodaj wydatek', words: ['dodaj wydatek', 'nowy wydatek'] },
        { label: '+ Dodaj subskrypcję', words: ['dodaj subskrypcję', 'nowa subskrypcja'] }
    ];
    const allButtons = Array.from(main.querySelectorAll('button,a.btn,input[type="submit"]'));
    const actionHost = dashboard.querySelector('.pf-bank-quick-actions');
    actionPatterns.forEach(pattern => {
        const original = allButtons.find(button => {
            const text = normalize(button.textContent || button.value);
            return pattern.words.some(word => text.includes(word));
        });
        if (!original) return;
        const quick = document.createElement('button');
        quick.type = 'button';
        quick.className = 'pf-bank-quick-action';
        quick.textContent = pattern.label;
        quick.addEventListener('click', () => {
            original.scrollIntoView({ behavior: 'smooth', block: 'center' });
            window.setTimeout(() => original.click(), 350);
        });
        actionHost.appendChild(quick);
    });
    const householdContributionLink = document.createElement('a');
    householdContributionLink.className = 'pf-bank-quick-action pf-bank-quick-link';
    householdContributionLink.href = '/HouseholdContributionPayments';
    householdContributionLink.textContent = '⌂ Wpłata do domu';
    householdContributionLink.title = 'Zgłoś wysłaną wpłatę do domu lub sprawdź jej status';
    actionHost.appendChild(householdContributionLink);

    if (!actionHost.children.length) actionHost.remove();

    Array.from(main.querySelectorAll('.pf-bank-section')).forEach(section => {
        const header = section.querySelector('h2,h3,h4,summary');
        if (header) header.classList.add('pf-bank-section-title');
        section.querySelectorAll('details.crud-details, details:not(.assignment-card)').forEach(x => x.classList.add('pf-bank-detail-card'));
    });
})();


/* PKG-015g — financial reminders on Private Finance dashboard */
(() => {
    const path = (window.location.pathname || '').toLowerCase();
    if (!path.includes('/privatefinance')) return;

    const dashboard = document.querySelector('.pf-bank-dashboard');
    if (!dashboard) return;

    const panel = document.createElement('section');
    panel.className = 'pf-reminders-panel';
    panel.innerHTML = `
        <div class="pf-reminders-head">
            <div>
                <span class="pf-bank-eyebrow">Nadchodzące i przypomnienia</span>
                <h3>Co wydarzy się w finansach</h3>
                <p>Subskrypcje, wypłaty, płatności i cykliczna wpłata do domu.</p>
            </div>
            <a class="btn btn-secondary btn-small" href="/Notifications">Centrum powiadomień</a>
        </div>
        <div class="pf-reminder-kpis" aria-label="Podsumowanie przypomnień">
            <div><strong data-reminder-kpi="today">–</strong><span>dzisiaj</span></div>
            <div><strong data-reminder-kpi="soon">–</strong><span>nadchodzące</span></div>
            <div><strong data-reminder-kpi="overdue">–</strong><span>po terminie</span></div>
            <div><strong data-reminder-kpi="house">–</strong><span>wpłaty do domu</span></div>
        </div>
        <div class="pf-reminder-list"><div class="pf-reminders-loading">Wczytywanie terminów…</div></div>`;

    dashboard.insertAdjacentElement('afterend', panel);

    const list = panel.querySelector('.pf-reminder-list');
    const setKpi = (key, value) => {
        const el = panel.querySelector(`[data-reminder-kpi="${key}"]`);
        if (el) el.textContent = String(value ?? 0);
    };
    const escapeHtml = value => String(value ?? '')
        .replaceAll('&', '&amp;')
        .replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;')
        .replaceAll('\"', '&quot;')
        .replaceAll("'", '&#39;');
    const money = (minor, currency) => {
        if (minor === null || minor === undefined || !currency) return '';
        try {
            return new Intl.NumberFormat('pl-PL', { style: 'currency', currency }).format(Number(minor) / 100);
        } catch {
            return `${(Number(minor) / 100).toFixed(2)} ${currency}`;
        }
    };
    const dateLabel = raw => {
        if (!raw) return '';
        const value = new Date(`${raw}T00:00:00`);
        if (Number.isNaN(value.getTime())) return raw;
        return new Intl.DateTimeFormat('pl-PL', { day: '2-digit', month: 'short', year: 'numeric' }).format(value);
    };
    const dueLabel = days => {
        if (days < 0) return `${Math.abs(days)} dni po terminie`;
        if (days === 0) return 'dzisiaj';
        if (days === 1) return 'jutro';
        return `za ${days} dni`;
    };
    const icon = type => ({
        Subscription: '↻',
        Income: '↓',
        Payment: '↑',
        HouseholdContribution: '⌂'
    }[type] || '•');

    fetch('/FinanceReminders/Summary', { credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
        .then(response => {
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            return response.json();
        })
        .then(data => {
            const items = Array.isArray(data.items) ? data.items : [];
            setKpi('today', data.dueTodayCount);
            setKpi('soon', data.upcomingCount);
            setKpi('overdue', data.overdueCount);
            setKpi('house', data.householdContributionCount);

            if (!items.length) {
                list.innerHTML = `<div class="pf-reminders-empty"><strong>Brak pilnych terminów.</strong><span>Nie znaleziono nadchodzących płatności ani wpływów w najbliższych 14 dniach.</span></div>`;
                return;
            }

            list.innerHTML = '';
            items.slice(0, 8).forEach(item => {
                const row = document.createElement('article');
                row.className = `pf-reminder-item severity-${String(item.severity || 'Future').toLowerCase()}`;
                const amount = money(item.amountMinor, item.currencyCode);
                const contributionAction = item.type === 'HouseholdContribution' && item.sourceId
                    ? `<a class="btn btn-primary btn-small pf-reminder-action" href="/HouseholdContributionPayments?obligationId=${encodeURIComponent(item.sourceId)}">Wysłałem pieniądze</a>`
                    : '';
                row.innerHTML = `
                    <span class="pf-reminder-icon">${icon(item.type)}</span>
                    <span class="pf-reminder-copy">
                        <small>${escapeHtml(item.category)}</small>
                        <strong>${escapeHtml(item.title)}</strong>
                        <span>${escapeHtml(item.description)}</span>
                    </span>
                    <span class="pf-reminder-date">
                        <strong>${dueLabel(Number(item.daysUntil || 0))}</strong>
                        <small>${dateLabel(item.dueOn)}</small>
                        ${amount ? `<b>${escapeHtml(amount)}</b>` : ''}
                        ${contributionAction}
                    </span>`;
                list.appendChild(row);
            });

            if (items.length > 8) {
                const more = document.createElement('a');
                more.className = 'pf-reminders-more';
                more.href = '/Notifications';
                more.textContent = `+ ${items.length - 8} kolejnych terminów — otwórz powiadomienia`;
                list.appendChild(more);
            }
        })
        .catch(() => {
            list.innerHTML = `<div class="pf-reminders-empty"><strong>Nie udało się wczytać terminów.</strong><span>Odśwież stronę. Pozostałe funkcje finansów działają niezależnie.</span></div>`;
        });
})();


/* PKG-015g-FIX-03 — guard actual income amount before ConfirmIncome */
(() => {
    const forms = Array.from(document.querySelectorAll('form')).filter(form =>
        String(form.getAttribute('action') || '').toLowerCase().includes('/privatefinance/confirmincome'));

    forms.forEach(form => {
        const amount = Array.from(form.querySelectorAll('input[type="number"]')).find(input =>
            String(input.getAttribute('name') || '').toLowerCase().includes('amount'));
        if (!amount) return;

        amount.min = '0.01';
        if (!amount.step || amount.step === 'any') amount.step = '0.01';
        amount.required = true;

        const validate = event => {
            const value = Number(String(amount.value || '').replace(',', '.'));
            if (Number.isFinite(value) && value > 0) {
                amount.setCustomValidity('');
                return;
            }
            amount.setCustomValidity('Podaj faktyczną kwotę wpływu większą od 0.');
            amount.reportValidity();
            event?.preventDefault();
        };

        amount.addEventListener('input', () => {
            if (Number(String(amount.value || '').replace(',', '.')) > 0) amount.setCustomValidity('');
        });
        form.addEventListener('submit', validate, true);
    });
})();


/* PKG-015g-FIX-03 — friendly server-side income amount validation message */
(() => {
    const params = new URLSearchParams(window.location.search);
    if (params.get('incomeAmountError') !== '1') return;
    const main = document.querySelector('main') || document.querySelector('.main-content');
    if (!main) return;
    const alert = document.createElement('div');
    alert.className = 'alert alert-danger';
    alert.textContent = 'Nie potwierdzono wpływu: faktyczna kwota wynagrodzenia musi być większa od 0.';
    main.prepend(alert);
    params.delete('incomeAmountError');
    const query = params.toString();
    history.replaceState(null, '', `${window.location.pathname}${query ? `?${query}` : ''}${window.location.hash}`);
})();

/* Otwórz kartę konkretnej wpłaty wskazanej z przypomnienia. */
(() => {
    const selected = document.querySelector('.contribution-obligation-card.selected .contribution-submit-details');
    if (selected) {
        selected.open = true;
        window.setTimeout(() => selected.closest('.contribution-obligation-card')?.scrollIntoView({ behavior: 'smooth', block: 'center' }), 120);
    }
})();
