(() => {
    const path =
        (window.location.pathname || "")
            .toLowerCase();

    if (path !== "/rental/settlements"
        && !path.startsWith(
            "/rental/settlements/")) {
        return;
    }

    const endpoint =
        "/Rental/AdminDirectPayments";

    const main =
        document.querySelector(
            "main, #mainContent")
        || document.body;

    const esc = value =>
        String(value ?? "")
            .replaceAll("&", "&amp;")
            .replaceAll("<", "&lt;")
            .replaceAll(">", "&gt;")
            .replaceAll('"', "&quot;")
            .replaceAll("'", "&#39;");

    const money = (
        minor,
        currency
    ) => {
        try {
            return new Intl.NumberFormat(
                "pl-PL",
                {
                    style: "currency",
                    currency:
                        currency || "PLN"
                }
            ).format(
                (Number(minor) || 0) / 100
            );
        } catch {
            return `${
                (
                    (Number(minor) || 0)
                    / 100
                ).toFixed(2)
            } ${currency || "PLN"}`;
        }
    };

    const localDateTimeValue = () => {
        const now =
            new Date();

        const local =
            new Date(
                now.getTime()
                - now.getTimezoneOffset()
                  * 60000
            );

        return local
            .toISOString()
            .slice(0, 16);
    };

    function findCard(item) {
        const expected =
            `${
                String(
                    item.tenantName || ""
                ).trim()
            } — ${
                String(
                    item.periodKey || ""
                ).trim()
            }`.toLowerCase();

        const heading =
            Array.from(
                main.querySelectorAll(
                    ".card-header strong, .card strong"
                )
            ).find(x =>
                String(
                    x.textContent || ""
                )
                    .trim()
                    .toLowerCase()
                === expected
            );

        return heading
            ?.closest(".card")
            || null;
    }

    function render(
        item,
        data
    ) {
        const card =
            findCard(item);

        if (!card) {
            return;
        }

        if (card.querySelector(
                "[data-admin-direct-payment]")) {
            return;
        }

        const body =
            card.querySelector(
                ".card-body")
            || card;

        const panel =
            document.createElement(
                "section");

        panel.dataset.adminDirectPayment =
            "true";

        panel.className =
            "admin-direct-payment";

        if (!item.canAccept) {
            panel.classList.add(
                "is-disabled");

            panel.innerHTML = `
                <div>
                    <span>Wpłata przyjmowana przez administratora</span>
                    <strong>Opcja niedostępna na tym etapie</strong>
                    <small>${esc(item.paymentHint || "")}</small>
                </div>`;

            body.appendChild(panel);
            return;
        }

        const suggestedMinor =
            Number(item.remainingMinor || 0);

        const suggestedMajor =
            suggestedMinor > 0
                ? (
                    suggestedMinor / 100
                ).toFixed(2)
                : "";

        panel.innerHTML = `
            <div class="admin-direct-payment-head">
                <div>
                    <span>Wpłata przyjmowana przez administratora</span>
                    <strong>Przyjmij i od razu zaksięguj wpłatę</strong>
                    <small>
                        ${esc(item.paymentHint || "")}
                        Gotówka zwiększy kasę domową, a przelew saldo rachunku domu.
                    </small>
                </div>

                <button type="button"
                        class="btn btn-secondary btn-sm"
                        data-admin-direct-toggle>
                    Przyjmij wpłatę
                </button>
            </div>

            <form data-admin-direct-form
                  hidden>
                <input type="hidden"
                       name="settlementId"
                       value="${esc(item.settlementId)}" />

                <input type="hidden"
                       name="currencyCode"
                       value="${esc(item.currencyCode || "PLN")}" />

                <div class="admin-direct-payment-grid">
                    <label>Kwota
                        <div class="admin-direct-payment-amount">
                            <input name="amount"
                                   type="number"
                                   min="0.01"
                                   step="0.01"
                                   value="${suggestedMajor}"
                                   placeholder="0,00"
                                   required />
                            <span>${esc(item.currencyCode || "PLN")}</span>
                        </div>
                    </label>

                    <label>Forma wpłaty
                        <select name="paymentMethod"
                                data-admin-payment-method>
                            <option value="Cash">Gotówka</option>
                            <option value="Bank">Przelew</option>
                        </select>
                    </label>

                    <label>Data i godzina wpłaty
                        <input name="paidAtLocal"
                               type="datetime-local"
                               value="${localDateTimeValue()}"
                               required />
                    </label>

                    <label class="admin-direct-payment-full">
                        Uwagi
                        <input name="note"
                               placeholder="np. wpłata przyjęta osobiście od lokatora" />
                    </label>
                </div>

                <div class="admin-direct-payment-info"
                     data-admin-payment-info>
                    Wpłata zostanie od razu zatwierdzona i zaksięgowana w kasie domowej.
                </div>

                <div class="admin-direct-payment-actions">
                    <button type="button"
                            class="btn btn-secondary btn-sm"
                            data-admin-direct-cancel>
                        Anuluj
                    </button>

                    <button type="submit"
                            class="btn btn-success btn-sm">
                        Przyjmij i zaksięguj
                    </button>
                </div>
            </form>`;

        // Wstawiamy przed tabelą zgłoszeń wpłat, jeżeli taka istnieje.
        const submissionsHeading =
            Array.from(
                body.querySelectorAll(
                    "h2,h3,h4,strong")
            ).find(x =>
                String(
                    x.textContent || "")
                    .trim()
                    .toLowerCase()
                === "zgłoszenia wpłat"
            );

        if (submissionsHeading) {
            submissionsHeading
                .parentElement
                ?.insertBefore(
                    panel,
                    submissionsHeading
                );
        } else {
            body.appendChild(
                panel);
        }

        const form =
            panel.querySelector(
                "[data-admin-direct-form]");

        const toggle =
            panel.querySelector(
                "[data-admin-direct-toggle]");

        const cancel =
            panel.querySelector(
                "[data-admin-direct-cancel]");

        const method =
            panel.querySelector(
                "[data-admin-payment-method]");

        const info =
            panel.querySelector(
                "[data-admin-payment-info]");

        const syncMethodInfo = () => {
            if (!info) {
                return;
            }

            info.textContent =
                method?.value === "Bank"
                    ? "Wpłata zostanie od razu zatwierdzona i zaksięgowana na rachunku bankowym domu."
                    : "Wpłata zostanie od razu zatwierdzona i zaksięgowana w kasie domowej.";
        };

        method?.addEventListener(
            "change",
            syncMethodInfo);

        syncMethodInfo();

        toggle?.addEventListener(
            "click",
            () => {
                form.hidden =
                    !form.hidden;

                toggle.classList.toggle(
                    "is-open",
                    !form.hidden);
            });

        cancel?.addEventListener(
            "click",
            () => {
                form.hidden = true;

                toggle?.classList.remove(
                    "is-open");
            });

        form?.addEventListener(
            "submit",
            async event => {
                event.preventDefault();

                const currentForm =
                    event.currentTarget;

                const amountInput =
                    currentForm.querySelector(
                        '[name="amount"]');

                const amount =
                    Number.parseFloat(
                        String(
                            amountInput?.value
                            || "0"
                        ).replace(",", ".")
                    );

                if (!Number.isFinite(amount)
                    || amount <= 0) {
                    window.alert(
                        "Podaj kwotę większą od 0.");
                    return;
                }

                const remainingMajor =
                    Number(item.remainingMinor || 0)
                    / 100;

                let confirmText =
                    `Przyjąć ${amount.toFixed(2)} ${item.currencyCode || "PLN"} od ${item.tenantName} i od razu zaksięgować tę wpłatę?`;

                if (remainingMajor > 0
                    && amount > remainingMajor) {
                    confirmText +=
                        ` Kwota jest większa od pozostałej należności o ${(amount - remainingMajor).toFixed(2)} ${item.currencyCode || "PLN"} — nadwyżka zostanie nadpłatą lokatora.`;
                }

                if (remainingMajor <= 0) {
                    confirmText +=
                        " Rozliczenie jest już opłacone, więc cała wpłata stanie się nadpłatą.";
                }

                if (!window.confirm(
                    confirmText)) {
                    return;
                }

                const submit =
                    currentForm.querySelector(
                        'button[type="submit"]');

                submit.disabled = true;

                try {
                    const body =
                        new FormData(
                            currentForm);

                    if (data.requestToken) {
                        body.append(
                            "__RequestVerificationToken",
                            data.requestToken);
                    }

                    const response =
                        await fetch(
                            `${endpoint}/Receive`,
                            {
                                method: "POST",
                                body,
                                credentials:
                                    "same-origin"
                            }
                        );

                    const result =
                        await response
                            .json()
                            .catch(
                                () => ({})
                            );

                    if (!response.ok) {
                        throw new Error(
                            result.message
                            || "Nie udało się zaksięgować wpłaty."
                        );
                    }

                    window.alert(
                        result.message
                        || "Wpłata została zaksięgowana."
                    );

                    window.location.reload();
                } catch (error) {
                    window.alert(
                        error.message
                        || "Nie udało się zaksięgować wpłaty."
                    );

                    submit.disabled = false;
                }
            });
    }

    async function load() {
        try {
            const response =
                await fetch(
                    `${endpoint}/Data`,
                    {
                        credentials:
                            "same-origin",
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

            if (!data.canManage) {
                return;
            }

            (
                data.settlements || []
            ).forEach(
                item =>
                    render(
                        item,
                        data)
            );
        } catch (error) {
            console.warn(
                "Nie udało się uruchomić bezpośredniego przyjmowania wpłat przez administratora.",
                error
            );
        }
    }

    if (document.readyState
        === "loading") {
        document.addEventListener(
            "DOMContentLoaded",
            load);
    } else {
        load();
    }
})();
