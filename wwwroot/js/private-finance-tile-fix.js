/* PKG-015h-FIX-05 — poprawka aktywacji kafelka „Wydatki” w Moich finansach. */
(() => {
    const path = (window.location.pathname || '').toLowerCase();
    if (!path.includes('/privatefinance')) return;

    const main = document.querySelector('.main-content, main, [role="main"]') || document.body;
    const normalize = value => (value || '').trim().toLowerCase();

    const findContainer = heading => {
        let node = heading;
        for (let i = 0; i < 5 && node; i++, node = node.parentElement) {
            if (!node || node === main || node === document.body) break;
            if (node.matches('section,.panel,.card,details,article')) return node;
        }
        return heading?.parentElement || null;
    };

    // „Świadczenia i wydatki dziecka” zawiera słowo „wydatki”, dlatego bazowy
    // skrypt może przypisać kafelek do niewłaściwej sekcji i pozostawić go wyłączonym.
    // Tutaj wskazujemy jednoznacznie sekcję prywatnych wydatków.
    const headings = Array.from(main.querySelectorAll('h2,h3,h4,summary'));
    const expenseHeading = headings.find(h => normalize(h.textContent) === 'wydatki prywatne')
        || headings.find(h => normalize(h.textContent).startsWith('wydatki prywatne'))
        || headings.find(h => normalize(h.textContent).includes('wydatki prywatne'));

    if (!expenseHeading) return;

    const expenseSection = findContainer(expenseHeading);
    if (!expenseSection) return;

    expenseSection.classList.add('pf-bank-section', 'pf-bank-section-expenses');
    if (!expenseSection.id) expenseSection.id = 'pf-expenses';
    const target = `#${expenseSection.id}`;

    const currentTile = document.querySelector('.pf-bank-tile-expenses');
    if (!currentTile) return;

    if (currentTile.tagName.toLowerCase() === 'a') {
        currentTile.href = target;
        currentTile.classList.remove('is-disabled');
        currentTile.removeAttribute('aria-disabled');
        return;
    }

    const activeTile = document.createElement('a');
    activeTile.className = currentTile.className.replace(/\bis-disabled\b/g, '').replace(/\s{2,}/g, ' ').trim();
    activeTile.href = target;
    activeTile.innerHTML = currentTile.innerHTML;
    activeTile.title = 'Przejdź do wydatków prywatnych';
    activeTile.setAttribute('aria-label', 'Wydatki — przejdź do kosztów i płatności');
    currentTile.replaceWith(activeTile);
})();
