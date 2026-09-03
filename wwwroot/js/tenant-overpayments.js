(() => {
    const path = (window.location.pathname || "").toLowerCase();
    if (path !== "/rental/settlements" && !path.startsWith("/rental/settlements/")) return;

    const endpoint = "/Rental/Overpayments";
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

    function findSettlementCard(item) {
        const expected = `${String(item.tenantName || "").trim()} — ${String(item.periodKey || "").trim()}`.toLowerCase();
        const headings = Array.from(main.querySelectorAll(".card-header strong, .card strong"));

        const heading = headings.find(x =>
            String(x.textContent || "").trim().toLowerCase() === expected);

        return heading?.closest(".card") || null;
    }

    function decisionLabel(decision) {
        if (decision === "CarryForward") return "Do kolejnego miesiąca";
        if (decision === "Refunded") return "Zwrócona lokatorowi";
        return decision || "Decyzja";
    }

    function renderItem(item, canManage) {
        const card = findSettlementCard(item);
        if (!card) return;

        let host = card.querySelector("[data-tenant-overpayment-host]");
        if (!host) {
            host = document.createElement("section");
            host.dataset.tenantOverpaymentHost = "true";
            host.className = "tenant-overpayment-host";

            const body = card.querySelector(".card-body") || card;
            const submissionsHeading = Array.from(body.querySelectorAll("h2,h3,h4,strong"))
                .find(x => String(x.textContent || "").trim().toLowerCase() === "zgłoszenia wpłat");

            if (submissionsHeading) {
                submissionsHeading.parentElement?.insertBefore(host, submissionsHeading);
            } else {
                body.appendChild(host);
            }
        }

        const decisions = Array.isArray(item.decisions) ? item.decisions : [];
        const hasHistory = decisions.length > 0;

        if (Number(item.overpaymentMinor) <= 0 && !hasHistory) {
            host.remove();
            return;
        }
        const decisionHistory = decisions.length
            ? `<div class="tenant-overpayment-history">
                <strong>Historia nadpłaty</strong>
                ${decisions.map(d => `
                    <span>
                        ${esc(decisionLabel(d.decision))}
                        · ${esc(money(d.amountMinor, d.currencyCode || item.currencyCode))}
                        ${d.refundedOn ? ` · ${esc(d.refundedOn)}` : ""}
                        ${Number(d.appliedMinor || 0) > 0 ? ` · wykorzystano ${esc(money(d.appliedMinor, d.currencyCode || item.currencyCode))}` : ""}
                    </span>`).join("")}
               </div>`
            : "";

        const available = Number(item.availableMinor || 0);
        const actions = canManage && available > 0
            ? `<div class="tenant-overpayment-actions">
                <button type="button"
                        class="btn btn-primary btn-sm"
                        data-overpayment-carry="${esc(item.settlementId)}">
                    Zostaw na następny miesiąc
                </button>
                <button type="button"
                        class="btn btn-secondary btn-sm"
                        data-overpayment-refund-toggle="${esc(item.settlementId)}">
                    Zwróć lokatorowi
                </button>
                <div class="tenant-overpayment-refund-form"
                     data-overpayment-refund-form="${esc(item.settlementId)}"
                     hidden>
                    <label>Forma zwrotu
                        <select data-refund-method>
                            <option value="Bank">Przelew</option>
                            <option value="Cash">Gotówka</option>
                        </select>
                    </label>
                    <label>Data zwrotu
                        <input type="date" data-refund-date value="${new Date().toISOString().slice(0, 10)}" />
                    </label>
                    <label>Uwagi
                        <input data-refund-note placeholder="np. zwrot nadpłaty za ${esc(item.periodKey)}" />
                    </label>
                    <button type="button"
                            class="btn btn-danger btn-sm"
                            data-overpayment-refund="${esc(item.settlementId)}">
                        Zarejestruj zwrot ${esc(money(available, item.currencyCode))}
                    </button>
                </div>
              </div>`
            : "";

        host.innerHTML = `
            <div class="tenant-overpayment-panel">
                <div class="tenant-overpayment-title">
                    <div>
                        <span>${available > 0 ? "Nadpłata lokatora" : "Historia nadpłaty / rozliczenie wpłat"}</span>
                        <strong>
                            ${available > 0
                                ? esc(money(item.overpaymentMinor, item.currencyCode))
                                : (Number(item.remainingAfterDecisionsMinor || 0) > 0
                                    ? `Do dopłaty ${esc(money(item.remainingAfterDecisionsMinor, item.currencyCode))}`
                                    : "Brak aktywnej nadpłaty")}
                        </strong>
                    </div>
                    <b>
                        ${available > 0
                            ? "Wymaga decyzji"
                            : (Number(item.remainingAfterDecisionsMinor || 0) > 0
                                ? "Do dopłaty"
                                : "Rozliczona historycznie")}
                    </b>
                </div>

                <div class="tenant-overpayment-kpis">
                    <div>
                        <span>Należność po przeliczeniu</span>
                        <strong>${esc(money(item.totalDueMinor, item.currencyCode))}</strong>
                    </div>
                    <div>
                        <span>Wpłaty zatwierdzone brutto</span>
                        <strong>${esc(money(item.grossApprovedTotalMinor ?? item.approvedTotalMinor, item.currencyCode))}</strong>
                    </div>
                    <div>
                        <span>Zwrócono / przeniesiono</span>
                        <strong>-${esc(money(item.disposedMinor || 0, item.currencyCode))}</strong>
                    </div>
                    <div>
                        <span>Efektywnie zaliczone wpłaty</span>
                        <strong>${esc(money(item.approvedTotalMinor, item.currencyCode))}</strong>
                    </div>
                    <div>
                        <span>Aktywna nadpłata</span>
                        <strong>${esc(money(item.overpaymentMinor, item.currencyCode))}</strong>
                    </div>
                    <div>
                        <span>Do dopłaty</span>
                        <strong>${esc(money(item.remainingAfterDecisionsMinor || 0, item.currencyCode))}</strong>
                    </div>
                </div>

                <p>
                    Kwoty już zwrócone lokatorowi lub przeniesione na inne miesiące nie są ponownie zaliczane do tego rozliczenia.
                    Historia pozostaje widoczna dla audytu.
                </p>

                ${actions}
                ${decisionHistory}
            </div>`;

        bindHost(host, item);
    }

    function bindHost(host, item) {
        host.querySelector("[data-overpayment-carry]")?.addEventListener("click", async buttonEvent => {
            const button = buttonEvent.currentTarget;

            if (!window.confirm(
                `Przenieść ${money(item.availableMinor, item.currencyCode)} nadpłaty na kolejne rozliczenia tego lokatora?`
            )) return;

            button.disabled = true;
            try {
                const body = new FormData();
                body.append("settlementId", item.settlementId);
                body.append("note", `Przeniesienie nadpłaty z okresu ${item.periodKey}`);
                if (token) body.append("__RequestVerificationToken", token);

                const response = await fetch(`${endpoint}/CarryForward`, {
                    method: "POST",
                    body,
                    credentials: "same-origin"
                });

                const result = await response.json().catch(() => ({}));
                if (!response.ok) throw new Error(result.message || "Nie udało się przenieść nadpłaty.");

                window.alert(result.message || "Nadpłata została przeniesiona.");
                window.location.reload();
            } catch (error) {
                window.alert(error.message || "Nie udało się przenieść nadpłaty.");
                button.disabled = false;
            }
        });

        host.querySelector("[data-overpayment-refund-toggle]")?.addEventListener("click", event => {
            const id = event.currentTarget.dataset.overpaymentRefundToggle;
            const form = host.querySelector(`[data-overpayment-refund-form="${CSS.escape(id)}"]`);
            if (form) form.hidden = !form.hidden;
        });

        host.querySelector("[data-overpayment-refund]")?.addEventListener("click", async event => {
            const button = event.currentTarget;
            const form = button.closest("[data-overpayment-refund-form]");
            if (!form) return;

            const method = form.querySelector("[data-refund-method]")?.value || "Bank";
            const refundedOn = form.querySelector("[data-refund-date]")?.value || "";
            const note = form.querySelector("[data-refund-note]")?.value || "";

            if (!refundedOn) {
                window.alert("Podaj datę zwrotu.");
                return;
            }

            if (!window.confirm(
                `Zarejestrować zwrot ${money(item.availableMinor, item.currencyCode)} lokatorowi?`
            )) return;

            button.disabled = true;

            try {
                const body = new FormData();
                body.append("settlementId", item.settlementId);
                body.append("refundMethod", method);
                body.append("refundedOn", refundedOn);
                body.append("note", note);
                if (token) body.append("__RequestVerificationToken", token);

                const response = await fetch(`${endpoint}/Refund`, {
                    method: "POST",
                    body,
                    credentials: "same-origin"
                });

                const result = await response.json().catch(() => ({}));
                if (!response.ok) throw new Error(result.message || "Nie udało się zapisać zwrotu.");

                window.alert(result.message || "Zwrot został zapisany.");
                window.location.reload();
            } catch (error) {
                window.alert(error.message || "Nie udało się zapisać zwrotu.");
                button.disabled = false;
            }
        });
    }

    function redirectBuildForm() {
        document.querySelectorAll("form").forEach(form => {
            const action = String(form.getAttribute("action") || "").toLowerCase();
            if (action.endsWith("/rental/settlements/build")) {
                form.setAttribute("action", `${endpoint}/Build`);
            }
        });
    }

    async function syncCarryForward() {
        if (!token) return;

        try {
            const body = new FormData();
            body.append("__RequestVerificationToken", token);

            await fetch(`${endpoint}/Sync`, {
                method: "POST",
                body,
                credentials: "same-origin"
            });
        } catch {
            // Synchronizacja jest pomocnicza; błąd nie blokuje pozostałego widoku.
        }
    }

    async function load() {
        redirectBuildForm();
        await syncCarryForward();

        try {
            const response = await fetch(`${endpoint}/Data`, {
                credentials: "same-origin",
                headers: { "Accept": "application/json" }
            });

            if (!response.ok) return;

            const data = await response.json();
            (data.settlements || []).forEach(item => renderItem(item, Boolean(data.canManage)));
        } catch (error) {
            console.warn("Nie udało się wczytać nadpłat lokatorów.", error);
        }
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", load);
    } else {
        load();
    }
})();
