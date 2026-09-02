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

    function render(data) {
        const existing = document.getElementById("tenantPelletPoolModule");
        existing?.remove();

        if (!data.canManage) return;

        const payload = data.data || {};
        const buildings = payload.buildings || [];
        const pools = payload.pools || [];
        const defaults = defaultDates();

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
