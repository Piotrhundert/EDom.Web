(() => {
    const path = (window.location.pathname || "").toLowerCase();
    if (path !== "/rental/settlements" && !path.startsWith("/rental/settlements/")) return;

    const endpoint = "/Rental/SettlementCorrections";
    const main = document.querySelector("main, #mainContent") || document.body;

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

    const date = value => {
        if (!value) return "—";
        const raw = String(value).slice(0, 10);
        const p = raw.split("-");
        return p.length === 3 ? `${p[2]}.${p[1]}.${p[0]}` : raw;
    };

    const localDateTimeValue = () => {
        const now = new Date();
        const local = new Date(now.getTime() - now.getTimezoneOffset() * 60000);
        return local.toISOString().slice(0, 16);
    };

    const defaultDueDate = () => {
        const value = new Date();
        value.setDate(value.getDate() + 14);
        const local = new Date(value.getTime() - value.getTimezoneOffset() * 60000);
        return local.toISOString().slice(0, 10);
    };

    function findCard(item) {
        const expected =
            `${String(item.tenantName || "").trim()} — ${String(item.periodKey || "").trim()}`.toLowerCase();

        const heading = Array.from(
            main.querySelectorAll(".card-header strong, .card strong")
        ).find(x =>
            String(x.textContent || "").trim().toLowerCase() === expected
        );

        return heading?.closest(".card") || null;
    }

    function correctionTypeLabel(type) {
        return type === "Pellet"
            ? "Pellet / ogrzewanie"
            : "Korekta";
    }

    function getPositiveCorrectionTotal(item) {
        const stored = (item.corrections || [])
            .reduce((sum, c) => sum + Math.max(0, Number(c.deltaMinor || 0)), 0);

        if (stored > 0) return stored;

        return Math.max(0, Number(item.correctionLineMinor || 0));
    }

    function renderCorrectionSummary(card, item, data) {
        const stored = Array.isArray(item.corrections)
            ? item.corrections
            : [];

        const fallbackCorrectionMinor = Number(item.correctionLineMinor || 0);

        if (!stored.length && fallbackCorrectionMinor === 0) {
            return;
        }

        let host = card.querySelector("[data-correction-summary]");
        if (!host) {
            host = document.createElement("section");
            host.dataset.correctionSummary = "true";
            host.className = "tenant-correction-summary";

            const body = card.querySelector(".card-body") || card;
            body.prepend(host);
        }

        const remaining = Math.max(0, Number(item.remainingMinor || 0));

        let entries = stored.map(c => ({
            type: c.correctionType,
            deltaMinor: Number(c.deltaMinor || 0),
            dueDate: c.dueDate,
            reason: c.reason,
            createdAtUtc: c.createdAtUtc
        }));

        // Obsługa korekt utworzonych wcześniej, zanim dodaliśmy
        // osobną historię UI.
        if (!entries.length && fallbackCorrectionMinor !== 0) {
            entries = [{
                type: "Other",
                deltaMinor: fallbackCorrectionMinor,
                dueDate: item.dueDate,
                reason: "Korekta istniejąca w rozliczeniu",
                createdAtUtc: null
            }];
        }

        const positiveTotal = entries
            .reduce((sum, c) => sum + Math.max(0, c.deltaMinor), 0);

        const currentOutstanding = Math.min(
            Math.max(0, positiveTotal),
            remaining
        );

        host.innerHTML = `
            <div class="tenant-correction-summary-head">
                <div>
                    <span>Korekta rozliczenia</span>
                    <strong>
                        ${currentOutstanding > 0
                            ? `Do zapłaty: ${esc(money(currentOutstanding, item.currencyCode))}`
                            : "Korekta rozliczona"}
                    </strong>
                </div>
                <b class="${currentOutstanding > 0 ? "is-due" : "is-paid"}">
                    ${currentOutstanding > 0 ? "Do zapłaty" : "Opłacona / rozliczona"}
                </b>
            </div>

            <div class="tenant-correction-summary-list">
                ${entries.map(c => `
                    <article>
                        <div>
                            <strong>${esc(correctionTypeLabel(c.type))}</strong>
                            <small>${c.deltaMinor >= 0 ? "+" : "-"}${esc(money(Math.abs(c.deltaMinor), item.currencyCode))}</small>
                        </div>
                        <div>
                            <span>Termin płatności</span>
                            <strong>${esc(date(c.dueDate || item.dueDate))}</strong>
                        </div>
                        <div>
                            <span>Powód</span>
                            <strong>${esc(c.reason || "—")}</strong>
                        </div>
                    </article>
                `).join("")}
            </div>

            ${data.isTenant && currentOutstanding > 0
                ? `
                    <div class="tenant-correction-payment">
                        <div class="tenant-correction-payment-head">
                            <strong>Opłać korektę</strong>
                            <small>
                                Po zgłoszeniu administrator zobaczy wpłatę w standardowej sekcji „Zgłoszenia wpłat”.
                            </small>
                        </div>

                        <form data-correction-payment-form>
                            <input type="hidden"
                                   name="settlementId"
                                   value="${esc(item.settlementId)}" />
                            <input type="hidden"
                                   name="currencyCode"
                                   value="${esc(item.currencyCode || "PLN")}" />

                            <label>Kwota
                                <input name="amount"
                                       type="number"
                                       min="0.01"
                                       step="0.01"
                                       value="${(currentOutstanding / 100).toFixed(2)}"
                                       required />
                            </label>

                            <label>Data i godzina wpłaty
                                <input name="declaredPaidAtLocal"
                                       type="datetime-local"
                                       value="${localDateTimeValue()}"
                                       required />
                            </label>

                            <label>Forma
                                <select name="paymentMethod">
                                    <option value="Bank">Przelew</option>
                                    <option value="Cash">Gotówka</option>
                                </select>
                            </label>

                            <button type="submit"
                                    class="btn btn-primary btn-sm">
                                Zgłoś wpłatę korekty
                            </button>
                        </form>
                    </div>`
                : ""}
        `;

        const paymentForm = host.querySelector("[data-correction-payment-form]");

        paymentForm?.addEventListener("submit", async event => {
            event.preventDefault();

            const form = event.currentTarget;
            const submit = form.querySelector('button[type="submit"]');
            submit.disabled = true;

            try {
                const body = new FormData(form);

                if (data.requestToken) {
                    body.append(
                        "__RequestVerificationToken",
                        data.requestToken
                    );
                }

                const response = await fetch(
                    `${endpoint}/Payment/Submit`,
                    {
                        method: "POST",
                        body,
                        credentials: "same-origin"
                    }
                );

                const result = await response.json().catch(() => ({}));

                if (!response.ok) {
                    throw new Error(
                        result.message ||
                        "Nie udało się zgłosić wpłaty korekty."
                    );
                }

                window.alert(
                    result.message ||
                    "Wpłata korekty została zgłoszona."
                );

                window.location.reload();
            } catch (error) {
                window.alert(
                    error.message ||
                    "Nie udało się zgłosić wpłaty korekty."
                );

                submit.disabled = false;
            }
        });
    }

    function ensureCorrectionButton(card, item, data) {
        if (!data.canManage || !item.lockedForNormalEdit) {
            return;
        }

        if (card.querySelector("[data-settlement-correction-toggle]")) {
            return;
        }

        const header = card.querySelector(".card-header") ||
                       card.firstElementChild;

        if (!header) return;

        const button = document.createElement("button");
        button.type = "button";
        button.className = "tenant-correction-toggle";
        button.dataset.settlementCorrectionToggle = item.settlementId;
        button.textContent = "Korekta";

        header.appendChild(button);

        button.addEventListener("click", () => {
            const panel = ensureCorrectionPanel(
                card,
                item,
                data
            );

            panel.hidden = !panel.hidden;

            button.classList.toggle(
                "is-open",
                !panel.hidden
            );
        });
    }

    function ensureCorrectionPanel(card, item, data) {
        let panel = card.querySelector(
            `[data-settlement-correction-panel="${CSS.escape(String(item.settlementId))}"]`
        );

        if (panel) return panel;

        panel = document.createElement("section");
        panel.className = "tenant-correction-panel";
        panel.dataset.settlementCorrectionPanel =
            item.settlementId;
        panel.hidden = true;

        const pelletMajor =
            Math.max(0, Number(item.pelletAmountMinor || 0)) / 100;

        panel.innerHTML = `
            <div class="tenant-correction-head">
                <div>
                    <span>Korekta opublikowanego rozliczenia</span>
                    <strong>${esc(item.tenantName)} · ${esc(item.periodKey)}</strong>
                    <small>
                        Status: ${esc(item.status)} ·
                        obecna należność ${esc(money(item.totalDueMinor, item.currencyCode))}
                    </small>
                </div>
                <button type="button" data-correction-close>×</button>
            </div>

            <div class="tenant-correction-warning">
                Oryginalne rozliczenie pozostaje w historii.
                Dodatnia korekta tworzy nową kwotę do zapłaty z własnym terminem.
            </div>

            <form data-settlement-correction-form>
                <input type="hidden"
                       name="settlementId"
                       value="${esc(item.settlementId)}" />

                <div class="tenant-correction-grid">
                    <label>Rodzaj korekty
                        <select name="correctionType"
                                data-correction-type>
                            <option value="Pellet">Pellet / ogrzewanie</option>
                            <option value="Other">Inna korekta</option>
                        </select>
                    </label>

                    <label>Operacja
                        <select name="operation"
                                data-correction-operation>
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
                                   data-correction-amount
                                   value="${pelletMajor > 0 ? pelletMajor.toFixed(2) : ""}"
                                   placeholder="0,00"
                                   required />
                            <span>${esc(item.currencyCode || "PLN")}</span>
                        </div>
                        <small data-pellet-current-amount>
                            ${pelletMajor > 0
                                ? `Aktualna pozycja pelletu w rozliczeniu: ${esc(money(item.pelletAmountMinor, item.currencyCode))}`
                                : "Brak wcześniejszej pozycji pelletu w tym rozliczeniu."}
                        </small>
                    </label>

                    <label data-correction-due-wrap>
                        Termin płatności korekty
                        <input name="dueDate"
                               type="date"
                               value="${defaultDueDate()}"
                               required />
                    </label>

                    <label class="tenant-correction-full">
                        Powód / opis
                        <textarea name="reason"
                                  rows="3"
                                  placeholder="np. dodatkowa faktura za pellet otrzymana po publikacji rachunku"></textarea>
                    </label>
                </div>

                <div class="tenant-correction-pellet-note"
                     data-pellet-note>
                    <strong>Pellet:</strong>
                    jeśli w tym rozliczeniu istnieje już pozycja pelletu,
                    jej kwota została automatycznie wpisana powyżej.
                    Możesz ją zmienić przed zapisaniem korekty.
                </div>

                <div class="tenant-correction-actions">
                    <button type="button"
                            class="btn btn-secondary btn-sm"
                            data-correction-cancel>
                        Anuluj
                    </button>

                    <button type="submit"
                            class="btn btn-primary btn-sm">
                        Zapisz korektę i termin
                    </button>
                </div>
            </form>
        `;

        const body = card.querySelector(".card-body") || card;
        body.prepend(panel);

        const type =
            panel.querySelector("[data-correction-type]");

        const operation =
            panel.querySelector("[data-correction-operation]");

        const amount =
            panel.querySelector("[data-correction-amount]");

        const dueWrap =
            panel.querySelector("[data-correction-due-wrap]");

        const dueInput =
            dueWrap?.querySelector('input[name="dueDate"]');

        const pelletNote =
            panel.querySelector("[data-pellet-note]");

        const syncType = () => {
            const pellet = type.value === "Pellet";

            if (pellet) {
                operation.value = "Add";
                operation.disabled = true;

                if (pelletMajor > 0) {
                    amount.value = pelletMajor.toFixed(2);
                }
            } else {
                operation.disabled = false;
            }

            pelletNote.hidden = !pellet;
            syncOperation();
        };

        const syncOperation = () => {
            const isAdd =
                type.value === "Pellet" ||
                operation.value !== "Subtract";

            dueWrap.hidden = !isAdd;

            if (dueInput) {
                dueInput.required = isAdd;
            }
        };

        type.addEventListener(
            "change",
            syncType
        );

        operation.addEventListener(
            "change",
            syncOperation
        );

        syncType();

        const close = () => {
            panel.hidden = true;

            card.querySelector(
                "[data-settlement-correction-toggle]"
            )?.classList.remove("is-open");
        };

        panel.querySelector(
            "[data-correction-close]"
        )?.addEventListener("click", close);

        panel.querySelector(
            "[data-correction-cancel]"
        )?.addEventListener("click", close);

        panel.querySelector(
            "[data-settlement-correction-form]"
        )?.addEventListener(
            "submit",
            async event => {
                event.preventDefault();

                const form = event.currentTarget;
                const submit =
                    form.querySelector('button[type="submit"]');

                const rawAmount =
                    form.querySelector('[name="amount"]')?.value ||
                    "0";

                const amountValue =
                    Number.parseFloat(
                        String(rawAmount).replace(",", ".")
                    );

                if (!Number.isFinite(amountValue) ||
                    amountValue <= 0) {
                    window.alert(
                        "Podaj kwotę większą od 0."
                    );
                    return;
                }

                const correctionType =
                    form.querySelector(
                        '[name="correctionType"]'
                    )?.value || "Other";

                const op =
                    correctionType === "Pellet"
                        ? "doliczyć"
                        : (
                            form.querySelector(
                                '[name="operation"]'
                            )?.value === "Subtract"
                                ? "odjąć"
                                : "doliczyć"
                        );

                const dueDate =
                    form.querySelector(
                        '[name="dueDate"]'
                    )?.value || "";

                if (op === "doliczyć" && !dueDate) {
                    window.alert(
                        "Podaj termin płatności korekty."
                    );
                    return;
                }

                if (!window.confirm(
                    `Czy na pewno ${op} ${amountValue.toFixed(2)} ${item.currencyCode || "PLN"} jako korektę rozliczenia ${item.periodKey}?`
                )) {
                    return;
                }

                submit.disabled = true;

                try {
                    const body = new FormData(form);

                    if (data.requestToken) {
                        body.append(
                            "__RequestVerificationToken",
                            data.requestToken
                        );
                    }

                    const response = await fetch(
                        `${endpoint}/Create`,
                        {
                            method: "POST",
                            body,
                            credentials: "same-origin"
                        }
                    );

                    const result =
                        await response.json()
                            .catch(() => ({}));

                    if (!response.ok) {
                        throw new Error(
                            result.message ||
                            "Nie udało się utworzyć korekty."
                        );
                    }

                    window.alert(
                        result.message ||
                        "Korekta została utworzona."
                    );

                    window.location.reload();
                } catch (error) {
                    window.alert(
                        error.message ||
                        "Nie udało się utworzyć korekty."
                    );

                    submit.disabled = false;
                }
            }
        );

        return panel;
    }

    async function load() {
        try {
            const response = await fetch(
                `${endpoint}/Data`,
                {
                    credentials: "same-origin",
                    headers: {
                        "Accept": "application/json"
                    }
                }
            );

            if (!response.ok) return;

            const data = await response.json();

            (data.settlements || []).forEach(item => {
                const card = findCard(item);

                if (!card) return;

                renderCorrectionSummary(
                    card,
                    item,
                    data
                );

                ensureCorrectionButton(
                    card,
                    item,
                    data
                );
            });
        } catch (error) {
            console.warn(
                "Nie udało się uruchomić obsługi korekt rozliczeń.",
                error
            );
        }
    }

    if (document.readyState === "loading") {
        document.addEventListener(
            "DOMContentLoaded",
            load
        );
    } else {
        load();
    }
})();


