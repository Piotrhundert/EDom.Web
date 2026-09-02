(() => {
    const endpoint = "/Rental/Workspace";
    const token = document.querySelector('#rental-workspace-token input[name="__RequestVerificationToken"]')?.value || "";
    let annexes = [];

    const esc = value => String(value ?? "").replace(/[&<>"']/g, c => ({
        "&":"&amp;", "<":"&lt;", ">":"&gt;", '"':"&quot;", "'":"&#039;"
    })[c]);

    const money = (minor, currency) => new Intl.NumberFormat("pl-PL", {
        style: "currency",
        currency: currency || "PLN"
    }).format((Number(minor) || 0) / 100);

    const date = value => {
        if (!value) return "—";
        const raw = String(value).slice(0, 10);
        const p = raw.split("-");
        return p.length === 3 ? `${p[2]}.${p[1]}.${p[0]}` : raw;
    };

    function closeAllPanels(except = null) {
        document.querySelectorAll(".rental-contract-panel:not([hidden])").forEach(panel => {
            if (panel !== except) panel.hidden = true;
        });
    }

    function openPanel(selector) {
        const panel = document.querySelector(selector);
        if (!panel) return;
        const willOpen = panel.hidden;
        closeAllPanels(panel);
        panel.hidden = !willOpen;
        if (willOpen) panel.scrollIntoView({ behavior: "smooth", block: "nearest" });
    }

    document.querySelectorAll("[data-rental-preview]").forEach(button => {
        button.addEventListener("click", () =>
            openPanel(`[data-rental-preview-panel="${CSS.escape(button.dataset.rentalPreview)}"]`)
        );
    });

    document.querySelectorAll("[data-rental-annex]").forEach(button => {
        button.addEventListener("click", () =>
            openPanel(`[data-rental-annex-panel="${CSS.escape(button.dataset.rentalAnnex)}"]`)
        );
    });

    document.querySelectorAll("[data-rental-actions]").forEach(button => {
        button.addEventListener("click", () =>
            openPanel(`[data-rental-actions-panel="${CSS.escape(button.dataset.rentalActions)}"]`)
        );
    });

    document.querySelectorAll("[data-rental-close]").forEach(button => {
        button.addEventListener("click", () => {
            const panel = button.closest(".rental-contract-panel");
            if (panel) panel.hidden = true;
        });
    });

    function renderAnnexHistory() {
        document.querySelectorAll("[data-rental-annex-history]").forEach(box => {
            const contractId = box.dataset.rentalAnnexHistory;
            const items = annexes
                .filter(x => String(x.contractId) === String(contractId))
                .sort((a, b) => Number(b.annexNumber) - Number(a.annexNumber));

            if (!items.length) {
                box.innerHTML = `
                    <div class="rental-annex-history-empty">
                        Brak aneksów utworzonych z nowego widoku.
                    </div>`;
                return;
            }

            box.innerHTML = `
                <div class="rental-annex-history-title">
                    <strong>Historia aneksów</strong>
                    <span>${items.length}</span>
                </div>
                <div class="rental-annex-list">
                    ${items.map(a => `
                        <article>
                            <div>
                                <strong>Aneks nr ${esc(a.annexNumber)}</strong>
                                <small>od ${esc(date(a.effectiveOn))}</small>
                            </div>
                            <div class="rental-annex-changes">
                                ${a.newRentAmountMinor != null
                                    ? `<span>Czynsz: ${esc(money(a.oldRentAmountMinor, a.currencyCode))} → ${esc(money(a.newRentAmountMinor, a.currencyCode))}</span>`
                                    : ""}
                                ${a.newLeaseTo
                                    ? `<span>Umowa do: ${esc(date(a.newLeaseTo))}</span>`
                                    : ""}
                                ${a.clauseTitle
                                    ? `<span>Nowe postanowienie: ${esc(a.clauseTitle)}</span>`
                                    : ""}
                            </div>
                            <a href="${endpoint}/Annex/${encodeURIComponent(a.id)}" target="_blank">Podgląd aneksu</a>
                        </article>`).join("")}
                </div>`;
        });
    }

    async function loadData() {
        try {
            const response = await fetch(`${endpoint}/Data`, { credentials: "same-origin" });
            if (!response.ok) return;
            const data = await response.json();
            annexes = data.annexes || [];
            renderAnnexHistory();
        } catch (error) {
            console.warn("Nie udało się wczytać historii aneksów.", error);
        }
    }

    async function submitAnnex(form) {
        const button = form.querySelector('button[type="submit"]');
        button.disabled = true;

        try {
            const body = new FormData(form);
            if (token) body.append("__RequestVerificationToken", token);

            const response = await fetch(`${endpoint}/CreateAnnex`, {
                method: "POST",
                body,
                credentials: "same-origin"
            });

            const result = await response.json().catch(() => ({}));

            if (!response.ok) {
                throw new Error(result.message || "Nie udało się utworzyć aneksu.");
            }

            window.alert(result.message || "Aneks został utworzony.");
            window.location.reload();
        } catch (error) {
            window.alert(error.message || "Nie udało się utworzyć aneksu.");
            button.disabled = false;
        }
    }

    document.querySelectorAll("[data-rental-annex-form]").forEach(form => {
        form.addEventListener("submit", event => {
            event.preventDefault();

            if (!window.confirm(
                "Utworzyć aneks do tej umowy? Pierwotna umowa nie zostanie nadpisana."
            )) return;

            submitAnnex(form);
        });
    });

    loadData();
})();
