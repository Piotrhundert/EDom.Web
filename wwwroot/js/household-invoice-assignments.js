(() => {
    const path = (window.location.pathname || "").toLowerCase();
    if (!path.includes("/householdfinance")) return;

    const endpoint = "/HouseholdInvoiceAssignments";
    const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value || "";

    const esc = value => String(value ?? "").replace(/[&<>"']/g, char => ({
        "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#039;"
    })[char]);

    const money = (minor, currency) => new Intl.NumberFormat("pl-PL", {
        style: "currency",
        currency: currency || "PLN"
    }).format((Number(minor) || 0) / 100);

    const date = value => {
        if (!value) return "—";
        const raw = String(value).slice(0, 10);
        const parts = raw.split("-");
        return parts.length === 3 ? `${parts[2]}.${parts[1]}.${parts[0]}` : raw;
    };

    const dateTime = value => {
        if (!value) return "—";
        const parsed = new Date(value);
        if (Number.isNaN(parsed.getTime())) return date(value);
        return new Intl.DateTimeFormat("pl-PL", {
            year: "numeric",
            month: "2-digit",
            day: "2-digit",
            hour: "2-digit",
            minute: "2-digit"
        }).format(parsed);
    };

    const statusText = status => ({
        Assigned: "Przekazana do opłacenia",
        Submitted: "Oczekuje na zatwierdzenie administratora",
        Approved: "Zatwierdzona i zaksięgowana",
        Cancelled: "Anulowane",
        Reassigned: "Przekazano innej osobie"
    })[status] || status || "—";

    const statusClass = status => ({
        Assigned: "hf-assign-status-warn",
        Submitted: "hf-assign-status-info",
        Approved: "hf-assign-status-success",
        Cancelled: "hf-assign-status-muted",
        Reassigned: "hf-assign-status-muted"
    })[status] || "hf-assign-status-muted";

    const settlementText = value => ({
        Refund: "Zwrot środków",
        Compensation: "Kompensata obowiązkowej wpłaty"
    })[value] || "—";

    function assignmentEvents(assignments) {
        return [...(assignments || [])].sort((a, b) =>
            String(a.assignedAtUtc || "").localeCompare(String(b.assignedAtUtc || ""))
        );
    }

    function latestAssignment(assignments) {
        return [...(assignments || [])].sort((a, b) =>
            String(b.assignedAtUtc || "").localeCompare(String(a.assignedAtUtc || ""))
        )[0] || null;
    }

    function processState(invoice, assignments) {
        const related = assignments || [];
        const latest = latestAssignment(related);
        const remaining = Number(invoice?.remainingMinor || 0);
        const paid = Number(invoice?.paidMinor || 0);
        const serverStatus = invoice?.assignmentStatus || latest?.status || null;
        const assignee = invoice?.assignmentAssigneeName || latest?.assigneeName || null;
        const assignedMinor = Number(invoice?.assignmentAmountMinor ?? latest?.amountMinor ?? 0);
        const householdAvailableMinor = Number(
            invoice?.householdAvailableMinor ?? Math.max(0, remaining - ((serverStatus === "Assigned" || serverStatus === "Submitted") ? assignedMinor : 0))
        );

        if (remaining <= 0 && Number(invoice?.grossMinor || 0) > 0) {
            return {
                text: "Opłacona",
                cls: "hf-invoice-process-paid",
                responsible: assignee || "Dom",
                hint: "Pełna kwota faktury jest zaksięgowana jako zapłacona."
            };
        }

        if (serverStatus === "Submitted") {
            return {
                text: `Czeka na zatwierdzenie — ${assignee || "domownik"}`,
                cls: "hf-invoice-process-reported",
                responsible: assignee || "Domownik",
                hint: `Domownik zgłosił opłacenie ${money(assignedMinor, invoice?.currencyCode)}. Administrator musi teraz zatwierdzić i zaksięgować tę płatność.`
            };
        }

        if (serverStatus === "Approved") {
            const booked = Boolean(invoice?.assignmentInvoicePaymentBookedAtUtc);
            if (!booked) {
                return {
                    text: `Zatwierdzona — dokończ księgowanie`,
                    cls: "hf-invoice-process-reported",
                    responsible: assignee || "Domownik",
                    hint: `Decyzja administratora jest zapisana, ale prywatna płatność ${money(Number(invoice?.assignmentApprovedAmountMinor ?? assignedMinor), invoice?.currencyCode)} nie została jeszcze dopisana do płatności faktury.`
                };
            }

            return {
                text: `Część domownika zaksięgowana — ${assignee || "domownik"}`,
                cls: "hf-invoice-process-partial",
                responsible: assignee || "Domownik",
                hint: `Zaksięgowano prywatną część ${money(Number(invoice?.assignmentInvoicePaymentBookedAmountMinor ?? invoice?.assignmentApprovedAmountMinor ?? assignedMinor), invoice?.currencyCode)}. Do zapłaty pozostaje ${money(remaining, invoice?.currencyCode)}.`
            };
        }

        if (serverStatus === "Assigned") {
            return {
                text: `Przekazana — ${assignee || "domownik"}`,
                cls: "hf-invoice-process-assigned",
                responsible: assignee || "Domownik",
                hint: `Część domownika: ${money(assignedMinor, invoice?.currencyCode)}. Część możliwa do opłacenia przez dom: ${money(householdAvailableMinor, invoice?.currencyCode)}.`
            };
        }

        if (paid > 0 && remaining > 0) {
            return {
                text: "Częściowo opłacona",
                cls: "hf-invoice-process-partial",
                responsible: "Dom",
                hint: "Część faktury jest już zaksięgowana jako zapłacona."
            };
        }

        return {
            text: "Do opłacenia",
            cls: "hf-invoice-process-open",
            responsible: "Nieprzekazana",
            hint: "Faktura nie została jeszcze przekazana konkretnej osobie."
        };
    }

    function lifecycleHtml(invoice, assignments) {
        const events = assignmentEvents(assignments);
        const parts = [];

        parts.push(`
            <li class="hf-invoice-event">
                <span class="hf-invoice-event-dot"></span>
                <div>
                    <strong>Stan księgowy faktury</strong>
                    <small>Status źródłowy: ${esc(invoice?.status || "—")}. Zapłacono ${esc(money(invoice?.paidMinor, invoice?.currencyCode))}, pozostało ${esc(money(invoice?.remainingMinor, invoice?.currencyCode))}.</small>
                </div>
            </li>`);

        events.forEach(a => {
            parts.push(`
                <li class="hf-invoice-event">
                    <span class="hf-invoice-event-dot"></span>
                    <div>
                        <strong>Przekazano do opłacenia: ${esc(a.assigneeName)} — ${esc(money(a.amountMinor, a.currencyCode))}</strong>
                        <small>${esc(dateTime(a.assignedAtUtc))}${a.note ? ` · Notatka: ${esc(a.note)}` : ""}</small>
                    </div>
                </li>`);

            if ((a.status === "Submitted" || a.status === "Approved") && a.submittedAtUtc) {
                parts.push(`
                    <li class="hf-invoice-event is-important">
                        <span class="hf-invoice-event-dot"></span>
                        <div>
                            <strong>${esc(a.assigneeName)} zgłosił(a) opłacenie ${esc(money(a.amountMinor, a.currencyCode))}</strong>
                            <small>${esc(dateTime(a.submittedAtUtc))} · Oczekiwanie na decyzję administratora · Rozliczenie: ${esc(settlementText(a.settlementType))}</small>
                        </div>
                    </li>`);
                if (a.status === "Approved" && a.approvedAtUtc) {
                    const booked = Boolean(a.invoicePaymentBookedAtUtc);
                    parts.push(`
                        <li class="hf-invoice-event ${booked ? "is-paid" : "is-important"}">
                            <span class="hf-invoice-event-dot"></span>
                            <div>
                                <strong>${booked
                                    ? `Administrator zatwierdził i zaksięgował ${esc(money(a.invoicePaymentBookedAmountMinor ?? a.approvedAmountMinor ?? a.amountMinor, a.currencyCode))}`
                                    : `Administrator zatwierdził ${esc(money(a.approvedAmountMinor ?? a.amountMinor, a.currencyCode))} — oczekuje na dokończenie księgowania faktury`}</strong>
                                <small>${esc(dateTime(a.approvedAtUtc))}</small>
                            </div>
                        </li>`);
                }
            } else if (a.status === "Cancelled") {
                parts.push(`
                    <li class="hf-invoice-event">
                        <span class="hf-invoice-event-dot"></span>
                        <div>
                            <strong>Przekazanie anulowano</strong>
                            <small>${esc(dateTime(a.closedAtUtc))}</small>
                        </div>
                    </li>`);
            } else if (a.status === "Reassigned") {
                parts.push(`
                    <li class="hf-invoice-event">
                        <span class="hf-invoice-event-dot"></span>
                        <div>
                            <strong>Zakończono poprzednie przekazanie</strong>
                            <small>${esc(dateTime(a.closedAtUtc))} · Faktura została przekazana innej osobie.</small>
                        </div>
                    </li>`);
            }
        });

        if (Number(invoice?.remainingMinor || 0) <= 0 && Number(invoice?.grossMinor || 0) > 0) {
            parts.push(`
                <li class="hf-invoice-event is-paid">
                    <span class="hf-invoice-event-dot"></span>
                    <div>
                        <strong>Faktura opłacona</strong>
                        <small>W finansach domu pozostało 0,00 do zapłaty.</small>
                    </div>
                </li>`);
        }

        return `<ol class="hf-invoice-timeline">${parts.join("")}</ol>`;
    }

    const normalizeText = value =>
        String(value ?? "")
            .trim()
            .replace(/\s+/g, " ")
            .toLocaleLowerCase("pl-PL");

    const parseMoneyMinor = value => {
        const raw = String(value ?? "")
            .replace(/\u00A0/g, " ")
            .replace(/[A-Za-z]/g, "")
            .replace(/\s/g, "")
            .replace(",", ".")
            .trim();

        const major = Number.parseFloat(raw);

        return Number.isFinite(major)
            ? Math.round(major * 100)
            : null;
    };

    const normalizeDate = value => {
        const raw = String(value ?? "").trim();
        if (!raw) return "";

        // ISO / DateOnly: 2026-09-18
        const iso = raw.match(/^(\d{4})-(\d{2})-(\d{2})/);
        if (iso) {
            return `${iso[1]}-${iso[2]}-${iso[3]}`;
        }

        // Widok PL: 18.09.2026
        const pl = raw.match(/^(\d{1,2})\.(\d{1,2})\.(\d{4})/);
        if (pl) {
            return `${pl[3]}-${pl[2].padStart(2, "0")}-${pl[1].padStart(2, "0")}`;
        }

        return raw;
    };

    function findInvoiceStateForRow(row, states) {
        const cells = Array.from(row.children);

        const invoiceNo =
            normalizeText(cells[0]?.textContent);

        const supplier =
            normalizeText(cells[1]?.textContent);

        const grossMinor =
            parseMoneyMinor(cells[3]?.textContent);

        const dueDate =
            normalizeDate(cells[6]?.textContent);

        let candidates =
            (states || []).filter(x =>
                normalizeText(x.invoiceNo) === invoiceNo
            );

        if (candidates.length <= 1) {
            return candidates[0] || null;
        }

        // Numer faktury nie jest globalnie unikalny.
        // Najpierw rozróżniamy faktury po dostawcy.
        const bySupplier =
            candidates.filter(x =>
                normalizeText(x.supplier) === supplier
            );

        if (bySupplier.length === 1) {
            return bySupplier[0];
        }

        if (bySupplier.length > 0) {
            candidates = bySupplier;
        }

        // Jeśli ten sam dostawca użył ponownie tego samego numeru,
        // zawężamy po kwocie brutto.
        if (grossMinor !== null) {
            const byGross =
                candidates.filter(x =>
                    Number(x.grossMinor) === grossMinor
                );

            if (byGross.length === 1) {
                return byGross[0];
            }

            if (byGross.length > 0) {
                candidates = byGross;
            }
        }

        // Ostatni bezpieczny wyróżnik to termin płatności.
        if (dueDate) {
            const byDueDate =
                candidates.filter(x =>
                    normalizeDate(x.dueDate) === dueDate
                );

            if (byDueDate.length === 1) {
                return byDueDate[0];
            }

            if (byDueDate.length > 0) {
                candidates = byDueDate;
            }
        }

        // Nie zgadujemy, jeśli nadal istnieje więcej niż jedna faktura.
        return candidates.length === 1
            ? candidates[0]
            : null;
    }

    function decorateInvoiceTable(data) {
        const section = document.querySelector("#hf-invoices");
        const table = section?.querySelector(".hf-status-table");
        const headRow = table?.querySelector("thead tr");
        if (!table || !headRow || table.dataset.lifecycleReady === "1") return;

        table.dataset.lifecycleReady = "1";

        const headers = Array.from(headRow.children);
        const statusHeader = headers.find(x => (x.textContent || "").trim().toLowerCase() === "status") || headers.at(-1);
        if (!statusHeader) return;

        statusHeader.textContent = "Status faktury";

        const responsibleHeader = document.createElement("th");
        responsibleHeader.textContent = "Odpowiedzialny";
        responsibleHeader.className = "hf-invoice-responsible-head";
        headRow.insertBefore(responsibleHeader, statusHeader);

        const linksHeader = document.createElement("th");
        linksHeader.textContent = "Powiązania";
        linksHeader.className = "hf-invoice-links-head";
        statusHeader.insertAdjacentElement("afterend", linksHeader);

        const states = data.invoiceStates || [];
        const assignments = data.assignments || [];

        table.querySelectorAll("tbody tr").forEach(row => {
            const cells = Array.from(row.children);
            const invoice =
                findInvoiceStateForRow(
                    row,
                    states
                );

            if (!invoice) {
                // Jeżeli nie da się jednoznacznie dopasować faktury,
                // zostawiamy jej oryginalny status z widoku serwera,
                // zamiast kopiować stan innej faktury o tym samym numerze.
                return;
            }

            const related = assignments.filter(x => String(x.invoiceId) === String(invoice.id));
            const process = processState(invoice, related);

            const responsibleCell = document.createElement("td");
            responsibleCell.className = "hf-invoice-responsible-cell";
            responsibleCell.innerHTML = `
                <strong>${esc(process.responsible)}</strong>
                <small>${esc(process.hint)}</small>`;
            row.insertBefore(responsibleCell, statusHeader.cellIndex >= 0 ? row.children[statusHeader.cellIndex - 1] : null);

            // Po dodaniu nowej kolumny status znajduje się bezpośrednio przed kolumną Powiązania.
            const statusIndex = Array.from(headRow.children).findIndex(x => x === statusHeader);
            const currentStatusCell = row.children[statusIndex];
            if (currentStatusCell) {
                currentStatusCell.className = "hf-invoice-process-cell";
                currentStatusCell.innerHTML = `
                    <span class="hf-invoice-process ${esc(process.cls)}">${esc(process.text)}</span>
                    <small>System: ${esc(invoice.status || "—")}</small>`;
            }

            const linksCell = document.createElement("td");
            linksCell.className = "hf-invoice-links-cell";
            linksCell.innerHTML = `
                <details class="hf-invoice-links-details">
                    <summary>Historia (${related.length})</summary>
                    <div class="hf-invoice-links-popover">
                        <div class="hf-invoice-links-title">
                            <strong>${esc(invoice.invoiceNo)}</strong>
                            <span>${esc(invoice.supplier || "")}</span>
                        </div>
                        ${lifecycleHtml(invoice, related)}
                    </div>
                </details>`;
            currentStatusCell?.insertAdjacentElement("afterend", linksCell);
        });
    }

    async function readJson(response) {
        const text = await response.text();
        if (!text) return {};
        try { return JSON.parse(text); } catch { return { message: text }; }
    }

    async function post(action, values) {
        const body = new URLSearchParams(values || {});
        if (token) body.set("__RequestVerificationToken", token);
        const response = await fetch(`${endpoint}/${action}`, {
            method: "POST",
            headers: { "Content-Type": "application/x-www-form-urlencoded;charset=UTF-8" },
            body: body.toString(),
            credentials: "same-origin"
        });
        const data = await readJson(response);
        if (!response.ok) throw new Error(data.message || "Nie udało się wykonać operacji.");
        return data;
    }

    function flash(section, message, isError = false) {
        let box = section.querySelector(".hf-assign-message");
        if (!box) {
            box = document.createElement("div");
            box.className = "hf-assign-message";
            section.prepend(box);
        }
        box.classList.toggle("is-error", isError);
        box.textContent = message;
        box.hidden = false;
        window.setTimeout(() => { box.hidden = true; }, 6000);
    }

    function adminSection(data) {
        const section = document.createElement("section");
        section.className = "panel hf-section hf-assignment-section";
        section.id = "hf-invoice-assignments";

        const invoiceOptions = (data.invoices || []).map(i =>
            `<option value="${esc(i.id)}" data-max-minor="${esc(i.maxAmountMinor ?? i.amountMinor)}" data-currency="${esc(i.currencyCode)}">${esc(i.invoiceNo)} — ${esc(i.supplier)} — pozostało ${esc(money(i.amountMinor, i.currencyCode))}</option>`
        ).join("");
        const personOptions = (data.people || []).map(p =>
            `<option value="${esc(p.id)}">${esc(p.name)}</option>`
        ).join("");

        const rows = (data.assignments || []).map(a => `
            <tr>
                <td><strong>${esc(a.invoiceNo)}</strong><small>${esc(a.supplier)}</small></td>
                <td>${esc(a.assigneeName)}</td>
                <td>${esc(money(a.amountMinor, a.currencyCode))}</td>
                <td>${esc(dateTime(a.assignedAtUtc))}</td>
                <td>${esc(a.submittedAtUtc ? dateTime(a.submittedAtUtc) : "—")}</td>
                <td>${esc(a.submittedAtUtc ? settlementText(a.settlementType) : "—")}</td>
                <td><span class="hf-assign-status ${statusClass(a.status)}">${esc(statusText(a.status))}</span></td>
                <td>${esc(a.note || "—")}</td>
                <td>${
                    a.status === "Assigned"
                        ? `<button type="button" class="btn btn-secondary btn-compact" data-cancel-assignment="${esc(a.id)}">Anuluj</button>`
                        : a.status === "Submitted"
                            ? `<button type="button" class="btn btn-compact" data-approve-assignment="${esc(a.id)}">Zatwierdź i zaksięguj</button>`
                            : a.status === "Approved"
                                ? (a.invoicePaymentBookedAtUtc
                                    ? `<span class="hf-assign-approved-note">Zaksięgowano ${esc(dateTime(a.invoicePaymentBookedAtUtc))}</span>`
                                    : `<button type="button" class="btn btn-compact" data-approve-assignment="${esc(a.id)}">Dokończ księgowanie</button>`)
                                : "—"
                }</td>
            </tr>`).join("");

        section.innerHTML = `
            <div class="hf-section-head">
                <div>
                    <h2>Przekaż fakturę domownikowi do opłacenia</h2>
                    <p class="muted">Wybierz fakturę i osobę. Status przekazania oraz późniejszego zgłoszenia opłacenia jest również widoczny bezpośrednio przy fakturze.</p>
                </div>
                <div class="hf-inline-chip">${(data.assignments || []).filter(x => x.status === "Submitted" || (x.status === "Approved" && !x.invoicePaymentBookedAtUtc)).length} do rozliczenia</div>
            </div>
            <div class="hf-assign-message" hidden></div>
            <div class="hf-assignment-admin-grid">
                <article class="hf-assignment-card">
                    <h3>Nowe przekazanie</h3>
                    ${(data.invoices || []).length === 0 ? `<div class="empty-note">Brak otwartych faktur do przekazania.</div>` : `
                    <form class="hf-assignment-form" data-assignment-form>
                        <label>Faktura
                            <select name="invoiceId" required>${invoiceOptions}</select>
                        </label>
                        <label>Osoba odpowiedzialna za opłacenie
                            <select name="personId" required>${personOptions}</select>
                        </label>
                        <label>Kwota przekazana domownikowi
                            <input name="assignedAmount" type="number" step="0.01" min="0.01" required />
                        </label>
                        <div class="hf-assignment-split-preview hf-assignment-full" data-split-preview></div>
                        <label class="hf-assignment-full">Notatka dla domownika
                            <input name="note" maxlength="500" placeholder="np. opłać do piątku z własnego konta" />
                        </label>
                        <button class="btn hf-assignment-full" type="submit">Przekaż fakturę do opłacenia</button>
                    </form>`}
                </article>
                <article class="hf-assignment-card hf-assignment-info-card">
                    <h3>Przebieg faktury</h3>
                    <ol>
                        <li>Faktura ma własny status księgowy i kwotę pozostałą do zapłaty.</li>
                        <li>Przekazanie wskazuje osobę, konkretną kwotę jej części i zapisuje datę.</li>
                        <li>Aktywnej lub zgłoszonej płatności nie można przekazać ponownie.</li>
                        <li>Domownik po płatności zgłasza jej wykonanie oraz sposób rozliczenia.</li>
                        <li>Zgłoszenie trafia do administratora i nie jest jeszcze ostatecznie zaksięgowane.</li>
                        <li>Administrator wybiera „Zatwierdź i zaksięguj”; wtedy aktualizują się Zapłacono, Pozostało i status faktury.</li>
                        <li>Pozostałą część faktury może opłacić rachunek domu.</li>
                    </ol>
                    <p>Rozwiń „Historia” przy fakturze, aby zobaczyć wszystkie powiązane zdarzenia.</p>
                </article>
            </div>
            <div class="hf-assignment-history">
                <h3>Historia przekazań</h3>
                ${(data.assignments || []).length === 0 ? `<div class="empty-note">Nie przekazano jeszcze żadnej faktury.</div>` : `
                <div class="hf-status-table-wrap">
                    <table class="hf-status-table hf-assignment-table">
                        <thead><tr><th>Faktura</th><th>Osoba</th><th>Kwota</th><th>Przekazano</th><th>Opłacenie zgłoszono</th><th>Rozliczenie</th><th>Status</th><th>Notatka</th><th>Akcja</th></tr></thead>
                        <tbody>${rows}</tbody>
                    </table>
                </div>`}
            </div>`;

        const form = section.querySelector("[data-assignment-form]");
        const invoiceSelect = form?.querySelector('select[name="invoiceId"]');
        const amountInput = form?.querySelector('input[name="assignedAmount"]');
        const splitPreview = form?.querySelector('[data-split-preview]');

        const parseMajorToMinor = value => {
            const normalized = String(value ?? "").trim().replace(/\s/g, "").replace(",", ".");
            const number = Number(normalized);
            if (!Number.isFinite(number)) return 0;
            return Math.round(number * 100);
        };

        const refreshSplit = () => {
            if (!invoiceSelect || !amountInput || !splitPreview) return;
            const option = invoiceSelect.selectedOptions[0];
            const maxMinor = Number(option?.dataset.maxMinor || 0);
            const currency = option?.dataset.currency || "PLN";
            if (!amountInput.value && maxMinor > 0) amountInput.value = (maxMinor / 100).toFixed(2);
            const assignedMinor = parseMajorToMinor(amountInput.value);
            const houseMinor = Math.max(0, maxMinor - assignedMinor);
            amountInput.max = (maxMinor / 100).toFixed(2);
            splitPreview.innerHTML = `<strong>Podział:</strong> domownik ${esc(money(assignedMinor, currency))} · rachunek domu ${esc(money(houseMinor, currency))}`;
            splitPreview.classList.toggle("is-invalid", assignedMinor <= 0 || assignedMinor > maxMinor);
        };

        invoiceSelect?.addEventListener("change", () => {
            if (amountInput) amountInput.value = "";
            refreshSplit();
        });
        amountInput?.addEventListener("input", refreshSplit);
        refreshSplit();

        form?.addEventListener("submit", async event => {
            event.preventDefault();
            const fd = new FormData(form);
            const button = form.querySelector('button[type="submit"]');
            button.disabled = true;
            try {
                const assignedMinor = parseMajorToMinor(fd.get("assignedAmount"));
                const selectedOption = invoiceSelect?.selectedOptions[0];
                const maxMinor = Number(selectedOption?.dataset.maxMinor || 0);
                if (assignedMinor <= 0 || assignedMinor > maxMinor) {
                    throw new Error("Kwota domownika musi być większa od 0 i nie może przekraczać kwoty pozostałej na fakturze.");
                }
                const result = await post("Assign", {
                    invoiceId: fd.get("invoiceId"),
                    personId: fd.get("personId"),
                    amountMinor: assignedMinor,
                    note: fd.get("note") || ""
                });
                flash(section, result.message || "Faktura została przekazana.");
                window.setTimeout(() => window.location.reload(), 700);
            } catch (error) {
                flash(section, error.message, true);
                button.disabled = false;
            }
        });

        section.querySelectorAll("[data-approve-assignment]").forEach(button => {
            button.addEventListener("click", async () => {
                const id = button.dataset.approveAssignment;
                const reason = window.prompt(
                    "Opcjonalna uwaga do zatwierdzenia:",
                    "Potwierdzono płatność wykonaną przez domownika."
                );
                if (reason === null) return;
                if (!window.confirm("Zaksięgować prywatną płatność przy tej fakturze? Po operacji zmienią się kolumny Zapłacono, Pozostało i status faktury.")) return;

                button.disabled = true;
                try {
                    const result = await post("Approve", {
                        assignmentId: id,
                        reason: reason || ""
                    });
                    flash(section, result.message || "Płatność została zatwierdzona i zaksięgowana.");
                    window.setTimeout(() => window.location.reload(), 900);
                } catch (error) {
                    flash(section, error.message, true);
                    button.disabled = false;
                }
            });
        });

        section.querySelectorAll("[data-cancel-assignment]").forEach(button => {
            button.addEventListener("click", async () => {
                if (!window.confirm("Anulować przekazanie tej faktury?")) return;
                button.disabled = true;
                try {
                    const result = await post("Cancel", { assignmentId: button.dataset.cancelAssignment });
                    flash(section, result.message || "Przekazanie anulowano.");
                    window.setTimeout(() => window.location.reload(), 700);
                } catch (error) {
                    flash(section, error.message, true);
                    button.disabled = false;
                }
            });
        });

        return section;
    }

    function memberSection(data) {
        const section = document.createElement("section");
        section.className = "panel hf-section hf-assignment-section hf-my-assigned-invoices";
        section.id = "hf-my-assigned-invoices";

        const active = (data.assignments || []).filter(x => x.status === "Assigned");
        const history = (data.assignments || []).filter(x => x.status !== "Assigned");

        const cards = active.map(a => `
            <article class="hf-assigned-invoice-card">
                <div class="hf-assigned-invoice-head">
                    <div>
                        <span class="hf-assigned-kicker">Faktura przekazana Tobie</span>
                        <h3>${esc(a.invoiceNo)} — ${esc(a.supplier)}</h3>
                    </div>
                    <span class="hf-assign-status hf-assign-status-warn">Do opłacenia</span>
                </div>
                <div class="hf-assigned-invoice-values">
                    <div><span>Kwota</span><strong>${esc(money(a.amountMinor, a.currencyCode))}</strong></div>
                    <div><span>Termin</span><strong>${esc(date(a.dueDate))}</strong></div>
                    <div><span>Przekazano</span><strong>${esc(dateTime(a.assignedAtUtc))}</strong></div>
                    <div><span>Notatka</span><strong>${esc(a.note || "Brak dodatkowej notatki")}</strong></div>
                </div>
                <div class="hf-assigned-invoice-action">
                    <label>Sposób późniejszego rozliczenia z domem
                        <select data-settlement-type="${esc(a.id)}">
                            <option value="Refund">Zwrot środków</option>
                            <option value="Compensation">Kompensata obowiązkowej wpłaty</option>
                        </select>
                    </label>
                    <button class="btn" type="button" data-pay-assignment="${esc(a.id)}">Zarejestruj, że faktura została opłacona</button>
                </div>
                <p class="muted hf-assigned-help">Kliknij dopiero po faktycznym wykonaniu płatności. e-dom nie wykonuje przelewu bankowego.</p>
            </article>`).join("");

        const historyRows = history.slice(0, 20).map(a => `
            <tr>
                <td><strong>${esc(a.invoiceNo)}</strong><small>${esc(a.supplier)}</small></td>
                <td>${esc(money(a.amountMinor, a.currencyCode))}</td>
                <td>${esc(dateTime(a.assignedAtUtc))}</td>
                <td>${esc(a.submittedAtUtc ? dateTime(a.submittedAtUtc) : "—")}</td>
                <td>${esc(a.submittedAtUtc ? settlementText(a.settlementType) : "—")}</td>
                <td><span class="hf-assign-status ${statusClass(a.status)}">${esc(statusText(a.status))}</span></td>
            </tr>`).join("");

        section.innerHTML = `
            <div class="hf-section-head">
                <div>
                    <h2>Faktury przekazane mi do opłacenia</h2>
                    <p class="muted">Tutaj pojawiają się rachunki, które administrator wskazał Tobie do opłacenia ze środków prywatnych.</p>
                </div>
                <div class="hf-inline-chip">${active.length} do opłacenia</div>
            </div>
            <div class="hf-assign-message" hidden></div>
            ${active.length ? `<div class="hf-assigned-invoice-grid">${cards}</div>` : `<div class="empty-note">Nie masz teraz żadnej faktury przekazanej do opłacenia.</div>`}
            ${history.length ? `
                <details class="hf-details-card hf-assignment-history-details">
                    <summary>Historia przekazanych mi faktur (${history.length})</summary>
                    <div class="hf-status-table-wrap">
                        <table class="hf-status-table hf-assignment-table">
                            <thead><tr><th>Faktura</th><th>Kwota</th><th>Przekazano</th><th>Opłacenie zgłoszono</th><th>Rozliczenie</th><th>Status</th></tr></thead>
                            <tbody>${historyRows}</tbody>
                        </table>
                    </div>
                </details>` : ""}`;

        section.querySelectorAll("[data-pay-assignment]").forEach(button => {
            button.addEventListener("click", async () => {
                const id = button.dataset.payAssignment;
                const select = section.querySelector(`[data-settlement-type="${CSS.escape(id)}"]`);
                if (!window.confirm("Potwierdzasz, że ta faktura została już faktycznie opłacona z Twoich prywatnych środków?")) return;
                button.disabled = true;
                try {
                    const result = await post("Pay", {
                        assignmentId: id,
                        settlementType: select?.value || "Refund"
                    });
                    flash(section, result.message || "Płatność została zgłoszona i czeka na zatwierdzenie administratora.");
                    window.setTimeout(() => window.location.reload(), 900);
                } catch (error) {
                    flash(section, error.message, true);
                    button.disabled = false;
                }
            });
        });

        return section;
    }

    function protectHouseholdPaymentForm(data) {
        if (!data.canManage) return;
        const states = data.invoiceStates || [];
        const form = document.querySelector('form[action$="/HouseholdFinance/Invoice/Pay"], form[action$="/HouseholdFinance/PayInvoice"]');
        if (!form) return;

        const select = form.querySelector('select[name="InvoiceId"]');
        const amount = form.querySelector('input[name="Amount"]');
        const submit = form.querySelector('button[type="submit"]');
        if (!select || !amount || !submit) return;

        let helper = form.querySelector('[data-house-share-helper]');
        if (!helper) {
            helper = document.createElement("div");
            helper.className = "hf-house-share-helper";
            helper.setAttribute("data-house-share-helper", "1");
            amount.closest("label")?.insertAdjacentElement("afterend", helper);
        }

        const refresh = () => {
            const invoice = states.find(x => String(x.id) === String(select.value));
            if (!invoice) return;
            const availableMinor = Number(invoice.householdAvailableMinor ?? invoice.remainingMinor ?? 0);
            const reservedMinor = Number(invoice.reservedMinor ?? 0);
            amount.max = (availableMinor / 100).toFixed(2);
            if (Number(amount.value || 0) > availableMinor / 100) amount.value = (availableMinor / 100).toFixed(2);

            if (reservedMinor > 0) {
                helper.innerHTML = `<strong>Podział aktywny:</strong> część zarezerwowana dla domownika ${esc(money(reservedMinor, invoice.currencyCode))}. Z rachunku domu możesz teraz zaksięgować maks. ${esc(money(availableMinor, invoice.currencyCode))}.`;
            } else {
                helper.innerHTML = `Z rachunku domu możesz zaksięgować maks. ${esc(money(availableMinor, invoice.currencyCode))}.`;
            }

            const blocked = availableMinor <= 0;
            amount.disabled = blocked;
            submit.disabled = blocked;
            if (blocked) helper.innerHTML += " <strong>Cała pozostała kwota jest przypisana domownikowi / oczekuje na jego rozliczenie.</strong>";
        };

        select.addEventListener("change", refresh);
        refresh();

        form.addEventListener("submit", async event => {
            event.preventDefault();
            const invoice = states.find(x => String(x.id) === String(select.value));
            if (!invoice) return;
            const amountMinor = Math.round(Number(String(amount.value || "0").replace(",", ".")) * 100);
            const availableMinor = Number(invoice.householdAvailableMinor ?? invoice.remainingMinor ?? 0);
            if (!Number.isFinite(amountMinor) || amountMinor <= 0) {
                window.alert("Podaj prawidłową kwotę płatności.");
                return;
            }
            if (amountMinor > availableMinor) {
                window.alert("Kwota przekracza część faktury dostępną do opłacenia przez rachunek domu.");
                refresh();
                return;
            }

            const sourceType = form.querySelector('select[name="SourceType"]')?.value || "HouseholdBank";
            const paidAtUtc = form.querySelector('input[name="PaidAtUtc"]')?.value || "";
            submit.disabled = true;
            try {
                const result = await post("PayHouseShare", {
                    invoiceId: select.value,
                    amountMinor,
                    sourceType,
                    paidAtUtc
                });
                window.alert(result.message || "Płatność została zaksięgowana.");
                window.location.reload();
            } catch (error) {
                window.alert(error.message);
                submit.disabled = false;
                refresh();
            }
        });
    }

    async function init() {
        const invoiceSection = document.querySelector("#hf-invoices");
        if (!invoiceSection || document.querySelector("#hf-invoice-assignments, #hf-my-assigned-invoices")) return;

        try {
            const response = await fetch(`${endpoint}/Data`, { credentials: "same-origin" });
            if (!response.ok) return;
            const data = await response.json();

            decorateInvoiceTable(data);
            protectHouseholdPaymentForm(data);

            const section = data.canManage ? adminSection(data) : memberSection(data);
            if (data.canManage) invoiceSection.insertAdjacentElement("afterend", section);
            else invoiceSection.insertAdjacentElement("beforebegin", section);
        } catch (error) {
            console.warn("Nie udało się wczytać przekazanych faktur.", error);
        }
    }

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
    else init();
})();
