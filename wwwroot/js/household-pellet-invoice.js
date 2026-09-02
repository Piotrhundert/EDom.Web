(() => {
    const path = (window.location.pathname || "").toLowerCase();
    if (path !== "/householdfinance" && !path.startsWith("/householdfinance/")) return;

    const endpoint = "/HouseholdFinance/PelletInvoice";
    const form = Array.from(document.querySelectorAll("form")).find(f => {
        const action = String(f.getAttribute("action") || "").toLowerCase();
        return action.endsWith("/householdfinance/invoice");
    });

    if (!form) return;

    const category = form.querySelector('[name="CategoryCode"]');
    const periodFrom = form.querySelector('[name="PeriodFrom"]');
    const periodTo = form.querySelector('[name="PeriodTo"]');
    if (!category) return;

    const listId = "household-invoice-category-list";
    if (!document.getElementById(listId)) {
        const list = document.createElement("datalist");
        list.id = listId;
        ["Other","Electricity","Water","Gas","Pellet","Internet","Insurance","Repair"].forEach(v => {
            const option = document.createElement("option");
            option.value = v;
            list.appendChild(option);
        });
        document.body.appendChild(list);
    }
    category.setAttribute("list", listId);

    const extra = document.createElement("div");
    extra.className = "hf-pellet-invoice-extra";
    extra.hidden = true;
    extra.innerHTML = `
        <div class="hf-pellet-invoice-note">
            <strong>Pellet → automatyczna pula lokatorów</strong>
            <span>
                Ta faktura automatycznie utworzy lub zasili pulę pelletu dla wybranego domu.
                Jeśli rachunki lokatorów za część okresu są już zatwierdzone lub opublikowane,
                e-dom utworzy jawne korekty zamiast zmieniać stare rozliczenia.
            </span>
        </div>
        <label>Dom / budynek
            <select name="BuildingId" data-pellet-building required>
                <option value="">Ładowanie budynków…</option>
            </select>
        </label>
        <label>Sezon / nazwa puli
            <input name="PelletSeasonName" data-pellet-season placeholder="np. 2026/2027" />
        </label>
        <label>Liczba palet
            <input name="PelletPalletCount" type="number" min="0" step="0.01" />
        </label>
        <label>Masa pelletu kg
            <input name="PelletWeightKg" type="number" min="0" step="0.01" />
        </label>
        <label class="hf-pellet-full">Uwagi do puli
            <input name="PelletNotes" placeholder="np. zakup na cały sezon grzewczy" />
        </label>
    `;

    const submit = form.querySelector('button[type="submit"]');
    submit?.parentElement?.insertBefore(extra, submit);

    const building = extra.querySelector("[data-pellet-building]");
    const season = extra.querySelector("[data-pellet-season]");

    const isPellet = () => {
        const value = String(category.value || "").trim().toLowerCase();
        return value === "pellet" || value === "pelet" || value.includes("pellet") || value.includes("pelet");
    };

    const iso = date => {
        const d = new Date(date.getTime() - date.getTimezoneOffset() * 60000);
        return d.toISOString().slice(0,10);
    };

    function fillSeasonDefaults() {
        const now = new Date();
        if (periodFrom && !periodFrom.value)
            periodFrom.value = iso(new Date(now.getFullYear(), now.getMonth(), 1));
        if (periodTo && !periodTo.value)
            periodTo.value = iso(new Date(now.getFullYear() + 1, now.getMonth(), 0));
        if (season && !season.value)
            season.value = `${now.getFullYear()}/${now.getFullYear() + 1}`;
    }

    function sync() {
        extra.hidden = !isPellet();

        if (isPellet()) {
            fillSeasonDefaults();
            if (periodFrom) periodFrom.required = true;
            if (periodTo) periodTo.required = true;
            if (building) building.required = true;
        } else {
            if (periodFrom) periodFrom.required = false;
            if (periodTo) periodTo.required = false;
            if (building) building.required = false;
        }
    }

    category.addEventListener("input", sync);
    category.addEventListener("change", sync);

    async function loadBuildings() {
        try {
            const response = await fetch(`${endpoint}/Data`, {
                credentials: "same-origin",
                headers: { "Accept": "application/json" }
            });

            if (!response.ok) throw new Error();

            const data = await response.json();
            const buildings = data.buildings || [];

            building.innerHTML = '<option value="">— wybierz dom —</option>' +
                buildings.map(x =>
                    `<option value="${x.id}">${x.name} · ${x.rentableRooms} pok. do wynajmu</option>`
                ).join("");

            if (buildings.length === 1)
                building.value = buildings[0].id;
        } catch {
            building.innerHTML = '<option value="">Nie udało się wczytać budynków</option>';
        }
    }

    form.addEventListener("submit", async event => {
        if (!isPellet()) return;

        event.preventDefault();

        if (!periodFrom?.value || !periodTo?.value) {
            window.alert("Dla pelletu podaj okres, przez który koszt ma być rozliczany między lokatorów.");
            return;
        }

        if (!building?.value) {
            window.alert("Wybierz dom, którego dotyczy zakup pelletu.");
            return;
        }

        const button = form.querySelector('button[type="submit"]');
        if (button) button.disabled = true;

        try {
            const body = new FormData(form);

            const response = await fetch(`${endpoint}/Create`, {
                method: "POST",
                body,
                credentials: "same-origin"
            });

            const result = await response.json().catch(() => ({}));

            if (!response.ok)
                throw new Error(result.message || "Nie udało się zapisać faktury pelletu.");

            window.alert(result.message || "Faktura i pula pelletu zostały zapisane.");
            window.location.reload();
        } catch (error) {
            window.alert(error.message || "Nie udało się zapisać faktury pelletu.");
            if (button) button.disabled = false;
        }
    });

    loadBuildings();
    sync();
})();