;(() => {
    const path = (window.location.pathname || "").toLowerCase();

    if (path !== "/rental/settlements"
        && !path.startsWith("/rental/settlements/")) {
        return;
    }

    const endpoint = "/Rental/SettlementRollbacks";
    const main =
        document.querySelector("main, #mainContent")
        || document.body;

    const rollbackMoney = (minor, currency) => {
        try {
            return new Intl.NumberFormat("pl-PL", {
                style: "currency",
                currency: currency || "PLN"
            }).format((Number(minor) || 0) / 100);
        } catch {
            return `${((Number(minor) || 0) / 100).toFixed(2)} ${currency || "PLN"}`;
        }
    };

    const esc = value => String(value ?? "")
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#39;");

    const dateTime = value => {
        if (!value) return "—";

        try {
            return new Intl.DateTimeFormat(
                "pl-PL",
                {
                    dateStyle: "short",
                    timeStyle: "short"
                }
            ).format(new Date(value));
        } catch {
            return String(value);
        }
    };

    function findCard(item) {
        const expected =
            `${String(item.tenantName || "").trim()} — ${String(item.periodKey || "").trim()}`
                .toLowerCase();

        const heading = Array.from(
            main.querySelectorAll(
                ".card-header strong, .card strong"
            )
        ).find(x =>
            String(x.textContent || "")
                .trim()
                .toLowerCase() === expected
        );

        return heading?.closest(".card") || null;
    }

    function renderTenantNotices(data) {
        const active = (data.history || [])
            .filter(x => x.activeRollback);

        if (!active.length) {
            return;
        }

        let host = document.getElementById(
            "tenantSettlementRollbackNotices"
        );

        if (!host) {
            host = document.createElement("section");
            host.id = "tenantSettlementRollbackNotices";
            host.className =
                "tenant-settlement-rollback-notices";

            const firstCard =
                main.querySelector(".card");

            if (firstCard) {
                firstCard.insertAdjacentElement(
                    "beforebegin",
                    host
                );
            } else {
                main.prepend(host);
            }
        }

        host.innerHTML = `
            <div class="tenant-rollback-notice-title">
                <strong>Rozliczenie cofnięte do ponownego przeliczenia</strong>
                <span>
                    Nie opłacaj poniższych rachunków do czasu ponownej publikacji przez administratora.
                </span>
            </div>

            ${active.map(item => `
                <article>
                    <div>
                        <strong>${esc(item.periodKey)} · ${esc(item.roomName)}</strong>
                        <small>
                            Cofnięto: ${esc(dateTime(item.reopenedAtUtc))}
                            · wcześniej: ${esc(item.previousStatus)}
                        </small>
                    </div>

                    <div>
                        <span>Powód</span>
                        <strong>${esc(item.reason)}</strong>
                        ${item.keptApprovedPayments && Number(item.paidMinorAtRollback || 0) > 0
                            ? `<small>Wpłata ${esc(rollbackMoney(item.paidMinorAtRollback, "PLN"))} pozostaje zaksięgowana — nie wpłacaj jej ponownie.</small>`
                            : ""}
                    </div>

                    <b>Oczekuje na nowe rozliczenie</b>
                </article>
            `).join("")}
        `;
    }

    function renderHistoryOnCard(
        card,
        item,
        history
    ) {
        const records = (history || [])
            .filter(x =>
                String(x.settlementId) ===
                String(item.settlementId)
            );

        if (!records.length) {
            return;
        }

        let host = card.querySelector(
            "[data-settlement-rollback-history]"
        );

        if (!host) {
            host = document.createElement("div");
            host.dataset.settlementRollbackHistory = "true";
            host.className =
                "tenant-settlement-rollback-history";

            const body =
                card.querySelector(".card-body")
                || card;

            body.prepend(host);
        }

        host.innerHTML = records.map(record => `
            <div class="${record.activeRollback ? "is-active" : "is-closed"}">
                <div>
                    <strong>
                        ${record.activeRollback
                            ? "Rozliczenie zostało cofnięte"
                            : "Historia cofnięcia rozliczenia"}
                    </strong>

                    <small>
                        ${esc(dateTime(record.reopenedAtUtc))}
                        · ${esc(record.previousStatus)}
                        → ${esc(record.currentStatus || record.reopenedStatus)}
                    </small>
                </div>

                <span>
                    ${esc(record.reason)}
                    ${record.keptApprovedPayments && Number(record.paidMinorAtRollback || 0) > 0
                        ? `<small>Zachowano wpłatę: ${esc(rollbackMoney(record.paidMinorAtRollback, "PLN"))}</small>`
                        : ""}
                </span>
            </div>
        `).join("");
    }

    function addRollbackButton(
        card,
        item,
        data
    ) {
        if (!data.canManage) {
            return;
        }

        if (card.querySelector(
                "[data-settlement-rollback-toggle]")) {
            return;
        }

        const header =
            card.querySelector(".card-header")
            || card.firstElementChild;

        if (!header) {
            return;
        }

        const button = document.createElement(
            "button"
        );

        button.type = "button";
        button.className =
            "tenant-settlement-rollback-toggle";
        button.dataset.settlementRollbackToggle =
            item.settlementId;
        button.textContent = "Cofnij";

        if (!item.canRollback) {
            button.disabled = true;
            button.title =
                item.rollbackBlockedReason
                || "Tego rozliczenia nie można cofnąć.";
        }

        header.appendChild(button);

        if (!item.canRollback) {
            return;
        }

        button.addEventListener(
            "click",
            () => {
                const panel = ensurePanel(
                    card,
                    item,
                    data
                );

                panel.hidden = !panel.hidden;

                button.classList.toggle(
                    "is-open",
                    !panel.hidden
                );
            }
        );
    }

    function ensurePanel(
        card,
        item,
        data
    ) {
        let panel = card.querySelector(
            `[data-settlement-rollback-panel="${CSS.escape(String(item.settlementId))}"]`
        );

        if (panel) {
            return panel;
        }

        panel = document.createElement(
            "section"
        );

        panel.className =
            "tenant-settlement-rollback-panel";
        panel.dataset.settlementRollbackPanel =
            item.settlementId;
        panel.hidden = true;

        panel.innerHTML = `
            <div class="tenant-settlement-rollback-head">
                <div>
                    <span>Cofnięcie miesięcznego rozliczenia</span>
                    <strong>
                        ${esc(item.tenantName)}
                        · ${esc(item.periodKey)}
                    </strong>
                    <small>
                        Status: ${esc(item.status)}
                        ${item.requiresKeepPayment
                            ? ` · zaksięgowano ${esc(rollbackMoney(item.approvedPaymentMinor, item.currencyCode))}`
                            : " · brak zaksięgowanej wpłaty"}
                    </small>
                </div>

                <button type="button"
                        data-rollback-close>
                    ×
                </button>
            </div>

            <div class="tenant-settlement-rollback-warning">
                Cofnięcie nie usuwa historii.
                Rozliczenie wróci do wersji roboczej i będzie wymagało ponownego
                przeliczenia, zatwierdzenia oraz publikacji.
                ${item.requiresKeepPayment
                    ? `Wpłata ${esc(rollbackMoney(item.approvedPaymentMinor, item.currencyCode))} pozostanie zaksięgowana. Po ponownym przeliczeniu pomniejszy nową należność; jeśli nowa kwota będzie wyższa, lokator dopłaci tylko różnicę.`
                    : "Lokator zobaczy informację, że stary rachunek został wycofany."}
            </div>

            <form data-settlement-rollback-form>
                <input type="hidden"
                       name="settlementId"
                       value="${esc(item.settlementId)}" />

                <label>Powód cofnięcia
                    <textarea name="reason"
                              rows="3"
                              required
                              minlength="5"
                              placeholder="np. brakująca faktura za pellet, błędna liczba lokatorów, błędna pozycja rozliczenia"></textarea>
                </label>

                ${item.requiresKeepPayment
                    ? `
                        <label class="tenant-settlement-rollback-payment-keep">
                            <input type="checkbox"
                                   name="keepApprovedPayments"
                                   value="true"
                                   required />
                            <span>
                                <strong>Zachowaj zaksięgowaną wpłatę ${esc(rollbackMoney(item.approvedPaymentMinor, item.currencyCode))}</strong>
                                <small>Pieniądze pozostają przypisane. Po ponownej publikacji system uwzględni je w nowej należności.</small>
                            </span>
                        </label>`
                    : `<input type="hidden" name="keepApprovedPayments" value="false" />`}

                <label class="tenant-settlement-rollback-confirm">
                    <input type="checkbox"
                           required
                           data-rollback-confirm />
                    <span>
                        ${item.requiresKeepPayment
                            ? "Potwierdzam cofnięcie opłaconego rozliczenia bez usuwania otrzymanej wpłaty."
                            : "Potwierdzam cofnięcie rozliczenia do ponownego przygotowania."}
                    </span>
                </label>

                <div class="tenant-settlement-rollback-actions">
                    <button type="button"
                            class="btn btn-secondary btn-sm"
                            data-rollback-cancel>
                        Anuluj
                    </button>

                    <button type="submit"
                            class="btn btn-danger btn-sm">
                        Cofnij rozliczenie
                    </button>
                </div>
            </form>
        `;

        const body =
            card.querySelector(".card-body")
            || card;

        body.prepend(panel);

        const close = () => {
            panel.hidden = true;

            card.querySelector(
                "[data-settlement-rollback-toggle]"
            )?.classList.remove("is-open");
        };

        panel.querySelector(
            "[data-rollback-close]"
        )?.addEventListener(
            "click",
            close
        );

        panel.querySelector(
            "[data-rollback-cancel]"
        )?.addEventListener(
            "click",
            close
        );

        panel.querySelector(
            "[data-settlement-rollback-form]"
        )?.addEventListener(
            "submit",
            async event => {
                event.preventDefault();

                const form =
                    event.currentTarget;

                const reason =
                    form.querySelector(
                        '[name="reason"]'
                    )?.value?.trim()
                    || "";

                if (reason.length < 5) {
                    window.alert(
                        "Podaj konkretny powód cofnięcia rozliczenia."
                    );
                    return;
                }

                if (!window.confirm(
                    item.requiresKeepPayment
                        ? `Cofnąć opłacone rozliczenie ${item.periodKey} dla ${item.tenantName}? Wpłata ${rollbackMoney(item.approvedPaymentMinor, item.currencyCode)} pozostanie zaksięgowana.`
                        : `Cofnąć rozliczenie ${item.periodKey} dla ${item.tenantName}? Lokator otrzyma informację, że rachunek został wycofany.`
                )) {
                    return;
                }

                const submit =
                    form.querySelector(
                        'button[type="submit"]'
                    );

                submit.disabled = true;

                try {
                    const body =
                        new FormData(form);

                    if (data.requestToken) {
                        body.append(
                            "__RequestVerificationToken",
                            data.requestToken
                        );
                    }

                    const response =
                        await fetch(
                            `${endpoint}/Reopen`,
                            {
                                method: "POST",
                                body,
                                credentials:
                                    "same-origin"
                            }
                        );

                    const result =
                        await response.json()
                            .catch(() => ({}));

                    if (!response.ok) {
                        throw new Error(
                            result.message
                            || "Nie udało się cofnąć rozliczenia."
                        );
                    }

                    window.alert(
                        result.message
                        || "Rozliczenie zostało cofnięte."
                    );

                    window.location.reload();
                } catch (error) {
                    window.alert(
                        error.message
                        || "Nie udało się cofnąć rozliczenia."
                    );

                    submit.disabled = false;
                }
            }
        );

        return panel;
    }

    function hidePaymentActionsForActiveRollback(
        card
    ) {
        card.querySelectorAll("form").forEach(form => {
            const action = String(
                form.getAttribute("action")
                || ""
            ).toLowerCase();

            if (action.includes(
                    "/payment/submit")) {
                form.hidden = true;
            }
        });

        card.querySelectorAll(
            "[data-correction-payment-form]"
        ).forEach(form => {
            form.hidden = true;
        });
    }

    async function load() {
        try {
            const response = await fetch(
                `${endpoint}/Data`,
                {
                    credentials: "same-origin",
                    headers: {
                        "Accept":
                            "application/json"
                    }
                }
            );

            if (!response.ok) {
                return;
            }

            const data =
                await response.json();

            if (data.isTenant) {
                renderTenantNotices(data);
            }

            (data.settlements || [])
                .forEach(item => {
                    const card =
                        findCard(item);

                    if (!card) {
                        return;
                    }

                    renderHistoryOnCard(
                        card,
                        item,
                        data.history
                    );

                    addRollbackButton(
                        card,
                        item,
                        data
                    );

                    const active =
                        (data.history || [])
                            .some(x =>
                                String(
                                    x.settlementId
                                ) ===
                                String(
                                    item.settlementId
                                )
                                && x.activeRollback
                            );

                    if (active) {
                        hidePaymentActionsForActiveRollback(
                            card
                        );
                    }
                });
        } catch (error) {
            console.warn(
                "Nie udało się uruchomić obsługi cofnięcia rozliczeń.",
                error
            );
        }
    }

    if (document.readyState === "loading") {
        document.addEventListener(
            "DOMContentLoaded",
            load
        );
    } else {
        load();
    }
})();
