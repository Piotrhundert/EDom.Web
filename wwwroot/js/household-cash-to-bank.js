(() => {
    const path = (window.location.pathname || "").toLowerCase();
    if (path !== "/householdfinance" && path !== "/householdfinance/") return;

    const endpoint = "/HouseholdFinance/CashToBank";
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

    const localDateTimeValue = () => {
        const now = new Date();
        const local = new Date(now.getTime() - now.getTimezoneOffset() * 60000);
        return local.toISOString().slice(0, 16);
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

    function render(data) {
        if (!data.canTransfer || document.getElementById("householdCashToBank")) return;

        const ledger = data.ledger || {};
        const history = Array.isArray(data.history) ? data.history : [];

        const section = document.createElement("section");
        section.id = "householdCashToBank";
        section.className = "panel hf-cash-bank-section";

        section.innerHTML = `
            <div class="hf-cash-bank-head">
                <div>
                    <span>Przesunięcie własnych środków</span>
                    <h2>Gotówka ↔ konto bankowe</h2>
                    <p>
                        Przenoś środki między kasą domową a kontem bankowym.
                        Operacja nie jest przychodem ani wydatkiem i nie zmienia łącznej wartości środków domu.
                    </p>
                </div>
            </div>

            <div class="hf-cash-bank-balances">
                <div>
                    <span>Gotówka</span>
                    <strong>${esc(money(ledger.cashBalanceMinor, ledger.currencyCode))}</strong>
                </div>

                <div class="hf-cash-bank-arrow">↔</div>

                <div>
                    <span>Konto bankowe</span>
                    <strong>${esc(money(ledger.bankBalanceMinor, ledger.currencyCode))}</strong>
                </div>

                <div>
                    <span>Łączne środki</span>
                    <strong>${esc(money(ledger.totalBalanceMinor, ledger.currencyCode))}</strong>
                    <small>Transfer wewnętrzny nie zmienia tej wartości.</small>
                </div>
            </div>

            <div class="hf-cash-bank-mode-actions">
                <button type="button"
                        class="btn btn-primary"
                        data-transfer-mode="CashToBank">
                    Wpłać gotówkę na konto
                </button>

                <button type="button"
                        class="btn btn-secondary"
                        data-transfer-mode="BankToCash">
                    Wypłać z konta do kasy
                </button>
            </div>

            <form data-cash-bank-form hidden>
                <input type="hidden"
                       name="direction"
                       value="CashToBank"
                       data-transfer-direction />

                <div class="hf-cash-bank-form-title">
                    <strong data-transfer-title>Wpłata gotówki na konto</strong>
                    <small data-transfer-description>
                        Gotówka zmniejszy się, a saldo bankowe wzrośnie o tę samą kwotę.
                    </small>
                </div>

                <div class="hf-cash-bank-grid">
                    <label data-transfer-amount-label>Kwota wpłacana na konto
                        <div class="hf-cash-bank-amount">
                            <input name="amount"
                                   type="number"
                                   min="0.01"
                                   step="0.01"
                                   required
                                   placeholder="0,00" />
                            <span>${esc(ledger.currencyCode || "PLN")}</span>
                        </div>
                    </label>

                    <label>Data i godzina operacji
                        <input name="transferredAtLocal"
                               type="datetime-local"
                               value="${localDateTimeValue()}"
                               required />
                    </label>

                    <label class="hf-cash-bank-full">Opis / uwagi
                        <input name="note"
                               maxlength="500"
                               placeholder="np. wpłata we wpłatomacie / wypłata gotówki z bankomatu" />
                    </label>
                </div>

                <div class="hf-cash-bank-preview" data-cash-bank-preview>
                    Wpisz kwotę, aby zobaczyć saldo po operacji.
                </div>

                <div class="hf-cash-bank-actions">
                    <button type="button"
                            class="btn btn-secondary"
                            data-cash-bank-cancel>
                        Anuluj
                    </button>

                    <button type="submit"
                            class="btn btn-success"
                            data-transfer-submit>
                        Zaksięguj przesunięcie
                    </button>
                </div>
            </form>

            <details class="hf-cash-bank-history" ${history.length ? "" : "hidden"}>
                <summary>Historia przesunięć gotówka ↔ bank (${history.length})</summary>

                <div>
                    ${history.map(item => {
                        const bankToCash = item.transferDirection === "BankToCash";
                        return `
                            <article>
                                <div>
                                    <strong>
                                        ${bankToCash ? "Bank → Kasa" : "Kasa → Bank"}
                                        · ${esc(money(item.amountMinor, item.currencyCode))}
                                    </strong>
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
                            </article>`;
                    }).join("")}
                </div>
            </details>
        `;

        const kpi = main.querySelector(".hf-kpi-grid");
        if (kpi) {
            const shell = kpi.closest(".hf-shell, .panel");
            (shell || kpi).insertAdjacentElement("afterend", section);
        } else {
            main.prepend(section);
        }

        const form = section.querySelector("[data-cash-bank-form]");
        const directionInput = section.querySelector("[data-transfer-direction]");
        const amount = section.querySelector('[name="amount"]');
        const preview = section.querySelector("[data-cash-bank-preview]");
        const title = section.querySelector("[data-transfer-title]");
        const description = section.querySelector("[data-transfer-description]");
        const amountLabel = section.querySelector("[data-transfer-amount-label]");
        const submit = section.querySelector("[data-transfer-submit]");

        const currentDirection = () => directionInput.value || "CashToBank";

        const syncMode = direction => {
            const bankToCash = direction === "BankToCash";
            directionInput.value = bankToCash ? "BankToCash" : "CashToBank";

            title.textContent = bankToCash
                ? "Wypłata z konta do kasy domowej"
                : "Wpłata gotówki na konto";

            description.textContent = bankToCash
                ? "Saldo bankowe zmniejszy się, a gotówka w kasie wzrośnie o tę samą kwotę."
                : "Gotówka zmniejszy się, a saldo bankowe wzrośnie o tę samą kwotę.";

            amountLabel.childNodes[0].textContent = bankToCash
                ? "Kwota wypłacana z konta "
                : "Kwota wpłacana na konto ";

            const sourceMinor = bankToCash
                ? Number(ledger.bankBalanceMinor || 0)
                : Number(ledger.cashBalanceMinor || 0);

            amount.max = Math.max(0, sourceMinor / 100).toFixed(2);
            amount.value = "";
            preview.textContent = "Wpisz kwotę, aby zobaczyć saldo po operacji.";
            submit.textContent = bankToCash
                ? "Wypłać do kasy"
                : "Wpłać na konto";

            section.querySelectorAll("[data-transfer-mode]").forEach(button => {
                button.classList.toggle(
                    "is-active",
                    button.dataset.transferMode === directionInput.value
                );
            });

            form.hidden = false;
            form.scrollIntoView({ behavior: "smooth", block: "nearest" });
        };

        const syncPreview = () => {
            const major = Number.parseFloat(String(amount?.value || "0").replace(",", "."));

            if (!Number.isFinite(major) || major <= 0) {
                preview.textContent = "Wpisz kwotę, aby zobaczyć saldo po operacji.";
                return;
            }

            const minor = Math.round(major * 100);
            const bankToCash = currentDirection() === "BankToCash";

            const cashAfter = Number(ledger.cashBalanceMinor || 0)
                + (bankToCash ? minor : -minor);

            const bankAfter = Number(ledger.bankBalanceMinor || 0)
                + (bankToCash ? -minor : minor);

            if (cashAfter < 0 || bankAfter < 0) {
                const available = bankToCash
                    ? ledger.bankBalanceMinor
                    : ledger.cashBalanceMinor;

                preview.innerHTML =
                    `<strong>Brak środków.</strong> Dostępne w źródle: ${esc(money(available, ledger.currencyCode))}.`;
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

        section.querySelectorAll("[data-transfer-mode]").forEach(button => {
            button.addEventListener("click", () => syncMode(button.dataset.transferMode));
        });

        amount?.addEventListener("input", syncPreview);

        section.querySelector("[data-cash-bank-cancel]")?.addEventListener("click", () => {
            form.hidden = true;
            section.querySelectorAll("[data-transfer-mode]").forEach(x => x.classList.remove("is-active"));
        });

        form?.addEventListener("submit", async event => {
            event.preventDefault();

            const major = Number.parseFloat(
                String(form.querySelector('[name="amount"]')?.value || "0").replace(",", ".")
            );

            if (!Number.isFinite(major) || major <= 0) {
                window.alert("Podaj kwotę większą od 0.");
                return;
            }

            const bankToCash = currentDirection() === "BankToCash";
            const availableMajor = (
                bankToCash
                    ? Number(ledger.bankBalanceMinor || 0)
                    : Number(ledger.cashBalanceMinor || 0)
            ) / 100;

            if (major > availableMajor) {
                window.alert(
                    `Nie możesz przenieść więcej niż jest dostępne. Dostępne: ${availableMajor.toFixed(2)} ${ledger.currencyCode || "PLN"}.`
                );
                return;
            }

            const confirmText = bankToCash
                ? `Wypłacić ${major.toFixed(2)} ${ledger.currencyCode || "PLN"} z konta bankowego do kasy domowej?`
                : `Wpłacić ${major.toFixed(2)} ${ledger.currencyCode || "PLN"} z kasy domowej na konto bankowe?`;

            if (!window.confirm(`${confirmText} Łączna wartość środków pozostanie bez zmian.`)) return;

            const submitButton = form.querySelector('button[type="submit"]');
            submitButton.disabled = true;

            try {
                const body = new FormData(form);

                if (data.requestToken) {
                    body.append("__RequestVerificationToken", data.requestToken);
                }

                const response = await fetch(`${endpoint}/Transfer`, {
                    method: "POST",
                    body,
                    credentials: "same-origin"
                });

                const result = await response.json().catch(() => ({}));

                if (!response.ok) {
                    throw new Error(result.message || "Nie udało się wykonać przesunięcia środków.");
                }

                window.alert(result.message || "Przesunięcie zostało zaksięgowane.");
                window.location.reload();
            } catch (error) {
                window.alert(error.message || "Nie udało się wykonać przesunięcia środków.");
                submitButton.disabled = false;
            }
        });
    }

    async function load() {
        try {
            const response = await fetch(`${endpoint}/Data`, {
                credentials: "same-origin",
                headers: { "Accept": "application/json" }
            });

            if (!response.ok) return;
            render(await response.json());
        } catch (error) {
            console.warn("Nie udało się uruchomić transferów gotówka ↔ bank.", error);
        }
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", load);
    } else {
        load();
    }
})();
