(() => {
    const path = (window.location.pathname || "").toLowerCase();

    if (path !== "/utilities" && path !== "/utilities/") {
        return;
    }

    const endpoint = "/Utilities/SubmeterTenant";
    const section = document.getElementById("submeterTenantSettlement");
    const host = section?.querySelector("[data-submeter-tenant-list]");

    if (!section || !host) {
        return;
    }

    const esc = value => String(value ?? "")
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#39;");

    const number = (value, digits = 3) => {
        const numeric = Number(value);
        if (!Number.isFinite(numeric)) return "—";

        return new Intl.NumberFormat("pl-PL", {
            minimumFractionDigits: 0,
            maximumFractionDigits: digits
        }).format(numeric);
    };

    const money = minor => {
        return new Intl.NumberFormat("pl-PL", {
            style: "currency",
            currency: "PLN"
        }).format((Number(minor) || 0) / 100);
    };

    const dateTime = value => {
        if (!value || String(value).startsWith("0001-")) return "—";

        try {
            return new Intl.DateTimeFormat("pl-PL", {
                dateStyle: "short",
                timeStyle: "short"
            }).format(new Date(value));
        } catch {
            return String(value);
        }
    };

    const positiveRate = value => {
        const numeric = Number(value || 0);
        return Number.isFinite(numeric) && numeric > 0
            ? numeric
            : 0;
    };

    function renderRateField({
        inputName,
        title,
        recommendedRate,
        recommendedSource,
        generatedRate,
        generated,
        unitCode,
        toggleAttribute
    }) {
        const effectiveGeneratedRate = positiveRate(generatedRate);
        const effectiveRecommendedRate = positiveRate(recommendedRate);
        const initialRate = generated
            ? effectiveGeneratedRate
            : effectiveRecommendedRate;

        const value = initialRate > 0
            ? initialRate.toFixed(6)
            : "";

        const description = generated
            ? "Ta pozycja jest już zapisana dla bieżącego odczytu."
            : effectiveRecommendedRate > 0
                ? recommendedSource || "Stawka z taryfy licznika głównego"
                : "Brak stawki w taryfie — wpisz wartość ręcznie.";

        return `
            <label>
                <span>${esc(title)}</span>

                <div class="submeter-main-rate">
                    <strong>
                        ${initialRate > 0
                            ? `${esc(number(initialRate, 6))} PLN/${esc(unitCode)}`
                            : "Brak podpowiedzi stawki"}
                    </strong>
                    <small>${esc(description)}</small>
                </div>

                <div class="submeter-rate-input">
                    <input name="${esc(inputName)}"
                           type="number"
                           step="0.000001"
                           min="0.000001"
                           value="${esc(value)}"
                           placeholder="np. 0,420000"
                           ${generated || effectiveRecommendedRate > 0 ? "readonly" : ""}
                           required />
                    <b>PLN/${esc(unitCode)}</b>
                </div>

                ${!generated && effectiveRecommendedRate > 0
                    ? `
                        <label class="submeter-manual-rate-toggle">
                            <input type="checkbox"
                                   ${toggleAttribute} />
                            <span>Użyj innej stawki ręcznie</span>
                        </label>`
                    : ""}
            </label>`;
    }

    function renderItem(item, data) {
        const card = document.createElement("article");
        card.className = "submeter-tenant-card";

        if (!item.canGenerate) {
            card.classList.add("is-blocked");
        }

        const hasPair =
            item.previousReadingId
            && item.currentReadingId
            && item.previousReadingId !== "00000000-0000-0000-0000-000000000000"
            && item.currentReadingId !== "00000000-0000-0000-0000-000000000000";

        const energyGenerated = Boolean(item.energyAlreadyGenerated);
        const distributionGenerated = Boolean(item.distributionAlreadyGenerated);
        const fullyGenerated = energyGenerated && distributionGenerated;

        const energyRate = energyGenerated
            ? positiveRate(item.generatedRatePerUnit)
            : positiveRate(item.recommendedRatePerUnit);

        const distributionRate = distributionGenerated
            ? positiveRate(item.generatedDistributionRatePerUnit)
            : positiveRate(item.recommendedDistributionRatePerUnit);

        const energyRateField = renderRateField({
            inputName: "ratePerUnit",
            title: `Energia czynna za 1 ${item.unitCode}`,
            recommendedRate: item.recommendedRatePerUnit,
            recommendedSource: item.rateSource,
            generatedRate: item.generatedRatePerUnit,
            generated: energyGenerated,
            unitCode: item.unitCode,
            toggleAttribute: "data-submeter-manual-energy-rate"
        });

        const distributionRateField = renderRateField({
            inputName: "distributionRatePerUnit",
            title: `Przesył / dystrybucja za 1 ${item.unitCode}`,
            recommendedRate: item.recommendedDistributionRatePerUnit,
            recommendedSource: item.distributionRateSource,
            generatedRate: item.generatedDistributionRatePerUnit,
            generated: distributionGenerated,
            unitCode: item.unitCode,
            toggleAttribute: "data-submeter-manual-distribution-rate"
        });

        card.innerHTML = `
            <div class="submeter-tenant-head">
                <div>
                    <span>Prąd · podlicznik pokoju</span>
                    <strong>${esc(item.meterName)}</strong>
                    <small>
                        ${esc(item.roomName)}
                        ${item.tenantName ? `· lokator: ${esc(item.tenantName)}` : ""}
                    </small>
                </div>

                ${fullyGenerated
                    ? `<b class="submeter-state is-done">Energia + przesył rozliczone</b>`
                    : energyGenerated && item.canGenerate
                        ? `<b class="submeter-state is-ready">Do uzupełnienia przesył</b>`
                        : item.canGenerate
                            ? `<b class="submeter-state is-ready">Gotowe</b>`
                            : `<b class="submeter-state is-waiting">Brak danych</b>`}
            </div>

            ${hasPair
                ? `
                    <div class="submeter-reading-compare">
                        <div>
                            <span>Poprzedni odczyt</span>
                            <strong>${esc(number(item.previousValue))} ${esc(item.unitCode)}</strong>
                            <small>${esc(dateTime(item.previousReadingAtUtc))}</small>
                        </div>

                        <div class="submeter-reading-arrow">→</div>

                        <div>
                            <span>Nowy odczyt</span>
                            <strong>${esc(number(item.currentValue))} ${esc(item.unitCode)}</strong>
                            <small>${esc(dateTime(item.currentReadingAtUtc))}</small>
                        </div>

                        <div class="submeter-consumption">
                            <span>${item.canGenerate ? "Zużycie do rozliczenia" : "Różnica odczytów"}</span>
                            <strong>${esc(number(item.consumption))} ${esc(item.unitCode)}</strong>
                            <small>
                                ${item.canGenerate
                                    ? `strefa ${esc(item.zoneCode || "ALL")}`
                                    : `nie naliczono · strefa ${esc(item.zoneCode || "ALL")}`}
                            </small>
                        </div>
                    </div>`
                : ""}

            ${fullyGenerated
                ? `
                    <div class="submeter-generated-info">
                        <strong>Ten odczyt został już rozliczony w dwóch pozycjach.</strong>
                        <span>
                            Energia: ${esc(money(item.generatedAmountMinor))} ·
                            przesył: ${esc(money(item.generatedDistributionAmountMinor))} ·
                            razem: ${esc(money(item.generatedTotalAmountMinor))}.
                        </span>
                    </div>`
                : item.canGenerate
                    ? `
                        ${energyGenerated
                            ? `
                                <div class="submeter-generated-info">
                                    <strong>Energia z podlicznika jest już zapisana.</strong>
                                    <span>
                                        Kwota energii: ${esc(money(item.generatedAmountMinor))}.
                                        Ta paczka dopisze tylko brakujący przesył / dystrybucję i nie utworzy drugiej opłaty za energię.
                                    </span>
                                </div>`
                            : ""}

                        <form data-submeter-generate>
                            <input type="hidden"
                                   name="meterId"
                                   value="${esc(item.meterId)}" />
                            <input type="hidden"
                                   name="currentReadingId"
                                   value="${esc(item.currentReadingId)}" />

                            <div class="submeter-form-grid">
                                <label>
                                    <span>Miesiąc rozliczenia</span>
                                    <input name="periodKey"
                                           value="${esc(item.periodKey)}"
                                           pattern="[0-9]{4}-[0-9]{2}"
                                           required />
                                </label>

                                ${energyRateField}
                                ${distributionRateField}

                                <div class="submeter-form-summary">
                                    <span>Wyliczenie lokatora</span>
                                    <strong data-submeter-formula>
                                        ${esc(number(item.consumption))} ${esc(item.unitCode)} × stawka energii + stawka przesyłu
                                    </strong>
                                </div>
                            </div>

                            <div class="submeter-actions">
                                <button type="submit"
                                        class="btn btn-primary btn-sm">
                                    ${energyGenerated
                                        ? "Dodaj brakujący przesył do rozliczenia"
                                        : "Wylicz energię i przesył dla lokatora"}
                                </button>
                            </div>
                        </form>`
                    : `
                        <div class="submeter-block-reason">
                            ${esc(item.blockReason || "Brak danych do wyliczenia.")}
                        </div>`}
        `;

        const form = card.querySelector("[data-submeter-generate]");

        if (form) {
            const energyInput = form.querySelector('[name="ratePerUnit"]');
            const distributionInput = form.querySelector('[name="distributionRatePerUnit"]');
            const formula = form.querySelector("[data-submeter-formula]");
            const manualEnergyToggle = form.querySelector("[data-submeter-manual-energy-rate]");
            const manualDistributionToggle = form.querySelector("[data-submeter-manual-distribution-rate]");

            const resetRecommended = (input, generated, recommended) => {
                if (generated || !input) return;

                const value = positiveRate(recommended);
                if (value > 0) {
                    input.value = value.toFixed(6);
                }
            };

            const syncFormula = () => {
                const currentEnergyRate = Number.parseFloat(
                    String(energyInput?.value || "0").replace(",", ".")
                );

                const currentDistributionRate = Number.parseFloat(
                    String(distributionInput?.value || "0").replace(",", ".")
                );

                const consumption = Number(item.consumption || 0);

                const energyAmount = energyGenerated
                    ? (Number(item.generatedAmountMinor || 0) / 100)
                    : consumption * (Number.isFinite(currentEnergyRate) ? currentEnergyRate : 0);

                const distributionAmount = distributionGenerated
                    ? (Number(item.generatedDistributionAmountMinor || 0) / 100)
                    : consumption * (Number.isFinite(currentDistributionRate) ? currentDistributionRate : 0);

                if (!Number.isFinite(currentEnergyRate)
                    || currentEnergyRate <= 0
                    || !Number.isFinite(currentDistributionRate)
                    || currentDistributionRate <= 0) {
                    formula.textContent =
                        `${number(consumption)} ${item.unitCode} × energia + przesył`;
                    return;
                }

                formula.textContent =
                    `energia ${number(energyAmount, 2)} PLN + przesył ${number(distributionAmount, 2)} PLN = ${number(energyAmount + distributionAmount, 2)} PLN`;
            };

            manualEnergyToggle?.addEventListener("change", () => {
                if (!energyInput) return;
                energyInput.readOnly = !manualEnergyToggle.checked;

                if (!manualEnergyToggle.checked) {
                    resetRecommended(
                        energyInput,
                        energyGenerated,
                        item.recommendedRatePerUnit
                    );
                }

                energyInput.focus();
                syncFormula();
            });

            manualDistributionToggle?.addEventListener("change", () => {
                if (!distributionInput) return;
                distributionInput.readOnly = !manualDistributionToggle.checked;

                if (!manualDistributionToggle.checked) {
                    resetRecommended(
                        distributionInput,
                        distributionGenerated,
                        item.recommendedDistributionRatePerUnit
                    );
                }

                distributionInput.focus();
                syncFormula();
            });

            energyInput?.addEventListener("input", syncFormula);
            distributionInput?.addEventListener("input", syncFormula);
            syncFormula();

            form.addEventListener("submit", async event => {
                event.preventDefault();

                const submit = form.querySelector('button[type="submit"]');
                const body = new FormData(form);

                if (data.requestToken) {
                    body.append(
                        "__RequestVerificationToken",
                        data.requestToken
                    );
                }

                const currentEnergyRate = Number.parseFloat(
                    String(body.get("ratePerUnit") || "0").replace(",", ".")
                );

                const currentDistributionRate = Number.parseFloat(
                    String(body.get("distributionRatePerUnit") || "0").replace(",", ".")
                );

                const consumption = Number(item.consumption || 0);
                const energyAmount = energyGenerated
                    ? Number(item.generatedAmountMinor || 0) / 100
                    : consumption * currentEnergyRate;
                const distributionAmount = consumption * currentDistributionRate;

                if (!Number.isFinite(currentEnergyRate)
                    || currentEnergyRate <= 0
                    || !Number.isFinite(currentDistributionRate)
                    || currentDistributionRate <= 0) {
                    window.alert("Podaj poprawną stawkę energii oraz stawkę przesyłu.");
                    return;
                }

                const confirmation = energyGenerated
                    ? `Dodać brakujący przesył dla ${item.tenantName} za ${body.get("periodKey")}: ` +
                      `${number(consumption)} ${item.unitCode} × ${number(currentDistributionRate, 6)} PLN = ${number(distributionAmount, 2)} PLN? ` +
                      `Energia ${number(energyAmount, 2)} PLN pozostanie bez zmian.`
                    : `Dodać do rozliczenia ${item.tenantName} za ${body.get("periodKey")}: ` +
                      `energia ${number(energyAmount, 2)} PLN + przesył ${number(distributionAmount, 2)} PLN = ` +
                      `${number(energyAmount + distributionAmount, 2)} PLN?`;

                if (!window.confirm(confirmation)) {
                    return;
                }

                submit.disabled = true;

                try {
                    const response = await fetch(
                        `${endpoint}/Generate`,
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
                            || "Nie udało się dodać rozliczenia prądu z podlicznika."
                        );
                    }

                    window.alert(
                        result.message
                        || "Energia i przesył zostały dodane do rozliczenia lokatora."
                    );

                    window.location.reload();
                } catch (error) {
                    window.alert(
                        error.message
                        || "Nie udało się dodać rozliczenia prądu z podlicznika."
                    );

                    submit.disabled = false;
                }
            });
        }

        return card;
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

            if (!response.ok) {
                return;
            }

            const data = await response.json();

            if (!data.canManage) {
                return;
            }

            const items = Array.isArray(data.submeters)
                ? data.submeters
                : [];

            if (!items.length) {
                return;
            }

            host.innerHTML = "";
            items.forEach(item => {
                host.appendChild(
                    renderItem(item, data)
                );
            });

            section.hidden = false;
        } catch (error) {
            console.warn(
                "Nie udało się uruchomić rozliczania podliczników lokatorów.",
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
