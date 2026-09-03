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

    const mediumLabel = value => ({
        Electricity: "Prąd",
        Water: "Woda",
        Gas: "Gaz",
        Heating: "Ogrzewanie"
    }[value] || value || "Inne");

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

        const rateValue =
            Number(item.recommendedRatePerUnit || 0) > 0
                ? Number(item.recommendedRatePerUnit).toFixed(6)
                : "";

        card.innerHTML = `
            <div class="submeter-tenant-head">
                <div>
                    <span>${esc(mediumLabel(item.medium))} · podlicznik</span>
                    <strong>${esc(item.meterName)}</strong>
                    <small>
                        ${esc(item.roomName)}
                        ${item.tenantName ? `· lokator: ${esc(item.tenantName)}` : ""}
                    </small>
                </div>

                ${item.alreadyGenerated
                    ? `<b class="submeter-state is-done">Już rozliczono</b>`
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
                            <span>Zużycie do rozliczenia</span>
                            <strong>${esc(number(item.consumption))} ${esc(item.unitCode)}</strong>
                            <small>strefa ${esc(item.zoneCode || "ALL")}</small>
                        </div>
                    </div>`
                : ""}

            ${item.alreadyGenerated
                ? `
                    <div class="submeter-generated-info">
                        <strong>Ten odczyt został już przekazany do rozliczenia lokatora.</strong>
                        <span>${Number(item.generatedAmountMinor || 0) > 0
                            ? `Kwota zapisana: ${esc(money(item.generatedAmountMinor))}.`
                            : "Nie można wygenerować go drugi raz."}</span>
                    </div>`
                : item.canGenerate
                    ? `
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

                                <label>
                                    <span>Stawka za 1 ${esc(item.unitCode)}</span>
                                    <div class="submeter-rate-input">
                                        <input name="ratePerUnit"
                                               type="number"
                                               step="0.000001"
                                               min="0.000001"
                                               value="${esc(rateValue)}"
                                               placeholder="np. 1,250000"
                                               required />
                                        <b>PLN/${esc(item.unitCode)}</b>
                                    </div>
                                    <small>
                                        ${rateValue
                                            ? `Propozycja z taryfy: ${esc(number(item.recommendedRatePerUnit, 6))} PLN/${esc(item.unitCode)} · ${esc(item.rateSource || "")}`
                                            : "Nie znaleziono jednoznacznej aktywnej stawki — wpisz stawkę ręcznie."}
                                    </small>
                                </label>

                                <div class="submeter-form-summary">
                                    <span>Wyliczenie</span>
                                    <strong data-submeter-formula>
                                        ${esc(number(item.consumption))} ${esc(item.unitCode)} × stawka
                                    </strong>
                                </div>
                            </div>

                            <div class="submeter-actions">
                                <button type="submit"
                                        class="btn btn-primary btn-sm">
                                    Wylicz i dodaj do rozliczenia lokatora
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
            const rateInput = form.querySelector('[name="ratePerUnit"]');
            const formula = form.querySelector("[data-submeter-formula]");

            const syncFormula = () => {
                const rate = Number.parseFloat(
                    String(rateInput?.value || "0").replace(",", ".")
                );

                if (!Number.isFinite(rate) || rate <= 0) {
                    formula.textContent =
                        `${number(item.consumption)} ${item.unitCode} × stawka`;
                    return;
                }

                const amount =
                    Number(item.consumption || 0)
                    * rate;

                formula.textContent =
                    `${number(item.consumption)} ${item.unitCode} × ${number(rate, 6)} PLN = ${number(amount, 2)} PLN`;
            };

            rateInput?.addEventListener("input", syncFormula);
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

                const rate =
                    Number.parseFloat(
                        String(body.get("ratePerUnit") || "0")
                            .replace(",", ".")
                    );

                const amount =
                    Number(item.consumption || 0)
                    * rate;

                if (!window.confirm(
                    `Dodać do rozliczenia ${item.tenantName} za ${body.get("periodKey")}: ` +
                    `${number(item.consumption)} ${item.unitCode} × ${number(rate, 6)} PLN = ${number(amount, 2)} PLN?`
                )) {
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
                            || "Nie udało się dodać kosztu podlicznika do rozliczenia."
                        );
                    }

                    window.alert(
                        result.message
                        || "Koszt podlicznika został dodany do rozliczenia lokatora."
                    );

                    window.location.reload();
                } catch (error) {
                    window.alert(
                        error.message
                        || "Nie udało się dodać kosztu podlicznika do rozliczenia."
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
