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

    const baseTariffSummary =
        root.querySelector("[data-base-tariff-summary]");

    const baseTariffForm =
        root.querySelector("[data-base-tariff-form]");

    let currentContract = null;
    let currentCanEdit = false;
    let currentTariff = null;

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


    function defaultUnitForMedium(medium) {
        return ({
            Electricity: "kWh",
            Water: "m3",
            Gas: "m3",
            Heating: "kWh"
        }[medium] || "unit");
    }

    function fillBaseTariffForm(contract, tariff) {
        if (!baseTariffForm) return;

        baseTariffForm.querySelector("[data-base-tariff-contract-id]").value =
            contract?.id || "";

        baseTariffForm.querySelector("[data-base-tariff-id]").value =
            tariff?.id || "";

        baseTariffForm.querySelector('[name="name"]').value =
            tariff?.name || "Taryfa podstawowa";

        baseTariffForm.querySelector('[name="ratePerUnit"]').value =
            Number(tariff?.ratePerUnit || 0) > 0
                ? Number(tariff.ratePerUnit).toFixed(6)
                : "";

        baseTariffForm.querySelector('[name="unitCode"]').value =
            tariff?.unitCode
            || defaultUnitForMedium(contract?.medium);

        baseTariffForm.querySelector('[name="zoneCode"]').value =
            tariff?.zoneCode || "ALL";

        baseTariffForm.querySelector('[name="componentCode"]').value =
            tariff?.componentCode || "Consumption";

        baseTariffForm.querySelector('[name="currencyCode"]').value =
            tariff?.currencyCode
            || contract?.currencyCode
            || "PLN";

        baseTariffForm.querySelector('[name="validFrom"]').value =
            tariff?.validFrom
            || contract?.validFrom
            || new Date().toISOString().slice(0, 10);

        baseTariffForm.querySelector('[name="validTo"]').value =
            tariff?.validTo || "";

        baseTariffForm.querySelector('[name="reason"]').value = "";
    }

    function renderBaseTariff(contract, tariff, canEdit) {
        currentTariff = tariff || null;

        if (!baseTariffSummary) return;

        if (!tariff) {
            baseTariffSummary.innerHTML = `
                <div class="utilities-base-tariff-empty">
                    <div>
                        <strong>Brak taryfy podstawowej</strong>
                        <span>
                            Dla tej umowy nie znaleziono taryfy. Utwórz ją tutaj,
                            aby podliczniki mogły automatycznie pobierać stawkę.
                        </span>
                    </div>
                    ${canEdit
                        ? `<button type="button"
                                   class="btn btn-primary btn-sm"
                                   data-base-tariff-edit>
                               + Ustaw taryfę podstawową
                           </button>`
                        : ""}
                </div>`;
        } else {
            baseTariffSummary.innerHTML = `
                <div class="utilities-base-tariff-summary">
                    <div>
                        <span>Nazwa</span>
                        <strong>${esc(tariff.name || "Taryfa podstawowa")}</strong>
                    </div>
                    <div>
                        <span>Stawka</span>
                        <strong>${esc(Number(tariff.ratePerUnit || 0).toLocaleString("pl-PL", { maximumFractionDigits: 6 }))} ${esc(tariff.currencyCode || "PLN")}/${esc(tariff.unitCode || "")}</strong>
                    </div>
                    <div>
                        <span>Strefa / składnik</span>
                        <strong>${esc(tariff.zoneCode || "ALL")} · ${esc(tariff.componentCode || "Consumption")}</strong>
                    </div>
                    <div>
                        <span>Obowiązuje</span>
                        <strong>${esc(date(tariff.validFrom))} – ${esc(date(tariff.validTo))}</strong>
                    </div>
                    <div>
                        <span>Wersje tej umowy</span>
                        <strong>${esc(tariff.versionCount || 1)}</strong>
                    </div>
                    ${canEdit
                        ? `<div class="utilities-base-tariff-action">
                               <button type="button"
                                       class="btn btn-secondary btn-sm"
                                       data-base-tariff-edit>
                                   Edytuj taryfę
                               </button>
                           </div>`
                        : ""}
                </div>

                ${Number(tariff.versionCount || 0) > 1
                    ? `<div class="utilities-base-tariff-note">
                           W bazie istnieje ${esc(tariff.versionCount)} wersji taryfy tej umowy.
                           Pokazana jest wersja aktywna lub najnowsza.
                       </div>`
                    : ""}
            `;
        }

        baseTariffSummary
            .querySelector("[data-base-tariff-edit]")
            ?.addEventListener("click", () => {
                fillBaseTariffForm(contract, tariff);
                baseTariffForm.hidden = false;
                baseTariffForm.scrollIntoView({
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

            renderBaseTariff(
                result.contract,
                result.tariff,
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


    root.querySelector("[data-base-tariff-cancel]")
        ?.addEventListener("click", () => {
            if (baseTariffForm) {
                baseTariffForm.hidden = true;
            }
        });

    baseTariffForm?.addEventListener("submit", async event => {
        event.preventDefault();

        const submit =
            baseTariffForm.querySelector('button[type="submit"]');

        submit.disabled = true;

        try {
            const body =
                new FormData(baseTariffForm);

            const response =
                await fetch(
                    "/Utilities/Contracts/BaseTariff/Save",
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
                    || "Nie udało się zapisać taryfy podstawowej."
                );
            }

            window.alert(
                result.message
                || "Taryfa podstawowa została zapisana."
            );

            const contractId =
                body.get("contractId");

            await loadContract(contractId);

            baseTariffForm.hidden = true;
        } catch (error) {
            window.alert(
                error.message
                || "Nie udało się zapisać taryfy podstawowej."
            );

            submit.disabled = false;
        }
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
