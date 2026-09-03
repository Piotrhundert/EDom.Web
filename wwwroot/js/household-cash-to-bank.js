(() => {
    const path =
        (window.location.pathname || "")
            .toLowerCase();

    if (path !== "/householdfinance"
        && path !== "/householdfinance/") {
        return;
    }

    const endpoint =
        "/HouseholdFinance/CashToBank";

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
                }
            ).format(
                new Date(value)
            );
        } catch {
            return String(value);
        }
    };

    function render(data) {
        if (!data.canTransfer) {
            return;
        }

        if (document.getElementById(
                "householdCashToBank")) {
            return;
        }

        const ledger =
            data.ledger || {};

        const history =
            Array.isArray(data.history)
                ? data.history
                : [];

        const section =
            document.createElement(
                "section");

        section.id =
            "householdCashToBank";

        section.className =
            "panel hf-cash-bank-section";

        section.innerHTML = `
            <div class="hf-cash-bank-head">
                <div>
                    <span>Przesunięcie własnych środków</span>
                    <h2>Wpłać gotówkę na konto bankowe</h2>
                    <p>
                        Ta operacja nie tworzy przychodu. Zmniejsza kasę domową
                        i zwiększa saldo bankowe o dokładnie tę samą kwotę.
                    </p>
                </div>

                <button type="button"
                        class="btn btn-primary"
                        data-cash-bank-toggle>
                    Wpłać gotówkę na konto
                </button>
            </div>

            <div class="hf-cash-bank-balances">
                <div>
                    <span>Gotówka przed operacją</span>
                    <strong>${esc(money(ledger.cashBalanceMinor, ledger.currencyCode))}</strong>
                </div>

                <div class="hf-cash-bank-arrow">→</div>

                <div>
                    <span>Saldo bankowe</span>
                    <strong>${esc(money(ledger.bankBalanceMinor, ledger.currencyCode))}</strong>
                </div>

                <div>
                    <span>Łączne środki</span>
                    <strong>${esc(money(ledger.totalBalanceMinor, ledger.currencyCode))}</strong>
                    <small>Ta wartość nie zmienia się przy przesunięciu.</small>
                </div>
            </div>

            <form data-cash-bank-form
                  hidden>
                <div class="hf-cash-bank-grid">
                    <label>Kwota wpłacana na konto
                        <div class="hf-cash-bank-amount">
                            <input name="amount"
                                   type="number"
                                   min="0.01"
                                   max="${Math.max(0, Number(ledger.cashBalanceMinor || 0) / 100).toFixed(2)}"
                                   step="0.01"
                                   required
                                   placeholder="0,00" />
                            <span>${esc(ledger.currencyCode || "PLN")}</span>
                        </div>
                    </label>

                    <label>Data i godzina wpłaty
                        <input name="transferredAtLocal"
                               type="datetime-local"
                               value="${localDateTimeValue()}"
                               required />
                    </label>

                    <label class="hf-cash-bank-full">Opis / uwagi
                        <input name="note"
                               maxlength="500"
                               placeholder="np. wpłata gotówki z kasy domu do bankomatu wpłatowego" />
                    </label>
                </div>

                <div class="hf-cash-bank-preview"
                     data-cash-bank-preview>
                    Wpisz kwotę, aby zobaczyć saldo po operacji.
                </div>

                <div class="hf-cash-bank-actions">
                    <button type="button"
                            class="btn btn-secondary"
                            data-cash-bank-cancel>
                        Anuluj
                    </button>

                    <button type="submit"
                            class="btn btn-success">
                        Zaksięguj przesunięcie
                    </button>
                </div>
            </form>

            <details class="hf-cash-bank-history"
                     ${history.length ? "" : "hidden"}>
                <summary>Historia wpłat gotówki na konto (${history.length})</summary>

                <div>
                    ${history.map(item => `
                        <article>
                            <div>
                                <strong>+${esc(money(item.amountMinor, item.currencyCode))} na bank</strong>
                                <small>${esc(dateTime(item.transferredAtUtc))}</small>
                            </div>

                            <div>
                                <span>Kasa</span>
                                <strong>
                                    ${esc(money(item.cashBeforeMinor, item.currencyCode))}
                                    → ${esc(money(item.cashAfterMinor, item.currencyCode))}
                                </strong>
                            </div>

                            <div>
                                <span>Bank</span>
                                <strong>
                                    ${esc(money(item.bankBeforeMinor, item.currencyCode))}
                                    → ${esc(money(item.bankAfterMinor, item.currencyCode))}
                                </strong>
                            </div>

                            <div>
                                <span>Opis</span>
                                <strong>${esc(item.note || "—")}</strong>
                            </div>
                        </article>
                    `).join("")}
                </div>
            </details>
        `;

        const kpi =
            main.querySelector(
                ".hf-kpi-grid");

        if (kpi) {
            const shell =
                kpi.closest(
                    ".hf-shell, .panel");

            if (shell) {
                shell.insertAdjacentElement(
                    "afterend",
                    section);
            } else {
                kpi.insertAdjacentElement(
                    "afterend",
                    section);
            }
        } else {
            main.prepend(
                section);
        }

        const toggle =
            section.querySelector(
                "[data-cash-bank-toggle]");

        const form =
            section.querySelector(
                "[data-cash-bank-form]");

        const cancel =
            section.querySelector(
                "[data-cash-bank-cancel]");

        const amount =
            section.querySelector(
                '[name="amount"]');

        const preview =
            section.querySelector(
                "[data-cash-bank-preview]");

        const syncPreview = () => {
            const major =
                Number.parseFloat(
                    String(
                        amount?.value || "0"
                    ).replace(",", ".")
                );

            if (!Number.isFinite(major)
                || major <= 0) {
                preview.textContent =
                    "Wpisz kwotę, aby zobaczyć saldo po operacji.";
                return;
            }

            const minor =
                Math.round(
                    major * 100);

            const cashAfter =
                Number(
                    ledger.cashBalanceMinor || 0)
                - minor;

            const bankAfter =
                Number(
                    ledger.bankBalanceMinor || 0)
                + minor;

            if (cashAfter < 0) {
                preview.innerHTML =
                    `<strong>Brak środków.</strong> W kasie jest tylko ${esc(money(ledger.cashBalanceMinor, ledger.currencyCode))}.`;
                return;
            }

            preview.innerHTML = `
                Po operacji:
                <strong>Kasa ${esc(money(cashAfter, ledger.currencyCode))}</strong>
                ·
                <strong>Bank ${esc(money(bankAfter, ledger.currencyCode))}</strong>
                ·
                Łącznie bez zmian:
                <strong>${esc(money(ledger.totalBalanceMinor, ledger.currencyCode))}</strong>
            `;
        };

        amount?.addEventListener(
            "input",
            syncPreview);

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

                const raw =
                    currentForm.querySelector(
                        '[name="amount"]'
                    )?.value || "0";

                const major =
                    Number.parseFloat(
                        String(raw)
                            .replace(",", ".")
                    );

                const availableMajor =
                    Number(
                        ledger.cashBalanceMinor || 0)
                    / 100;

                if (!Number.isFinite(major)
                    || major <= 0) {
                    window.alert(
                        "Podaj kwotę większą od 0.");
                    return;
                }

                if (major > availableMajor) {
                    window.alert(
                        `Nie możesz wpłacić więcej niż znajduje się w kasie. Dostępne: ${availableMajor.toFixed(2)} ${ledger.currencyCode || "PLN"}.`
                    );
                    return;
                }

                if (!window.confirm(
                    `Przenieść ${major.toFixed(2)} ${ledger.currencyCode || "PLN"} z kasy domowej na konto bankowe? Łączna wartość środków gospodarstwa pozostanie bez zmian.`
                )) {
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
                            `${endpoint}/Transfer`,
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
                            || "Nie udało się przenieść gotówki na konto."
                        );
                    }

                    window.alert(
                        result.message
                        || "Gotówka została przeniesiona na konto."
                    );

                    window.location.reload();
                } catch (error) {
                    window.alert(
                        error.message
                        || "Nie udało się przenieść gotówki na konto."
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

            render(
                await response.json());
        } catch (error) {
            console.warn(
                "Nie udało się uruchomić przesunięcia gotówki na konto.",
                error);
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
