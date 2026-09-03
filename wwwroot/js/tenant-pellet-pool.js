(() => {
    const path = (window.location.pathname || "").toLowerCase();
    if (path !== "/rental/settlements" && !path.startsWith("/rental/settlements/")) return;

    const endpoint = "/Rental/Pellet";
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

    const date = value => {
        if (!value) return "—";
        const raw = String(value).slice(0, 10);
        const p = raw.split("-");
        return p.length === 3 ? `${p[2]}.${p[1]}.${p[0]}` : raw;
    };


    const defaultCorrectionDueDate = () => {
        const value = new Date();
        value.setDate(value.getDate() + 14);
        const local = new Date(
            value.getTime() - value.getTimezoneOffset() * 60000
        );
        return local.toISOString().slice(0, 10);
    };

    function defaultDates() {
        const now = new Date();
        const from = new Date(now.getFullYear(), now.getMonth(), 1);
        const to = new Date(now.getFullYear() + 1, now.getMonth(), 0);

        const iso = d => {
            const local = new Date(d.getTime() - d.getTimezoneOffset() * 60000);
            return local.toISOString().slice(0, 10);
        };

        return {
            from: iso(from),
            to: iso(to),
            purchase: iso(now)
        };
    }

    function findSettlementCard(item) {
        const expected =
            `${String(item.tenantName || "").trim()} — ${String(item.periodKey || "").trim()}`
                .toLowerCase();

        const heading = Array.from(
            main.querySelectorAll(
                ".card-header strong, .card strong"
            )
        ).find(x =>
            String(x.textContent || "")
                .trim()
                .toLowerCase() === expected
        );

        return heading?.closest(".card") || null;
    }

    function addPelletButtonToSettlement(
        item,
        canManage)
    {
        if (!canManage || !item.editable) {
            return;
        }

        const card = findSettlementCard(item);
        if (!card) {
            return;
        }

        if (card.querySelector(
                "[data-add-pellet-to-settlement]")) {
            return;
        }

        const body =
            card.querySelector(".card-body")
            || card;

        const panel = document.createElement("section");
        panel.className = "tenant-pellet-draft-action";

        if (Number(item.pelletAmountMinor || 0) > 0) {
            panel.innerHTML = `
                <div>
                    <span>Pellet / ogrzewanie</span>
                    <strong>
                        Już dodano:
                        ${esc(money(item.pelletAmountMinor, "PLN"))}
                    </strong>
                    <small>
                        Pozycja jest częścią bieżącego projektu rozliczenia.
                    </small>
                </div>
                <b>Gotowe</b>`;
        } else {
            panel.innerHTML = `
                <div>
                    <span>Pellet / ogrzewanie</span>
                    <strong>Dodaj udział z aktywnej puli pelletu</strong>
                    <small>
                        e-dom wyliczy kwotę dla ${esc(item.periodKey)}
                        na podstawie puli domu i liczby aktywnych lokatorów.
                    </small>
                </div>

                <button type="button"
                        class="btn btn-secondary btn-sm"
                        data-add-pellet-to-settlement>
                    Dodaj pellet z puli
                </button>`;
        }

        const table =
            body.querySelector("table");

        if (table) {
            table.insertAdjacentElement(
                "afterend",
                panel
            );
        } else {
            body.prepend(panel);
        }

        panel.querySelector(
            "[data-add-pellet-to-settlement]"
        )?.addEventListener(
            "click",
            async event => {
                const button =
                    event.currentTarget;

                if (!window.confirm(
                    `Dodać do projektu ${item.periodKey} udział pelletu wyliczony z aktywnej puli?`
                )) {
                    return;
                }

                button.disabled = true;

                try {
                    const body = new FormData();

                    body.append(
                        "leaseContractId",
                        item.leaseContractId
                    );

                    body.append(
                        "periodKey",
                        item.periodKey
                    );

                    if (token) {
                        body.append(
                            "__RequestVerificationToken",
                            token
                        );
                    }

                    const response = await fetch(
                        `${endpoint}/ApplyToSettlement`,
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
                            || "Nie udało się dodać pelletu."
                        );
                    }

                    window.alert(
                        result.message
                        || "Pellet został dodany."
                    );

                    window.location.reload();
                } catch (error) {
                    window.alert(
                        error.message
                        || "Nie udało się dodać pelletu."
                    );

                    button.disabled = false;
                }
            }
        );
    }

    function findInsertPoint() {
        const heading = Array.from(main.querySelectorAll("h1,h2,h3,strong"))
            .find(x => String(x.textContent || "").trim().toLowerCase() === "przygotuj miesięczne rozliczenie");

        return heading?.closest(".card") || null;
    }

    function poolCard(pool) {
        const currentShare = pool.currentPerTenantMinor != null
            ? money(pool.currentPerTenantMinor, pool.currencyCode)
            : "po utworzeniu planu miesiąca";

        const plans = Array.isArray(pool.plans) ? pool.plans : [];

        return `
            <article class="tenant-pellet-pool-card">
                <div class="tenant-pellet-pool-head">
                    <div>
                        <span>${esc(pool.buildingName)} · ${esc(pool.seasonName)}</span>
                        <strong>${esc(money(pool.totalAmountMinor, pool.currencyCode))}</strong>
                        <small>${esc(date(pool.periodFrom))} – ${esc(date(pool.periodTo))}</small>
                    </div>
                    <b>${esc(pool.status)}</b>
                </div>

                <div class="tenant-pellet-kpis">
                    <div><span>Koszt całej puli</span><strong>${esc(money(pool.totalAmountMinor, pool.currencyCode))}</strong></div>
                    <div><span>Już przypisano lokatorom</span><strong>${esc(money(pool.allocatedMinor, pool.currencyCode))}</strong></div>
                    <div><span>Z tego opłacone</span><strong>${esc(money(pool.paidMinor, pool.currencyCode))}</strong></div>
                    <div><span>Pozostało do rozdzielenia</span><strong>${esc(money(pool.remainingMinor, pool.currencyCode))}</strong></div>
                </div>

                <div class="tenant-pellet-current">
                    <span>Aktualnie wynajętych osób: <strong>${esc(pool.currentTenantCount)}</strong></span>
                    <span>Bieżący udział na osobę: <strong>${esc(currentShare)}</strong></span>
                    ${pool.palletCount != null ? `<span>Palety: <strong>${esc(pool.palletCount)}</strong></span>` : ""}
                    ${pool.weightKg != null ? `<span>Masa: <strong>${esc(pool.weightKg)} kg</strong></span>` : ""}
                </div>

                <div class="tenant-pellet-correction-actions">
                    <button type="button"
                            class="btn btn-secondary btn-sm"
                            data-pellet-preview="${esc(pool.id)}">
                        Wylicz podział
                    </button>

                    <button type="button"
                            class="btn btn-primary btn-sm"
                            data-pellet-generate="${esc(pool.id)}">
                        Wygeneruj korekty
                    </button>
                </div>

                <div class="tenant-pellet-correction-preview"
                     data-pellet-preview-host="${esc(pool.id)}"
                     hidden></div>

                <details class="tenant-pellet-history">
                    <summary>Historia miesięcznych podziałów (${plans.length})</summary>
                    <div>
                        ${plans.length
                            ? plans.map(plan => `
                                <div class="tenant-pellet-plan">
                                    <span><strong>${esc(plan.periodKey)}</strong> · ${esc(plan.tenantCount)} lokatorów</span>
                                    <span>Pula miesiąca: <strong>${esc(money(plan.monthlyBudgetMinor, pool.currencyCode))}</strong></span>
                                    <span>Pozostało przed miesiącem: <strong>${esc(money(plan.poolRemainingBeforeMinor, pool.currencyCode))}</strong></span>
                                    <small>${(plan.shares || []).map(s =>
                                        `${esc(s.tenantName)} (${esc(s.roomName)}): ${esc(money(s.amountMinor, pool.currencyCode))}`
                                    ).join(" · ")}</small>
                                </div>`).join("")
                            : '<div class="tenant-pellet-no-plan">Brak miesięcznych planów. Powstaną przy przeliczaniu rozliczeń.</div>'}
                    </div>
                </details>
            </article>`;
    }

    async function loadCorrectionPreview(pool, host) {
        host.hidden = false;
        host.innerHTML = `
            <div class="tenant-pellet-preview-loading">
                Wyliczam podział puli i sprawdzam zamknięte rozliczenia…
            </div>`;

        const response = await fetch(
            `${endpoint}/PreviewCorrections/${encodeURIComponent(pool.id)}`,
            {
                credentials: "same-origin",
                headers: { "Accept": "application/json" }
            }
        );

        const result = await response.json().catch(() => ({}));

        if (!response.ok) {
            throw new Error(
                result.message ||
                "Nie udało się wyliczyć podziału pelletu."
            );
        }

        const preview = result.preview;
        const rows = preview.rows || [];

        const closedRows = rows.filter(x => x.closedSettlement);
        const corrections = closedRows.filter(
            x => Number(x.correctionNeededMinor || 0) > 0
        );

        host.innerHTML = `
            <div class="tenant-pellet-preview-head">
                <div>
                    <strong>Wyliczenie dla zamkniętych rozliczeń</strong>
                    <small>
                        Pula ${esc(money(preview.poolTotalMinor, preview.currencyCode))}
                        · do korekt ${esc(money(preview.proposedCorrectionMinor, preview.currencyCode))}
                        · ${esc(preview.closedCorrectionCount)} korekt
                    </small>
                </div>
            </div>

            ${rows.length
                ? `
                    <div class="tenant-pellet-preview-table-wrap">
                        <table class="tenant-pellet-preview-table">
                            <thead>
                                <tr>
                                    <th>Miesiąc</th>
                                    <th>Lokator</th>
                                    <th>Osób</th>
                                    <th>Pula miesiąca</th>
                                    <th>Należny udział</th>
                                    <th>Już pellet</th>
                                    <th>Korekta</th>
                                    <th>Status</th>
                                </tr>
                            </thead>
                            <tbody>
                                ${rows.map(row => `
                                    <tr class="${row.closedSettlement ? "" : "is-open"}">
                                        <td>${esc(row.periodKey)}</td>
                                        <td>
                                            <strong>${esc(row.tenantName)}</strong>
                                            <small>${esc(row.roomName)}</small>
                                        </td>
                                        <td>${esc(row.tenantCount)}</td>
                                        <td>${esc(money(row.monthlyPoolMinor, preview.currencyCode))}</td>
                                        <td>${esc(money(row.targetShareMinor, preview.currencyCode))}</td>
                                        <td>${esc(money(row.alreadyAssignedPelletMinor, preview.currencyCode))}</td>
                                        <td>
                                            <strong class="${Number(row.correctionNeededMinor || 0) > 0 ? "needs-correction" : ""}">
                                                ${esc(money(row.correctionNeededMinor, preview.currencyCode))}
                                            </strong>
                                        </td>
                                        <td>
                                            ${row.closedSettlement
                                                ? `<span class="tenant-pellet-closed">Zamknięte · ${esc(row.settlementStatus)}</span>`
                                                : `<span class="tenant-pellet-open">Otwarte · ${esc(row.settlementStatus)}</span>`}
                                        </td>
                                    </tr>`).join("")}
                            </tbody>
                        </table>
                    </div>`
                : `
                    <div class="tenant-pellet-no-plan">
                        Nie znaleziono rozliczeń lokatorów w okresie tej puli.
                    </div>`}

            ${corrections.length
                ? `
                    <div class="tenant-pellet-generate-box">
                        <label>Termin płatności wygenerowanych korekt
                            <input type="date"
                                   data-pellet-correction-due
                                   value="${defaultCorrectionDueDate()}" />
                        </label>

                        <button type="button"
                                class="btn btn-primary btn-sm"
                                data-pellet-generate-confirm="${esc(pool.id)}">
                            Wygeneruj ${esc(corrections.length)} korekt
                            · ${esc(money(preview.proposedCorrectionMinor, preview.currencyCode))}
                        </button>
                    </div>`
                : `
                    <div class="tenant-pellet-preview-ok">
                        Zamknięte rozliczenia nie wymagają nowych korekt pelletu.
                    </div>`}
        `;

        host.querySelector(
            "[data-pellet-generate-confirm]"
        )?.addEventListener("click", async event => {
            await generateCorrections(
                pool,
                host,
                event.currentTarget
            );
        });
    }

    async function generateCorrections(pool, host, button) {
        const dueDate = host.querySelector(
            "[data-pellet-correction-due]"
        )?.value || "";

        if (!dueDate) {
            window.alert(
                "Podaj termin płatności korekt."
            );
            return;
        }

        if (!window.confirm(
            "Wygenerować korekty pelletu dla wszystkich zamkniętych rozliczeń wskazanych w wyliczeniu? Oryginalne rachunki pozostaną w historii."
        )) {
            return;
        }

        button.disabled = true;

        try {
            const body = new FormData();
            body.append("poolId", pool.id);
            body.append("dueDate", dueDate);

            if (token) {
                body.append(
                    "__RequestVerificationToken",
                    token
                );
            }

            const response = await fetch(
                `${endpoint}/GenerateCorrections`,
                {
                    method: "POST",
                    body,
                    credentials: "same-origin"
                }
            );

            const result = await response.json().catch(() => ({}));

            if (!response.ok) {
                throw new Error(
                    result.message ||
                    "Nie udało się wygenerować korekt."
                );
            }

            window.alert(
                result.message ||
                "Korekty zostały wygenerowane."
            );

            window.location.reload();
        } catch (error) {
            window.alert(
                error.message ||
                "Nie udało się wygenerować korekt."
            );
            button.disabled = false;
        }
    }

    function render(data) {
        const existing = document.getElementById("tenantPelletPoolModule");
        existing?.remove();

        if (!data.canManage) return;

        const payload = data.data || {};
        const buildings = payload.buildings || [];
        const pools = payload.pools || [];
        const defaults = defaultDates();


        (data.editableSettlements || []).forEach(
            settlement => addPelletButtonToSettlement(
                settlement,
                Boolean(data.canManage)
            )
        );

        const module = document.createElement("section");
        module.id = "tenantPelletPoolModule";
        module.className = "tenant-pellet-module";

        module.innerHTML = `
            <div class="tenant-pellet-module-head">
                <div>
                    <span class="tenant-pellet-eyebrow">Dom2 · ogrzewanie sezonowe</span>
                    <h2>Pellet — pula do rozliczeń lokatorów</h2>
                    <p>
                        Koszt zakupu pelletu jest dzielony wyłącznie między aktywnych lokatorów.
                        Opublikowane i opłacone wcześniejsze miesiące nie są przeliczane wstecz.
                        Gdy zmieni się liczba lokatorów, kolejny miesiąc korzysta z pozostałej puli i nowej liczby osób.
                    </p>
                </div>
                <button type="button" class="btn btn-primary btn-sm" data-pellet-create-toggle>+ Dodaj pulę ręcznie</button>
            </div>

            <div class="tenant-pellet-create" data-pellet-create-form hidden>
                <form>
                    <div class="tenant-pellet-grid">
                        <label>Dom / budynek
                            <select name="buildingId" required>
                                <option value="">— wybierz dom z pokojami do wynajmu —</option>
                                ${buildings.map(x =>
                                    `<option value="${esc(x.id)}">${esc(x.name)} · ${esc(x.rentableRooms)} pok. do wynajmu</option>`
                                ).join("")}
                            </select>
                        </label>
                        <label>Nazwa sezonu
                            <input name="seasonName" value="${new Date().getFullYear()}/${new Date().getFullYear()+1}" />
                        </label>
                        <label>Okres od
                            <input name="periodFrom" type="date" value="${defaults.from}" required />
                        </label>
                        <label>Okres do
                            <input name="periodTo" type="date" value="${defaults.to}" required />
                        </label>
                        <label>Data zakupu
                            <input name="purchaseDate" type="date" value="${defaults.purchase}" required />
                        </label>
                        <label>Łączny koszt pelletu
                            <input name="totalAmount" type="number" step="0.01" min="0.01" required />
                        </label>
                        <label>Waluta
                            <input name="currencyCode" value="PLN" maxlength="3" />
                        </label>
                        <label>Liczba palet
                            <input name="palletCount" type="number" step="0.01" min="0" />
                        </label>
                        <label>Masa kg
                            <input name="weightKg" type="number" step="0.01" min="0" />
                        </label>
                        <label>Dostawca
                            <input name="supplier" />
                        </label>
                        <label>Nr faktury / dokumentu
                            <input name="documentNo" />
                        </label>
                        <label class="tenant-pellet-full">Uwagi
                            <textarea name="notes" rows="2" placeholder="np. zakup na sezon grzewczy, kilka palet"></textarea>
                        </label>
                    </div>
                    <div class="tenant-pellet-form-actions">
                        <button type="submit" class="btn btn-primary">Utwórz pulę sezonową</button>
                    </div>
                </form>
            </div>

            <div class="tenant-pellet-pools">
                ${pools.length
                    ? pools.map(poolCard).join("")
                    : `<div class="tenant-pellet-empty">
                        Nie ma jeszcze puli pelletu. Standardowo utworzy się automatycznie po dodaniu faktury z kategorią Pellet w Finansach domowych.
                       </div>`}
            </div>`;

        const insert = findInsertPoint();
        if (insert) {
            insert.insertAdjacentElement("afterend", module);
        } else {
            main.prepend(module);
        }

        const create = module.querySelector("[data-pellet-create-form]");
        module.querySelector("[data-pellet-create-toggle]")?.addEventListener("click", () => {
            create.hidden = !create.hidden;
        });


        module.querySelectorAll("[data-pellet-preview]").forEach(button => {
            button.addEventListener("click", async () => {
                const poolId = button.dataset.pelletPreview;
                const pool = pools.find(
                    x => String(x.id) === String(poolId)
                );

                const host = module.querySelector(
                    `[data-pellet-preview-host="${CSS.escape(String(poolId))}"]`
                );

                if (!pool || !host) return;

                try {
                    if (!host.hidden) {
                        host.hidden = true;
                        return;
                    }

                    await loadCorrectionPreview(
                        pool,
                        host
                    );
                } catch (error) {
                    host.hidden = false;
                    host.innerHTML = `
                        <div class="tenant-pellet-preview-error">
                            ${esc(error.message || "Nie udało się wyliczyć podziału.")}
                        </div>`;
                }
            });
        });

        module.querySelectorAll("[data-pellet-generate]").forEach(button => {
            button.addEventListener("click", async () => {
                const poolId = button.dataset.pelletGenerate;
                const pool = pools.find(
                    x => String(x.id) === String(poolId)
                );

                const host = module.querySelector(
                    `[data-pellet-preview-host="${CSS.escape(String(poolId))}"]`
                );

                if (!pool || !host) return;

                try {
                    await loadCorrectionPreview(
                        pool,
                        host
                    );

                    host.scrollIntoView({
                        behavior: "smooth",
                        block: "nearest"
                    });
                } catch (error) {
                    host.hidden = false;
                    host.innerHTML = `
                        <div class="tenant-pellet-preview-error">
                            ${esc(error.message || "Nie udało się przygotować korekt.")}
                        </div>`;
                }
            });
        });

        create?.querySelector("form")?.addEventListener("submit", async event => {
            event.preventDefault();

            const button = event.currentTarget.querySelector('button[type="submit"]');
            button.disabled = true;

            try {
                const body = new FormData(event.currentTarget);
                if (token) body.append("__RequestVerificationToken", token);

                const response = await fetch(`${endpoint}/Create`, {
                    method: "POST",
                    body,
                    credentials: "same-origin"
                });

                const result = await response.json().catch(() => ({}));
                if (!response.ok) throw new Error(result.message || "Nie udało się utworzyć puli pelletu.");

                window.alert(result.message || "Utworzono pulę pelletu.");
                window.location.reload();
            } catch (error) {
                window.alert(error.message || "Nie udało się utworzyć puli pelletu.");
                button.disabled = false;
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
            console.warn("Nie udało się wczytać puli pelletu.", error);
        }
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", load);
    } else {
        load();
    }
})();
