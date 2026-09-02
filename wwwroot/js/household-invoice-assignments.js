(() => {
    const path = (window.location.pathname || "").toLowerCase();
    if (!path.includes("/householdfinance")) return;

    const endpoint = "/HouseholdInvoiceAssignments";
    const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value || "";

    const esc = value => String(value ?? "").replace(/[&<>"']/g, char => ({
        "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#039;"
    })[char]);

    const money = (minor, currency) => new Intl.NumberFormat("pl-PL", {
        style: "currency",
        currency: currency || "PLN"
    }).format((Number(minor) || 0) / 100);

    const date = value => {
        if (!value) return "—";
        const raw = String(value).slice(0, 10);
        const parts = raw.split("-");
        return parts.length === 3 ? `${parts[2]}.${parts[1]}.${parts[0]}` : raw;
    };

    const statusText = status => ({
        Assigned: "Przekazana do opłacenia",
        Submitted: "Opłacenie zgłoszone",
        Cancelled: "Anulowane",
        Reassigned: "Przekazano innej osobie"
    })[status] || status || "—";

    const statusClass = status => ({
        Assigned: "hf-assign-status-warn",
        Submitted: "hf-assign-status-info",
        Cancelled: "hf-assign-status-muted",
        Reassigned: "hf-assign-status-muted"
    })[status] || "hf-assign-status-muted";

    async function readJson(response) {
        const text = await response.text();
        if (!text) return {};
        try { return JSON.parse(text); } catch { return { message: text }; }
    }

    async function post(action, values) {
        const body = new URLSearchParams(values || {});
        if (token) body.set("__RequestVerificationToken", token);
        const response = await fetch(`${endpoint}/${action}`, {
            method: "POST",
            headers: { "Content-Type": "application/x-www-form-urlencoded;charset=UTF-8" },
            body: body.toString(),
            credentials: "same-origin"
        });
        const data = await readJson(response);
        if (!response.ok) throw new Error(data.message || "Nie udało się wykonać operacji.");
        return data;
    }

    function flash(section, message, isError = false) {
        let box = section.querySelector(".hf-assign-message");
        if (!box) {
            box = document.createElement("div");
            box.className = "hf-assign-message";
            section.prepend(box);
        }
        box.classList.toggle("is-error", isError);
        box.textContent = message;
        box.hidden = false;
        window.setTimeout(() => { box.hidden = true; }, 6000);
    }

    function adminSection(data) {
        const section = document.createElement("section");
        section.className = "panel hf-section hf-assignment-section";
        section.id = "hf-invoice-assignments";

        const invoiceOptions = (data.invoices || []).map(i =>
            `<option value="${esc(i.id)}">${esc(i.invoiceNo)} — ${esc(i.supplier)} — ${esc(money(i.amountMinor, i.currencyCode))}</option>`
        ).join("");
        const personOptions = (data.people || []).map(p =>
            `<option value="${esc(p.id)}">${esc(p.name)}</option>`
        ).join("");

        const rows = (data.assignments || []).map(a => `
            <tr>
                <td><strong>${esc(a.invoiceNo)}</strong><small>${esc(a.supplier)}</small></td>
                <td>${esc(a.assigneeName)}</td>
                <td>${esc(money(a.amountMinor, a.currencyCode))}</td>
                <td>${esc(date(a.dueDate))}</td>
                <td><span class="hf-assign-status ${statusClass(a.status)}">${esc(statusText(a.status))}</span></td>
                <td>${esc(a.note || "—")}</td>
                <td>${a.status === "Assigned" ? `<button type="button" class="btn btn-secondary btn-compact" data-cancel-assignment="${esc(a.id)}">Anuluj</button>` : "—"}</td>
            </tr>`).join("");

        section.innerHTML = `
            <div class="hf-section-head">
                <div>
                    <h2>Przekaż fakturę domownikowi do opłacenia</h2>
                    <p class="muted">Wybierz fakturę i osobę. Domownik zobaczy ją u siebie jako zadanie i może zarejestrować opłacenie ze swoich prywatnych środków.</p>
                </div>
                <div class="hf-inline-chip">${(data.assignments || []).filter(x => x.status === "Assigned").length} aktywnych</div>
            </div>
            <div class="hf-assign-message" hidden></div>
            <div class="hf-assignment-admin-grid">
                <article class="hf-assignment-card">
                    <h3>Nowe przekazanie</h3>
                    ${(data.invoices || []).length === 0 ? `<div class="empty-note">Brak otwartych faktur do przekazania.</div>` : `
                    <form class="hf-assignment-form" data-assignment-form>
                        <label>Faktura
                            <select name="invoiceId" required>${invoiceOptions}</select>
                        </label>
                        <label>Osoba odpowiedzialna za opłacenie
                            <select name="personId" required>${personOptions}</select>
                        </label>
                        <label class="hf-assignment-full">Notatka dla domownika
                            <input name="note" maxlength="500" placeholder="np. opłać do piątku z własnego konta" />
                        </label>
                        <button class="btn hf-assignment-full" type="submit">Przekaż fakturę do opłacenia</button>
                    </form>`}
                </article>
                <article class="hf-assignment-card hf-assignment-info-card">
                    <h3>Jak to działa?</h3>
                    <ol>
                        <li>Administrator przekazuje fakturę konkretnej osobie.</li>
                        <li>Osoba widzi fakturę na swoim ekranie „Finanse domowe”.</li>
                        <li>Po faktycznym opłaceniu rejestruje wydatek jako opłacony prywatnie.</li>
                        <li>Administrator rozlicza istniejące zgłoszenie jako zwrot albo kompensatę.</li>
                    </ol>
                    <p>e-dom nie wykonuje przelewu bankowego — zapisuje i rozlicza wykonaną płatność.</p>
                </article>
            </div>
            <div class="hf-assignment-history">
                <h3>Historia przekazań</h3>
                ${(data.assignments || []).length === 0 ? `<div class="empty-note">Nie przekazano jeszcze żadnej faktury.</div>` : `
                <div class="hf-status-table-wrap">
                    <table class="hf-status-table hf-assignment-table">
                        <thead><tr><th>Faktura</th><th>Osoba</th><th>Kwota</th><th>Termin</th><th>Status</th><th>Notatka</th><th>Akcja</th></tr></thead>
                        <tbody>${rows}</tbody>
                    </table>
                </div>`}
            </div>`;

        const form = section.querySelector("[data-assignment-form]");
        form?.addEventListener("submit", async event => {
            event.preventDefault();
            const fd = new FormData(form);
            const button = form.querySelector('button[type="submit"]');
            button.disabled = true;
            try {
                const result = await post("Assign", {
                    invoiceId: fd.get("invoiceId"),
                    personId: fd.get("personId"),
                    note: fd.get("note") || ""
                });
                flash(section, result.message || "Faktura została przekazana.");
                window.setTimeout(() => window.location.reload(), 700);
            } catch (error) {
                flash(section, error.message, true);
                button.disabled = false;
            }
        });

        section.querySelectorAll("[data-cancel-assignment]").forEach(button => {
            button.addEventListener("click", async () => {
                if (!window.confirm("Anulować przekazanie tej faktury?")) return;
                button.disabled = true;
                try {
                    const result = await post("Cancel", { assignmentId: button.dataset.cancelAssignment });
                    flash(section, result.message || "Przekazanie anulowano.");
                    window.setTimeout(() => window.location.reload(), 700);
                } catch (error) {
                    flash(section, error.message, true);
                    button.disabled = false;
                }
            });
        });

        return section;
    }

    function memberSection(data) {
        const section = document.createElement("section");
        section.className = "panel hf-section hf-assignment-section hf-my-assigned-invoices";
        section.id = "hf-my-assigned-invoices";

        const active = (data.assignments || []).filter(x => x.status === "Assigned");
        const history = (data.assignments || []).filter(x => x.status !== "Assigned");

        const cards = active.map(a => `
            <article class="hf-assigned-invoice-card">
                <div class="hf-assigned-invoice-head">
                    <div>
                        <span class="hf-assigned-kicker">Faktura przekazana Tobie</span>
                        <h3>${esc(a.invoiceNo)} — ${esc(a.supplier)}</h3>
                    </div>
                    <span class="hf-assign-status hf-assign-status-warn">Do opłacenia</span>
                </div>
                <div class="hf-assigned-invoice-values">
                    <div><span>Kwota</span><strong>${esc(money(a.amountMinor, a.currencyCode))}</strong></div>
                    <div><span>Termin</span><strong>${esc(date(a.dueDate))}</strong></div>
                    <div><span>Notatka</span><strong>${esc(a.note || "Brak dodatkowej notatki")}</strong></div>
                </div>
                <div class="hf-assigned-invoice-action">
                    <label>Sposób późniejszego rozliczenia z domem
                        <select data-settlement-type="${esc(a.id)}">
                            <option value="Refund">Zwrot środków</option>
                            <option value="Compensation">Kompensata obowiązkowej wpłaty</option>
                        </select>
                    </label>
                    <button class="btn" type="button" data-pay-assignment="${esc(a.id)}">Zarejestruj, że faktura została opłacona</button>
                </div>
                <p class="muted hf-assigned-help">Kliknij dopiero po faktycznym wykonaniu płatności. e-dom nie wykonuje przelewu bankowego.</p>
            </article>`).join("");

        const historyRows = history.slice(0, 20).map(a => `
            <tr>
                <td><strong>${esc(a.invoiceNo)}</strong><small>${esc(a.supplier)}</small></td>
                <td>${esc(money(a.amountMinor, a.currencyCode))}</td>
                <td>${esc(date(a.dueDate))}</td>
                <td><span class="hf-assign-status ${statusClass(a.status)}">${esc(statusText(a.status))}</span></td>
            </tr>`).join("");

        section.innerHTML = `
            <div class="hf-section-head">
                <div>
                    <h2>Faktury przekazane mi do opłacenia</h2>
                    <p class="muted">Tutaj pojawiają się rachunki, które administrator wskazał Tobie do opłacenia ze środków prywatnych.</p>
                </div>
                <div class="hf-inline-chip">${active.length} do opłacenia</div>
            </div>
            <div class="hf-assign-message" hidden></div>
            ${active.length ? `<div class="hf-assigned-invoice-grid">${cards}</div>` : `<div class="empty-note">Nie masz teraz żadnej faktury przekazanej do opłacenia.</div>`}
            ${history.length ? `
                <details class="hf-details-card hf-assignment-history-details">
                    <summary>Historia przekazanych mi faktur (${history.length})</summary>
                    <div class="hf-status-table-wrap">
                        <table class="hf-status-table hf-assignment-table">
                            <thead><tr><th>Faktura</th><th>Kwota</th><th>Termin</th><th>Status</th></tr></thead>
                            <tbody>${historyRows}</tbody>
                        </table>
                    </div>
                </details>` : ""}`;

        section.querySelectorAll("[data-pay-assignment]").forEach(button => {
            button.addEventListener("click", async () => {
                const id = button.dataset.payAssignment;
                const select = section.querySelector(`[data-settlement-type="${CSS.escape(id)}"]`);
                if (!window.confirm("Potwierdzasz, że ta faktura została już faktycznie opłacona z Twoich prywatnych środków?")) return;
                button.disabled = true;
                try {
                    const result = await post("Pay", {
                        assignmentId: id,
                        settlementType: select?.value || "Refund"
                    });
                    flash(section, result.message || "Płatność została zgłoszona.");
                    window.setTimeout(() => window.location.reload(), 900);
                } catch (error) {
                    flash(section, error.message, true);
                    button.disabled = false;
                }
            });
        });

        return section;
    }

    async function init() {
        const invoiceSection = document.querySelector("#hf-invoices");
        if (!invoiceSection || document.querySelector("#hf-invoice-assignments, #hf-my-assigned-invoices")) return;

        try {
            const response = await fetch(`${endpoint}/Data`, { credentials: "same-origin" });
            if (!response.ok) return;
            const data = await response.json();
            const section = data.canManage ? adminSection(data) : memberSection(data);
            if (data.canManage) invoiceSection.insertAdjacentElement("afterend", section);
            else invoiceSection.insertAdjacentElement("beforebegin", section);
        } catch (error) {
            console.warn("Nie udało się wczytać przekazanych faktur.", error);
        }
    }

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
    else init();
})();
