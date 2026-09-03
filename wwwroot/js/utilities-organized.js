(() => {
    const root = document;
    const tabButtons = Array.from(root.querySelectorAll("[data-utilities-tab]"));
    const panels = Array.from(root.querySelectorAll("[data-utilities-panel]"));

    if (!tabButtons.length || !panels.length) {
        return;
    }

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

    const dateTime = value => {
        if (!value) return "—";

        try {
            return new Intl.DateTimeFormat("pl-PL", {
                dateStyle: "short",
                timeStyle: "short"
            }).format(new Date(value));
        } catch {
            return String(value);
        }
    };

    const date = value => {
        if (!value) return "bezterminowo";
        const parts = String(value).slice(0, 10).split("-");
        return parts.length === 3
            ? `${parts[2]}.${parts[1]}.${parts[0]}`
            : String(value);
    };

    const mediumLabel = value => ({
        Electricity: "Prąd",
        Water: "Woda",
        Gas: "Gaz",
        Waste: "Odpady",
        Heating: "Ogrzewanie"
    }[value] || value || "Inne");

    const billingLabel = value => ({
        Monthly: "Miesięczny",
        BiMonthly: "Co 2 miesiące",
        Quarterly: "Kwartalny",
        Annual: "Roczny"
    }[value] || value || "—");

    function openTab(name) {
        const exists = panels.some(panel =>
            panel.dataset.utilitiesPanel === name
        );

        const target = exists ? name : "readings";

        panels.forEach(panel => {
            panel.hidden =
                panel.dataset.utilitiesPanel !== target;
        });

        tabButtons.forEach(button => {
            button.classList.toggle(
                "is-active",
                button.dataset.utilitiesTab === target
            );
        });

        sessionStorage.setItem(
            "edom.utilities.activeTab",
            target
        );
    }

    tabButtons.forEach(button => {
        button.addEventListener("click", () => {
            openTab(button.dataset.utilitiesTab || "readings");
        });
    });

    openTab(
        sessionStorage.getItem("edom.utilities.activeTab")
        || "readings"
    );

    // Szybki odczyt.
    const meterSelect = root.querySelector("[data-reading-meter]");
    const unit = root.querySelector("[data-reading-unit]");
    const last = root.querySelector("[data-reading-last]");

    function syncReadingMeter() {
        const option =
            meterSelect?.selectedOptions?.[0];

        if (!option) return;

        if (unit) {
            unit.textContent =
                option.dataset.unit || "";
        }

        if (last) {
            last.textContent =
                `Ostatni zatwierdzony: ${option.dataset.latest || "brak"}`;
        }
    }

    meterSelect?.addEventListener(
        "change",
        syncReadingMeter
    );

    syncReadingMeter();

    // Dodawanie umowy.
    const createToggle =
        root.querySelector("[data-contract-create-toggle]");

    const createPanel =
        root.querySelector("[data-contract-create-panel]");

    createToggle?.addEventListener("click", () => {
        if (!createPanel) return;

        createPanel.hidden = !createPanel.hidden;

        if (!createPanel.hidden) {
            createPanel.scrollIntoView({
                behavior: "smooth",
                block: "nearest"
            });
        }
    });

    // Podgląd / edycja umów.
    const workspace =
        root.querySelector("[data-contract-workspace]");

    const loading =
        root.querySelector("[data-contract-loading]");

    const content =
        root.querySelector("[data-contract-content]");

    const details =
        root.querySelector("[data-contract-details]");

    const historyHost =
        root.querySelector("[data-contract-history]");

    const editForm =
        root.querySelector("[data-contract-edit-form]");

    const title =
        root.querySelector("[data-contract-workspace-title]");

    const subtitle =
        root.querySelector("[data-contract-workspace-subtitle]");

    let currentContract = null;
    let currentCanEdit = false;

    function fillEditForm(contract) {
        if (!editForm) return;

        editForm.querySelector("[data-contract-id]").value =
            contract.id || "";

        editForm.querySelector('[name="operatorName"]').value =
            contract.operatorName || "";

        editForm.querySelector('[name="contractNumber"]').value =
            contract.contractNumber || "";

        editForm.querySelector('[name="accountPoint"]').value =
            contract.accountPoint || "";

        editForm.querySelector('[name="billingSchedule"]').value =
            contract.billingSchedule || "Monthly";

        editForm.querySelector('[name="validFrom"]').value =
            contract.validFrom || "";

        editForm.querySelector('[name="validTo"]').value =
            contract.validTo || "";

        editForm.querySelector('[name="fixedCharge"]').value =
            ((Number(contract.fixedChargeMinor || 0)) / 100)
                .toFixed(2);

        editForm.querySelector('[name="currencyCode"]').value =
            contract.currencyCode || "PLN";

        editForm.querySelector('[name="reason"]').value = "";
    }

    function renderDetails(contract, canEdit) {
        if (!details) return;

        details.innerHTML = `
            <div>
                <span>Operator</span>
                <strong>${esc(contract.operatorName || "—")}</strong>
            </div>

            <div>
                <span>Medium</span>
                <strong>${esc(mediumLabel(contract.medium))}</strong>
            </div>

            <div>
                <span>Działka</span>
                <strong>${esc(contract.parcelName || "—")}</strong>
            </div>

            <div>
                <span>Numer umowy</span>
                <strong>${esc(contract.contractNumber || "—")}</strong>
            </div>

            <div>
                <span>Punkt / PPE</span>
                <strong>${esc(contract.accountPoint || "—")}</strong>
            </div>

            <div>
                <span>Harmonogram</span>
                <strong>${esc(billingLabel(contract.billingSchedule))}</strong>
            </div>

            <div>
                <span>Okres obowiązywania</span>
                <strong>${esc(date(contract.validFrom))} – ${esc(date(contract.validTo))}</strong>
            </div>

            <div>
                <span>Opłata stała</span>
                <strong>${esc(money(contract.fixedChargeMinor, contract.currencyCode))}</strong>
            </div>

            ${canEdit
                ? `
                    <div class="utilities-contract-detail-action">
                        <button type="button"
                                class="btn btn-primary btn-sm"
                                data-contract-edit-start>
                            Edytuj dane umowy
                        </button>
                    </div>`
                : ""}
        `;

        details.querySelector(
            "[data-contract-edit-start]"
        )?.addEventListener("click", () => {
            if (!editForm) return;

            fillEditForm(contract);
            editForm.hidden = false;
            editForm.scrollIntoView({
                behavior: "smooth",
                block: "nearest"
            });
        });
    }

    function renderHistory(history) {
        if (!historyHost) return;

        const items = Array.isArray(history)
            ? history
            : [];

        if (!items.length) {
            historyHost.innerHTML = `
                <div class="utilities-empty compact">
                    Brak zapisanych zmian. To jest aktualna wersja umowy.
                </div>`;
            return;
        }

        historyHost.innerHTML = `
            <div class="utilities-contract-history-list">
                ${items.map(item => `
                    <article>
                        <div class="utilities-contract-history-date">
                            <strong>${esc(dateTime(item.changedAtUtc))}</strong>
                            <small>${esc(item.reason || "Edycja umowy")}</small>
                        </div>

                        <div class="utilities-contract-history-changes">
                            ${(item.changes || []).map(change => `
                                <div>
                                    <span>${esc(change.field)}</span>
                                    <strong>
                                        ${esc(change.before || "—")}
                                        <b>→</b>
                                        ${esc(change.after || "—")}
                                    </strong>
                                </div>
                            `).join("")}
                        </div>
                    </article>
                `).join("")}
            </div>
        `;
    }

    async function loadContract(contractId) {
        if (!workspace || !loading || !content) return;

        workspace.hidden = false;
        loading.hidden = false;
        content.hidden = true;

        if (editForm) {
            editForm.hidden = true;
        }

        workspace.scrollIntoView({
            behavior: "smooth",
            block: "start"
        });

        try {
            const response = await fetch(
                `/Utilities/Contracts/Data/${encodeURIComponent(contractId)}`,
                {
                    credentials: "same-origin",
                    headers: {
                        "Accept": "application/json"
                    }
                }
            );

            const result =
                await response.json().catch(() => ({}));

            if (!response.ok) {
                throw new Error(
                    result.message
                    || "Nie udało się wczytać umowy."
                );
            }

            currentContract = result.contract;
            currentCanEdit = Boolean(result.canEdit);

            if (title) {
                title.textContent =
                    result.contract.operatorName || "Umowa";
            }

            if (subtitle) {
                subtitle.textContent =
                    `${mediumLabel(result.contract.medium)} · ${
                        result.contract.contractNumber || "bez numeru umowy"
                    }`;
            }

            renderDetails(
                result.contract,
                currentCanEdit
            );

            renderHistory(
                result.history
            );

            loading.hidden = true;
            content.hidden = false;
        } catch (error) {
            loading.innerHTML = `
                <div class="error-box">
                    ${esc(error.message || "Nie udało się wczytać umowy.")}
                </div>`;
        }
    }

    root.querySelectorAll("[data-contract-open]")
        .forEach(button => {
            button.addEventListener("click", () => {
                loadContract(
                    button.dataset.contractOpen
                );
            });
        });

    root.querySelector("[data-contract-workspace-close]")
        ?.addEventListener("click", () => {
            if (workspace) workspace.hidden = true;
            if (editForm) editForm.hidden = true;
        });

    root.querySelector("[data-contract-edit-cancel]")
        ?.addEventListener("click", () => {
            if (editForm) editForm.hidden = true;
        });

    editForm?.addEventListener("submit", async event => {
        event.preventDefault();

        const submit =
            editForm.querySelector('button[type="submit"]');

        submit.disabled = true;

        try {
            const body =
                new FormData(editForm);

            const response = await fetch(
                "/Utilities/Contracts/Update",
                {
                    method: "POST",
                    body,
                    credentials: "same-origin"
                }
            );

            const result =
                await response.json().catch(() => ({}));

            if (!response.ok) {
                throw new Error(
                    result.message
                    || "Nie udało się zapisać umowy."
                );
            }

            window.alert(
                result.message
                || "Zmiany zostały zapisane."
            );

            const id =
                body.get("contractId");

            await loadContract(id);

            // Odświeżamy również kartę na stronie, żeby po zmianie
            // operatora numer był aktualny w całym module.
            const card =
                root.querySelector(
                    `[data-contract-card="${CSS.escape(String(id))}"]`
                );

            if (card && currentContract) {
                // Dane karty zostaną w pełni odświeżone po przeładowaniu.
                // Nie wykonujemy dodatkowego zapisu po stronie klienta.
            }

            window.location.reload();
        } catch (error) {
            window.alert(
                error.message
                || "Nie udało się zapisać umowy."
            );

            submit.disabled = false;
        }
    });
})();
