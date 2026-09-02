(() => {
    const path = (window.location.pathname || "").toLowerCase();
    if (path !== "/rental/settlements" && !path.startsWith("/rental/settlements/")) return;

    const endpoint = "/Rental/SettlementCorrections";
    const main = document.querySelector("main, #mainContent") || document.body;
    const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value || "";

    const esc = value => String(value ?? "")
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#39;");

    const money = (minor, currency) => {
        try {
            return new Intl.NumberFormat("pl-PL", {
                style: "currency",
                currency: currency || "PLN"
            }).format((Number(minor) || 0) / 100);
        } catch {
            return `${((Number(minor) || 0) / 100).toFixed(2)} ${currency || "PLN"}`;
        }
    };

    function findCard(item) {
        const expected = `${String(item.tenantName || "").trim()} — ${String(item.periodKey || "").trim()}`.toLowerCase();

        const heading = Array.from(
            main.querySelectorAll(".card-header strong, .card strong")
        ).find(x =>
            String(x.textContent || "").trim().toLowerCase() === expected
        );

        return heading?.closest(".card") || null;
    }

    function ensureButton(card, item) {
        if (!item.lockedForNormalEdit) return;
        if (card.querySelector("[data-settlement-correction-toggle]")) return;

        const header = card.querySelector(".card-header") || card.firstElementChild;
        if (!header) return;

        const button = document.createElement("button");
        button.type = "button";
        button.className = "tenant-correction-toggle";
        button.dataset.settlementCorrectionToggle = item.settlementId;
        button.textContent = "Korekta";

        header.appendChild(button);

        button.addEventListener("click", () => {
            const panel = ensurePanel(card, item);
            panel.hidden = !panel.hidden;
            button.classList.toggle("is-open", !panel.hidden);
        });
    }

    function ensurePanel(card, item) {
        let panel = card.querySelector(
            `[data-settlement-correction-panel="${CSS.escape(String(item.settlementId))}"]`
        );

        if (panel) return panel;

        panel = document.createElement("section");
        panel.className = "tenant-correction-panel";
        panel.dataset.settlementCorrectionPanel = item.settlementId;
        panel.hidden = true;

        panel.innerHTML = `
            <div class="tenant-correction-head">
                <div>
                    <span>Korekta opublikowanego rozliczenia</span>
                    <strong>${esc(item.tenantName)} · ${esc(item.periodKey)}</strong>
                    <small>Status: ${esc(item.status)} · obecna należność ${esc(money(item.totalDueMinor, item.currencyCode))}</small>
                </div>
                <button type="button" data-correction-close>×</button>
            </div>

            <div class="tenant-correction-warning">
                Oryginalne rozliczenie pozostaje bez zmian. Zapis tworzy osobną, jawną korektę.
            </div>

            <form data-settlement-correction-form>
                <input type="hidden" name="settlementId" value="${esc(item.settlementId)}" />

                <div class="tenant-correction-grid">
                    <label>Rodzaj korekty
                        <select name="correctionType" data-correction-type>
                            <option value="Pellet">Pellet / ogrzewanie</option>
                            <option value="Other">Inna korekta</option>
                        </select>
                    </label>

                    <label>Operacja
                        <select name="operation" data-correction-operation>
                            <option value="Add">Dolicz kwotę</option>
                            <option value="Subtract">Odejmij kwotę</option>
                        </select>
                    </label>

                    <label>Kwota
                        <div class="tenant-correction-amount">
                            <input name="amount"
                                   type="number"
                                   min="0.01"
                                   step="0.01"
                                   required
                                   placeholder="0,00" />
                            <span>${esc(item.currencyCode || "PLN")}</span>
                        </div>
                    </label>

                    <label class="tenant-correction-full">Powód / opis
                        <textarea name="reason"
                                  rows="3"
                                  placeholder="np. dodatkowa faktura za pellet otrzymana po publikacji rachunków"></textarea>
                    </label>
                </div>

                <div class="tenant-correction-pellet-note" data-pellet-note>
                    <strong>Pellet:</strong>
                    wpisana kwota zostanie <b>doliczona</b> do tego konkretnego rozliczenia jako korekta.
                    Jeśli koszt pochodzi z nowej faktury za pellet, standardowo dodaj ją w Finansach domowych —
                    wtedy pula i korekty wielu lokatorów utworzą się automatycznie.
                </div>

                <div class="tenant-correction-actions">
                    <button type="button"
                            class="btn btn-secondary btn-sm"
                            data-correction-cancel>
                        Anuluj
                    </button>
                    <button type="submit"
                            class="btn btn-primary btn-sm">
                        Zapisz korektę
                    </button>
                </div>
            </form>`;

        const body = card.querySelector(".card-body") || card;
        body.prepend(panel);

        const type = panel.querySelector("[data-correction-type]");
        const operation = panel.querySelector("[data-correction-operation]");
        const pelletNote = panel.querySelector("[data-pellet-note]");

        const syncType = () => {
            const pellet = type.value === "Pellet";
            operation.disabled = pellet;

            if (pellet) {
                operation.value = "Add";
            }

            pelletNote.hidden = !pellet;
        };

        type.addEventListener("change", syncType);
        syncType();

        const close = () => {
            panel.hidden = true;
            card.querySelector("[data-settlement-correction-toggle]")?.classList.remove("is-open");
        };

        panel.querySelector("[data-correction-close]")?.addEventListener("click", close);
        panel.querySelector("[data-correction-cancel]")?.addEventListener("click", close);

        panel.querySelector("[data-settlement-correction-form]")?.addEventListener(
            "submit",
            async event => {
                event.preventDefault();

                const form = event.currentTarget;
                const submit = form.querySelector('button[type="submit"]');
                const amount = Number.parseFloat(
                    String(form.querySelector('[name="amount"]')?.value || "0").replace(",", ".")
                );

                if (!Number.isFinite(amount) || amount <= 0) {
                    window.alert("Podaj kwotę większą od 0.");
                    return;
                }

                const correctionType = form.querySelector('[name="correctionType"]')?.value || "Other";
                const op = correctionType === "Pellet"
                    ? "doliczyć"
                    : (form.querySelector('[name="operation"]')?.value === "Subtract" ? "odjąć" : "doliczyć");

                if (!window.confirm(
                    `Czy na pewno ${op} ${amount.toFixed(2)} ${item.currencyCode || "PLN"} jako korektę rozliczenia ${item.periodKey}?`
                )) return;

                submit.disabled = true;

                try {
                    const body = new FormData(form);
                    if (token) body.append("__RequestVerificationToken", token);

                    const response = await fetch(`${endpoint}/Create`, {
                        method: "POST",
                        body,
                        credentials: "same-origin"
                    });

                    const result = await response.json().catch(() => ({}));

                    if (!response.ok) {
                        throw new Error(result.message || "Nie udało się utworzyć korekty.");
                    }

                    window.alert(result.message || "Korekta została utworzona.");
                    window.location.reload();
                } catch (error) {
                    window.alert(error.message || "Nie udało się utworzyć korekty.");
                    submit.disabled = false;
                }
            }
        );

        return panel;
    }

    async function load() {
        try {
            const response = await fetch(`${endpoint}/Data`, {
                credentials: "same-origin",
                headers: { "Accept": "application/json" }
            });

            if (!response.ok) return;

            const data = await response.json();
            if (!data.canManage) return;

            (data.settlements || []).forEach(item => {
                const card = findCard(item);
                if (card) ensureButton(card, item);
            });
        } catch (error) {
            console.warn("Nie udało się uruchomić obsługi korekt rozliczeń.", error);
        }
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", load);
    } else {
        load();
    }
})();
