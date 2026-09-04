(() => {
    const path =
        (window.location.pathname || "")
            .toLowerCase();

    if (path !== "/utilities"
        && path !== "/utilities/") {
        return;
    }

    const root =
        document.querySelector(
            "[data-utility-invoice-flow]");

    if (!root) {
        return;
    }

    const endpoint =
        "/Utilities/InvoiceDistribution";

    const workspace =
        root.querySelector(
            "[data-utility-invoice-workspace]");

    const form =
        root.querySelector(
            "[data-utility-invoice-form]");

    const contractSelect =
        root.querySelector(
            "[data-utility-invoice-contract]");

    const mediumInput =
        root.querySelector(
            "[data-utility-invoice-medium-input]");

    const allocationModeInput =
        root.querySelector(
            "[data-utility-invoice-allocation-mode]");

    const occupancyInput =
        root.querySelector(
            "[data-utility-tenant-occupancy-json]");

    const tenantList =
        root.querySelector(
            "[data-utility-tenant-list]");

    const waterFields =
        root.querySelector(
            "[data-utility-water-fields]");

    const gasFields =
        root.querySelector(
            "[data-utility-gas-fields]");

    const wasteFields =
        root.querySelector(
            "[data-utility-waste-fields]");

    const peopleSection =
        root.querySelector(
            "[data-utility-people-section]");

    const waterConsumptionFields =
        root.querySelector(
            "[data-water-consumption-fields]");

    const waterManualFields =
        root.querySelector(
            "[data-water-manual-fields]");

    const waterSubmeter =
        root.querySelector(
            "[data-water-submeter]");

    const waterSubmeterInfo =
        root.querySelector(
            "[data-water-submeter-info]");

    const title =
        root.querySelector(
            "[data-utility-invoice-title]");

    const subtitle =
        root.querySelector(
            "[data-utility-invoice-subtitle]");

    const kicker =
        root.querySelector(
            "[data-utility-invoice-kicker]");

    const householdPersonCount =
        root.querySelector(
            "[data-household-person-count]");

    const tenantPersonCount =
        root.querySelector(
            "[data-tenant-person-count]");

    const previewGross =
        root.querySelector(
            "[data-preview-gross]");

    const previewHousehold =
        root.querySelector(
            "[data-preview-household]");

    const previewTenants =
        root.querySelector(
            "[data-preview-tenants]");

    const previewHouseholdNote =
        root.querySelector(
            "[data-preview-household-note]");

    const previewTenantNote =
        root.querySelector(
            "[data-preview-tenant-note]");

    const historySection =
        root.querySelector(
            "[data-utility-invoice-history]");

    const historyList =
        root.querySelector(
            "[data-utility-invoice-history-list]");

    let data = null;
    let currentMedium = null;

    const esc = value =>
        String(value ?? "")
            .replaceAll("&", "&amp;")
            .replaceAll("<", "&lt;")
            .replaceAll(">", "&gt;")
            .replaceAll('"', "&quot;")
            .replaceAll("'", "&#39;");

    const parseNumber = value => {
        const normalized =
            String(value ?? "")
                .trim()
                .replace(/\s/g, "")
                .replace(",", ".");

        const number =
            Number.parseFloat(
                normalized);

        return Number.isFinite(number)
            ? number
            : 0;
    };

    const money = value => {
        const number =
            Number(value) || 0;

        return new Intl.NumberFormat(
            "pl-PL",
            {
                style: "currency",
                currency: "PLN"
            })
            .format(number);
    };

    const minorMoney = value =>
        money(
            (Number(value) || 0)
            / 100
        );

    const dateTime = value => {
        if (!value) {
            return "—";
        }

        try {
            return new Intl.DateTimeFormat(
                "pl-PL",
                {
                    dateStyle: "short",
                    timeStyle: "short"
                })
                .format(
                    new Date(value)
                );
        } catch {
            return String(value);
        }
    };

    const mediumLabel = medium => ({
        Water: "Woda",
        Gas: "Gaz",
        Waste: "Odpady / śmieci"
    }[medium] || medium || "Media");

    const formValue = name =>
        form?.querySelector(
            `[name="${name}"]`
        )?.value ?? "";

    const setHidden = (
        element,
        hidden
    ) => {
        if (element) {
            element.hidden =
                Boolean(hidden);
        }
    };

    const dateOnly = value =>
        String(value || "")
            .slice(0, 10);

    const overlaps = (
        tenant,
        from,
        to
    ) => {
        if (!from || !to) {
            return true;
        }

        const status =
            String(tenant.status || "");

        if ([
            "Prepared",
            "Draft",
            "Cancelled",
            "Archived"
        ].includes(status)) {
            return false;
        }

        const leaseFrom =
            dateOnly(
                tenant.leaseFrom
            ) || "0001-01-01";

        const leaseTo =
            dateOnly(
                tenant.leaseTo
            ) || "9999-12-31";

        return leaseFrom <= to
            && leaseTo >= from;
    };

    const currentTenants = () => {
        if (!data) {
            return [];
        }

        const from =
            formValue(
                "periodFrom"
            );

        const to =
            formValue(
                "periodTo"
            );

        return (data.tenants || [])
            .filter(tenant =>
                overlaps(
                    tenant,
                    from,
                    to
                )
            );
    };

    const occupancyItems = () =>
        Array.from(
            tenantList?.querySelectorAll(
                "[data-tenant-occupancy]"
            ) || []
        )
        .map(input => ({
            leaseContractId:
                input.dataset.leaseContractId,
            persons:
                Math.max(
                    1,
                    Number.parseInt(
                        input.value || "1",
                        10
                    ) || 1
                )
        }));

    const occupancyTotal = () =>
        occupancyItems()
            .reduce(
                (sum, item) =>
                    sum + item.persons,
                0
            );

    function renderTenants() {
        if (!tenantList
            || !data) {
            return;
        }

        const tenants =
            currentTenants();

        if (!tenants.length) {
            tenantList.innerHTML = `
                <div class="utilities-empty compact">
                    Brak lokatorów z umową obejmującą wybrany okres faktury.
                </div>
            `;

            if (tenantPersonCount) {
                tenantPersonCount.textContent =
                    "0";
            }

            updatePreview();
            return;
        }

        tenantList.innerHTML =
            tenants.map(tenant => `
                <article class="utility-tenant-row">
                    <div>
                        <strong>${esc(tenant.tenantName)}</strong>
                        <small>
                            ${esc(tenant.roomName)}
                            · ${esc(dateOnly(tenant.leaseFrom) || "—")}
                            – ${esc(dateOnly(tenant.leaseTo) || "bezterminowo")}
                        </small>
                    </div>

                    <label>
                        <span>Liczba osób</span>
                        <input type="number"
                               min="1"
                               step="1"
                               value="1"
                               data-tenant-occupancy
                               data-lease-contract-id="${esc(tenant.contractId)}" />
                    </label>

                    <div data-tenant-share>
                        <span>Udział</span>
                        <strong>—</strong>
                    </div>
                </article>
            `).join("");

        tenantList
            .querySelectorAll(
                "[data-tenant-occupancy]"
            )
            .forEach(input => {
                input.addEventListener(
                    "input",
                    () => {
                        if (tenantPersonCount) {
                            tenantPersonCount.textContent =
                                String(
                                    occupancyTotal()
                                );
                        }

                        updatePreview();
                    }
                );
            });

        if (tenantPersonCount) {
            tenantPersonCount.textContent =
                String(
                    occupancyTotal()
                );
        }

        updatePreview();
    }

    function populateContracts(
        medium
    ) {
        if (!contractSelect
            || !data) {
            return false;
        }

        const contracts =
            (data.contracts || [])
                .filter(contract =>
                    contract.medium === medium
                );

        contractSelect.innerHTML =
            contracts.length
                ? contracts.map(contract => `
                    <option value="${esc(contract.id)}">
                        ${esc(contract.label)}
                    </option>
                `).join("")
                : `
                    <option value="">
                        — brak umowy dla tego medium —
                    </option>
                `;

        return contracts.length > 0;
    }

    function populateWaterSubmeters() {
        if (!waterSubmeter
            || !data) {
            return;
        }

        const meters =
            data.waterSubmeters || [];

        waterSubmeter.innerHTML = `
            <option value="">
                — wybierz / wpisz zużycie ręcznie —
            </option>
            ${meters.map(meter => `
                <option value="${esc(meter.id)}"
                        data-consumption="${esc(meter.consumption ?? "")}"
                        data-previous="${esc(meter.previousValue ?? "")}"
                        data-current="${esc(meter.currentValue ?? "")}"
                        data-location="${esc(meter.locationName || "")}"
                        data-previous-at="${esc(meter.previousAtUtc || "")}"
                        data-current-at="${esc(meter.currentAtUtc || "")}">
                    ${esc(meter.name)}
                    ${meter.locationName ? ` · ${esc(meter.locationName)}` : ""}
                </option>
            `).join("")}
        `;
    }

    function setWaterMode(
        mode
    ) {
        const manual =
            mode ===
            "ManualTenantAmount";

        if (allocationModeInput) {
            allocationModeInput.value =
                manual
                    ? "ManualTenantAmount"
                    : "WaterByConsumption";
        }

        setHidden(
            waterConsumptionFields,
            manual
        );

        setHidden(
            waterManualFields,
            !manual
        );

        updatePreview();
    }

    function openMedium(
        medium
    ) {
        currentMedium =
            medium;

        if (mediumInput) {
            mediumInput.value =
                medium;
        }

        const hasContract =
            populateContracts(
                medium
            );

        if (!hasContract) {
            window.alert(
                `Najpierw dodaj umowę dla medium: ${mediumLabel(medium)}.`
            );
            return;
        }

        setHidden(
            waterFields,
            medium !== "Water"
        );

        setHidden(
            gasFields,
            medium !== "Gas"
        );

        setHidden(
            wasteFields,
            medium !== "Waste"
        );

        setHidden(
            peopleSection,
            ![
                "Water",
                "Waste"
            ].includes(medium)
        );

        if (kicker) {
            kicker.textContent =
                mediumLabel(medium)
                    .toUpperCase();
        }

        if (title) {
            title.textContent =
                medium === "Water"
                    ? "Rozliczenie FV Aquanet / woda"
                    : medium === "Gas"
                        ? "Rozliczenie FV za gaz — Dom 1"
                        : "Rozliczenie opłaty za odpady";
        }

        if (subtitle) {
            subtitle.textContent =
                medium === "Water"
                    ? "Krok 1: zarejestruj pełną FV. Krok 2: opłać ją w Finansach domowych. Krok 3: wygeneruj rozliczenie lokatorów."
                    : medium === "Gas"
                        ? "100% FV pozostaje kosztem gospodarstwa; lokatorów nie obciążamy gazem."
                        : "Koszt dzielimy przez wszystkie osoby, a udział lokatorów trafia do ich rozliczeń.";
        }

        if (workspace) {
            workspace.hidden =
                false;

            workspace.scrollIntoView({
                behavior:
                    "smooth",
                block:
                    "nearest"
            });
        }

        renderTenants();
        updatePreview();
    }

    function updateTenantShareLabels(
        tenantPool
    ) {
        const items =
            occupancyItems();

        const total =
            items.reduce(
                (sum, item) =>
                    sum + item.persons,
                0
            );

        const rows =
            Array.from(
                tenantList?.querySelectorAll(
                    ".utility-tenant-row"
                ) || []
            );

        rows.forEach((row, index) => {
            const share =
                row.querySelector(
                    "[data-tenant-share] strong"
                );

            if (!share) {
                return;
            }

            const item =
                items[index];

            const amount =
                total > 0
                    && item
                    ? tenantPool
                      * item.persons
                      / total
                    : 0;

            share.textContent =
                money(amount);
        });
    }

    function updatePreview() {
        if (!data
            || !currentMedium) {
            return;
        }

        const gross =
            Math.max(
                0,
                parseNumber(
                    formValue(
                        "grossAmount"
                    )
                )
            );

        const householdCount =
            (data.householdPersons || [])
                .length;

        const tenantCount =
            occupancyTotal();

        let tenantPool = 0;
        let householdPool =
            gross;

        if (currentMedium
            === "Water") {
            const mode =
                allocationModeInput?.value
                || "WaterByConsumption";

            if (mode
                === "ManualTenantAmount") {
                tenantPool =
                    Math.max(
                        0,
                        parseNumber(
                            formValue(
                                "manualTenantAmount"
                            )
                        )
                    );
            } else {
                const totalConsumption =
                    Math.max(
                        0,
                        parseNumber(
                            formValue(
                                "totalConsumption"
                            )
                        )
                    );

                const tenantConsumption =
                    Math.max(
                        0,
                        parseNumber(
                            formValue(
                                "tenantConsumption"
                            )
                        )
                    );

                if (totalConsumption > 0) {
                    tenantPool =
                        gross
                        * Math.min(
                            tenantConsumption,
                            totalConsumption
                        )
                        / totalConsumption;
                }
            }

            if (tenantCount === 0) {
                tenantPool = 0;
            }

            tenantPool =
                Math.min(
                    gross,
                    tenantPool
                );

            householdPool =
                gross
                - tenantPool;

            if (previewHouseholdNote) {
                previewHouseholdNote.textContent =
                    householdCount > 0
                        ? `${householdCount} domowników · średnio ${money(householdPool / householdCount)} / os. (informacyjnie)`
                        : "brak domowników";
            }

            if (previewTenantNote) {
                previewTenantNote.textContent =
                    tenantCount > 0
                        ? `${tenantCount} osób lokatorów`
                        : "brak lokatorów w okresie FV";
            }
        }

        if (currentMedium
            === "Waste") {
            const allPersons =
                householdCount
                + tenantCount;

            const perPerson =
                allPersons > 0
                    ? gross
                      / allPersons
                    : 0;

            tenantPool =
                perPerson
                * tenantCount;

            householdPool =
                gross
                - tenantPool;

            if (previewHouseholdNote) {
                previewHouseholdNote.textContent =
                    `${householdCount} osób × ${money(perPerson)}`;
            }

            if (previewTenantNote) {
                previewTenantNote.textContent =
                    `${tenantCount} osób × ${money(perPerson)}`;
            }
        }

        if (currentMedium
            === "Gas") {
            tenantPool = 0;
            householdPool =
                gross;

            if (previewHouseholdNote) {
                previewHouseholdNote.textContent =
                    "100% kosztu · Dom 1 / gospodarstwo";
            }

            if (previewTenantNote) {
                previewTenantNote.textContent =
                    "gaz nie jest doliczany lokatorom";
            }
        }

        if (previewGross) {
            previewGross.textContent =
                money(gross);
        }

        if (previewHousehold) {
            previewHousehold.textContent =
                money(householdPool);
        }

        if (previewTenants) {
            previewTenants.textContent =
                money(tenantPool);
        }

        updateTenantShareLabels(
            tenantPool
        );
    }

    function renderHistory() {
        if (!historySection
            || !historyList
            || !data) {
            return;
        }

        const history =
            data.history || [];

        if (!history.length) {
            historySection.hidden =
                true;
            return;
        }

        historySection.hidden =
            false;

        historyList.innerHTML =
            history.map(item => {
                let waterAction = "";

                if (item.medium === "Water") {
                    if (!item.householdInvoiceId) {
                        waterAction = `
                            <div class="utility-water-payment-state is-warning">
                                Nie znaleziono FV w Finansach domowych.
                            </div>`;
                    } else if (!item.isFullyPaid) {
                        waterAction = `
                            <div class="utility-water-payment-state is-warning">
                                <strong>Najpierw opłać całą FV</strong>
                                <span>
                                    Zapłacono ${esc(minorMoney(item.paidMinor))},
                                    pozostało ${esc(minorMoney(item.remainingMinor))}
                                </span>
                                <a href="/HouseholdFinance#hf-invoices">
                                    Przejdź do Finansów domowych →
                                </a>
                            </div>`;
                    } else if (Number(item.tenantShareMinor || 0) <= 0) {
                        waterAction = `
                            <div class="utility-water-payment-state is-done">
                                FV opłacona · brak kwoty do rozliczenia lokatorów
                            </div>`;
                    } else if (Number(item.tenantCharges || 0) > 0
                               && Number(item.pendingCorrections || 0) === 0) {
                        waterAction = `
                            <div class="utility-water-payment-state is-done">
                                <strong>FV opłacona przez dom</strong>
                                <span>
                                    Rozliczenie lokatorów już wygenerowane.
                                </span>
                            </div>`;
                    } else {
                        waterAction = `
                            <div class="utility-water-payment-state is-ready">
                                <strong>FV opłacona w całości</strong>
                                <span>
                                    Możesz teraz wygenerować ${esc(minorMoney(item.tenantShareMinor))}
                                    do rozliczeń lokatorów.
                                </span>
                                <button type="button"
                                        class="btn btn-primary btn-sm"
                                        data-generate-water
                                        data-utility-invoice-id="${esc(item.utilityInvoiceId)}">
                                    Generuj rozliczenie lokatorów
                                </button>
                            </div>`;
                    }
                }

                return `
                    <article>
                        <div>
                            <strong>
                                ${esc(mediumLabel(item.medium))}
                                · FV ${esc(item.invoiceNo)}
                            </strong>
                            <small>
                                ${esc(item.periodKey)}
                                · ${esc(dateTime(item.createdAtUtc))}
                            </small>
                        </div>

                        <div>
                            <span>Cała FV — płaci dom</span>
                            <strong>${esc(minorMoney(item.grossAmountMinor))}</strong>
                        </div>

                        <div>
                            <span>Koszt domu po rozliczeniu</span>
                            <strong>${esc(minorMoney(item.householdShareMinor))}</strong>
                        </div>

                        <div>
                            <span>Do odzyskania od lokatorów</span>
                            <strong>${esc(minorMoney(item.tenantShareMinor))}</strong>
                        </div>

                        ${waterAction
                            ? `<div class="utility-water-history-action">${waterAction}</div>`
                            : ""}
                    </article>
                `;
            }).join("");

        historyList
            .querySelectorAll("[data-generate-water]")
            .forEach(button => {
                button.addEventListener("click", async () => {
                    const invoiceId =
                        button.dataset.utilityInvoiceId;

                    if (!invoiceId) {
                        return;
                    }

                    if (!window.confirm(
                            "FV za wodę jest opłacona przez dom. Wygenerować teraz należności za wodę dla lokatorów?"
                        )) {
                        return;
                    }

                    const token =
                        form?.querySelector(
                            'input[name="__RequestVerificationToken"]'
                        )?.value;

                    const body =
                        new FormData();

                    body.append(
                        "utilityInvoiceId",
                        invoiceId
                    );

                    if (token) {
                        body.append(
                            "__RequestVerificationToken",
                            token
                        );
                    }

                    button.disabled = true;

                    try {
                        const response =
                            await fetch(
                                `${endpoint}/Water/Generate`,
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
                                result.message
                                || "Nie udało się wygenerować rozliczenia lokatorów."
                            );
                        }

                        window.alert(
                            result.message
                            || "Rozliczenie lokatorów zostało wygenerowane."
                        );

                        window.location.reload();
                    } catch (error) {
                        window.alert(
                            error.message
                            || "Nie udało się wygenerować rozliczenia lokatorów."
                        );

                        button.disabled = false;
                    }
                });
            });
    }

    async function loadData() {
        try {
            const response =
                await fetch(
                    `${endpoint}/Data`,
                    {
                        credentials:
                            "same-origin",
                        cache:
                            "no-store",
                        headers: {
                            "Accept":
                                "application/json"
                        }
                    }
                );

            if (!response.ok) {
                return;
            }

            data =
                await response.json();

            if (householdPersonCount) {
                householdPersonCount.textContent =
                    String(
                        (data.householdPersons || [])
                            .length
                    );
            }

            populateWaterSubmeters();
            renderHistory();
        } catch (error) {
            console.warn(
                "Nie udało się wczytać danych do rozliczenia FV mediów.",
                error
            );
        }
    }

    root.querySelectorAll(
        "[data-utility-invoice-medium]"
    )
    .forEach(button => {
        button.addEventListener(
            "click",
            () => {
                openMedium(
                    button.dataset.utilityInvoiceMedium
                );
            }
        );
    });

    root.querySelectorAll(
        "[data-water-mode]"
    )
    .forEach(input => {
        input.addEventListener(
            "change",
            () => {
                if (input.checked) {
                    setWaterMode(
                        input.value
                    );
                }
            }
        );
    });

    waterSubmeter?.addEventListener(
        "change",
        () => {
            const option =
                waterSubmeter.selectedOptions?.[0];

            const consumption =
                option?.dataset.consumption;

            const tenantConsumptionInput =
                form?.querySelector(
                    '[name="tenantConsumption"]'
                );

            if (tenantConsumptionInput
                && consumption) {
                tenantConsumptionInput.value =
                    consumption;
            }

            if (waterSubmeterInfo) {
                if (option
                    && option.value
                    && consumption) {
                    waterSubmeterInfo.textContent =
                        `${option.dataset.location || ""} · ` +
                        `${option.dataset.previous || "—"} → ${option.dataset.current || "—"} m³ ` +
                        `= ${consumption} m³ · ` +
                        `${dateTime(option.dataset.previousAt)} → ${dateTime(option.dataset.currentAt)}`;
                } else {
                    waterSubmeterInfo.textContent =
                        "Wpisz zużycie lokatorów ręcznie albo wybierz podlicznik z dwoma zatwierdzonymi odczytami.";
                }
            }

            updatePreview();
        }
    );

    [
        "grossAmount",
        "totalConsumption",
        "tenantConsumption",
        "manualTenantAmount",
        "periodFrom",
        "periodTo"
    ]
    .forEach(name => {
        form?.querySelector(
            `[name="${name}"]`
        )?.addEventListener(
            "input",
            () => {
                if (name === "periodFrom"
                    || name === "periodTo") {
                    renderTenants();
                } else {
                    updatePreview();
                }
            }
        );

        form?.querySelector(
            `[name="${name}"]`
        )?.addEventListener(
            "change",
            () => {
                if (name === "periodFrom"
                    || name === "periodTo") {
                    renderTenants();
                } else {
                    updatePreview();
                }
            }
        );
    });

    const closeWorkspace = () => {
        if (workspace) {
            workspace.hidden =
                true;
        }
    };

    root.querySelector(
        "[data-utility-invoice-close]"
    )?.addEventListener(
        "click",
        closeWorkspace
    );

    root.querySelector(
        "[data-utility-invoice-cancel]"
    )?.addEventListener(
        "click",
        closeWorkspace
    );

    form?.addEventListener(
        "submit",
        async event => {
            event.preventDefault();

            const submit =
                form.querySelector(
                    'button[type="submit"]'
                );

            const occupancies =
                occupancyItems();

            if (occupancyInput) {
                occupancyInput.value =
                    JSON.stringify(
                        occupancies
                    );
            }

            const body =
                new FormData(
                    form
                );

            const gross =
                parseNumber(
                    body.get(
                        "grossAmount"
                    )
                );

            if (gross <= 0) {
                window.alert(
                    "Podaj kwotę faktury większą od 0."
                );
                return;
            }

            const confirmText =
                currentMedium === "Water"
                    ? `Zarejestrować całą FV za wodę ${money(gross)}? Rozliczenie lokatorów NIE zostanie jeszcze utworzone. Najpierw dom musi opłacić 100% FV w Finansach domowych.`
                    : currentMedium === "Waste"
                        ? `Zarejestrować opłatę za odpady ${money(gross)} i podzielić ją przez wszystkie osoby?`
                        : `Zarejestrować FV za gaz ${money(gross)} jako koszt gospodarstwa / Domu 1?`;

            if (!window.confirm(
                    `${confirmText} Sama płatność FV zostanie wykonana w Finansach domowych.`
                )) {
                return;
            }

            submit.disabled =
                true;

            try {
                const response =
                    await fetch(
                        `${endpoint}/Create`,
                        {
                            method:
                                "POST",
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
                        || "Nie udało się rozliczyć faktury mediów."
                    );
                }

                window.alert(
                    result.message
                    || "Faktura i rozliczenia zostały zapisane."
                );

                window.location.reload();
            } catch (error) {
                window.alert(
                    error.message
                    || "Nie udało się rozliczyć faktury mediów."
                );

                submit.disabled =
                    false;
            }
        }
    );

    loadData();
})();
