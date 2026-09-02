/* PKG-015h-FIX-07 — jawny odbiorca w historii wysłanych wpłat. */
(() => {
    const path = (window.location.pathname || '').toLowerCase();
    if (!path.includes('/householdfinance')) return;

    const normalize = value => (value || '').trim().toLowerCase();

    const heading = Array.from(document.querySelectorAll('h2,h3')).find(h => {
        const text = normalize(h.textContent);
        return text.includes('moje wysłane wpłaty i status')
            || text.includes('rejestr wszystkich zgłoszeń wpłat');
    });

    if (!heading) return;

    const section = heading.closest('section') || heading.parentElement;
    if (!section) return;

    const table = section.querySelector('table.hf-status-table, table');
    if (!table) return;

    const headRow = table.querySelector('thead tr');
    if (!headRow) return;

    const headers = Array.from(headRow.children);
    if (!headers.length) return;

    // Poprzednia kolumna „Osoba” oznacza nadawcę.
    if (normalize(headers[0].textContent) === 'osoba') {
        headers[0].textContent = 'Od kogo';
    }

    // Nie dodawaj ponownie po kolejnym załadowaniu skryptu.
    if (headRow.querySelector('[data-edom-recipient-column]')) return;

    const recipientHeader = document.createElement('th');
    recipientHeader.textContent = 'Do kogo';
    recipientHeader.setAttribute('data-edom-recipient-column', '1');

    if (headers[0].nextSibling) headRow.insertBefore(recipientHeader, headers[0].nextSibling);
    else headRow.appendChild(recipientHeader);

    table.querySelectorAll('tbody tr').forEach(row => {
        const senderCell = row.children[0];
        if (!senderCell || row.querySelector('[data-edom-recipient-cell]')) return;

        const recipientCell = document.createElement('td');
        recipientCell.setAttribute('data-edom-recipient-cell', '1');

        const name = document.createElement('strong');
        name.textContent = 'Dom';

        const detail = document.createElement('small');
        detail.className = 'muted';
        detail.textContent = 'Wspólny rachunek gospodarstwa';
        detail.style.display = 'block';

        recipientCell.appendChild(name);
        recipientCell.appendChild(detail);

        if (senderCell.nextSibling) row.insertBefore(recipientCell, senderCell.nextSibling);
        else row.appendChild(recipientCell);
    });
})();
