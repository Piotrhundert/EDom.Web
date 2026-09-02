(() => {
    const path = (window.location.pathname || "").toLowerCase();
    if (path !== "/property" && !path.startsWith("/property/")) return;

    const endpoint = "/Property/Details";
    const token = document.querySelector('#property-details-token input[name="__RequestVerificationToken"]')?.value || "";

    const esc = value => String(value ?? "").replace(/[&<>"']/g, c => ({
        "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#039;"
    })[c]);

    const formatArea = value => {
        const n = Number(value);
        if (!Number.isFinite(n)) return "—";
        return new Intl.NumberFormat("pl-PL", { maximumFractionDigits: 2 }).format(n) + " m²";
    };

    const formatDate = value => {
        if (!value) return "—";
        const raw = String(value).slice(0, 10);
        const p = raw.split("-");
        return p.length === 3 ? `${p[2]}.${p[1]}.${p[0]}` : raw;
    };

    const detailValue = (obj, key, fallback = null) =>
        obj?.details?.[key] ?? fallback;

    let state = null;

    function ownerName(id) {
        if (!id || !state) return null;
        return state.owners.find(x => String(x.id) === String(id))?.name || null;
    }

    function coOwnerNames(ids) {
        return (ids || []).map(ownerName).filter(Boolean);
    }

    function summaryHtml(type, item) {
        const details = item.details || {};
        const primaryOwner = ownerName(details.primaryOwnerPersonId);
        const coOwners = coOwnerNames(details.coOwnerPersonIds);
        const ownerText = [primaryOwner, ...coOwners].filter(Boolean).join(", ") || "Nie wskazano";
        const address = type === "Parcel"
            ? (item.addressText || "Brak adresu")
            : (details.addressText || `Adres jak dla działki: ${item.parcelName || "—"}`);

        const landRegister = details.landRegisterNumber || "Nie podano";

        if (type === "Parcel") {
            return `
                <div class="property-details-chip"><span>Adres</span><strong>${esc(address)}</strong></div>
                <div class="property-details-chip"><span>Właściciel</span><strong>${esc(ownerText)}</strong></div>
                <div class="property-details-chip"><span>Księga wieczysta</span><strong>${esc(landRegister)}</strong></div>
                <div class="property-details-chip"><span>Nr ewidencyjny</span><strong>${esc(item.registryNo || "Nie podano")}</strong></div>
                <div class="property-details-chip"><span>Powierzchnia</span><strong>${esc(formatArea(item.area))}</strong></div>`;
        }

        return `
            <div class="property-details-chip"><span>Adres</span><strong>${esc(address)}</strong></div>
            <div class="property-details-chip"><span>Właściciel</span><strong>${esc(ownerText)}</strong></div>
            <div class="property-details-chip"><span>Księga wieczysta</span><strong>${esc(landRegister)}</strong></div>
            <div class="property-details-chip"><span>Pow. użytkowa</span><strong>${esc(formatArea(item.usableArea))}</strong></div>
            <div class="property-details-chip"><span>Rok budowy</span><strong>${esc(item.buildYear ?? "—")}</strong></div>`;
    }

    function ownerOptions(selected) {
        return `<option value="">— nie wskazano —</option>` +
            state.owners.map(x =>
                `<option value="${esc(x.id)}" ${String(x.id) === String(selected || "") ? "selected" : ""}>${esc(x.name)}</option>`
            ).join("");
    }

    function coOwnerOptions(selectedIds) {
        const selected = new Set((selectedIds || []).map(String));
        return state.owners.map(x =>
            `<option value="${esc(x.id)}" ${selected.has(String(x.id)) ? "selected" : ""}>${esc(x.name)}</option>`
        ).join("");
    }

    function parcelForm(item) {
        const d = item.details || {};
        return `
            <form class="property-details-form" data-property-details-form="Parcel">
                <input type="hidden" name="ObjectId" value="${esc(item.id)}" />

                <div class="property-details-group">
                    <h4>Dane podstawowe</h4>
                    <div class="property-details-grid">
                        <label>Nazwa
                            <input name="Name" value="${esc(item.name)}" required />
                        </label>
                        <label>Adres
                            <input name="AddressText" value="${esc(item.addressText || "")}" placeholder="ulica, numer, kod, miejscowość" />
                        </label>
                        <label>Numer ewidencyjny działki
                            <input name="RegistryNo" value="${esc(item.registryNo || "")}" />
                        </label>
                        <label>Powierzchnia m²
                            <input name="Area" type="number" step="0.01" min="0" value="${esc(item.area ?? "")}" />
                        </label>
                        <label>Rodzaj własności
                            <select name="OwnershipType">
                                ${["Owned","CoOwned","Leasehold","Rented","Other"].map(x =>
                                    `<option value="${x}" ${String(item.ownershipType || "Owned") === x ? "selected" : ""}>${({
                                        Owned:"Własność",
                                        CoOwned:"Współwłasność",
                                        Leasehold:"Użytkowanie / dzierżawa",
                                        Rented:"Najem",
                                        Other:"Inne"
                                    })[x]}</option>`
                                ).join("")}
                            </select>
                        </label>
                        <label>Data nabycia
                            <input name="AcquiredOn" type="date" value="${esc(item.acquiredOn ? String(item.acquiredOn).slice(0,10) : "")}" />
                        </label>
                    </div>
                </div>

                <div class="property-details-group">
                    <h4>Własność i dokumenty</h4>
                    <div class="property-details-grid">
                        <label>Numer księgi wieczystej
                            <input name="LandRegisterNumber" value="${esc(d.landRegisterNumber || "")}" placeholder="np. PO1P/00000000/0" />
                        </label>
                        <label>Obręb ewidencyjny
                            <input name="CadastralDistrict" value="${esc(d.cadastralDistrict || "")}" />
                        </label>
                        <label>Właściciel główny
                            <select name="PrimaryOwnerPersonId">${ownerOptions(d.primaryOwnerPersonId)}</select>
                        </label>
                        <label>Udział właściciela
                            <input name="OwnershipShare" value="${esc(d.ownershipShare || "")}" placeholder="np. 1/1, 1/2" />
                        </label>
                        <label class="property-details-full">Współwłaściciele
                            <select name="CoOwnerPersonIds" multiple size="4">${coOwnerOptions(d.coOwnerPersonIds)}</select>
                            <small>Możesz zaznaczyć kilka osób (Ctrl / Cmd + kliknięcie).</small>
                        </label>
                    </div>
                </div>

                <div class="property-details-group">
                    <h4>Dodatkowe informacje</h4>
                    <label class="property-details-full">Uwagi
                        <textarea name="Notes" rows="3" placeholder="np. informacje prawne, podatkowe lub organizacyjne">${esc(d.notes || "")}</textarea>
                    </label>
                </div>

                <div class="property-details-actions">
                    <button type="button" class="btn btn-secondary" data-property-details-cancel>Anuluj</button>
                    <button type="submit" class="btn btn-primary">Zapisz dane działki</button>
                </div>
            </form>`;
    }

    function buildingForm(item) {
        const d = item.details || {};
        return `
            <form class="property-details-form" data-property-details-form="Building">
                <input type="hidden" name="ObjectId" value="${esc(item.id)}" />

                <div class="property-details-group">
                    <h4>Dane budynku</h4>
                    <div class="property-details-grid">
                        <label>Nazwa
                            <input name="Name" value="${esc(item.name)}" required />
                        </label>
                        <label>Adres budynku
                            <input name="AddressText" value="${esc(d.addressText || "")}" placeholder="jeśli inny lub bardziej szczegółowy niż adres działki" />
                        </label>
                        <label>Typ budynku
                            <select name="BuildingType">
                                ${["Residential","Rental","Mixed","Utility","Other"].map(x =>
                                    `<option value="${x}" ${String(item.buildingType || "") === x ? "selected" : ""}>${({
                                        Residential:"Mieszkalny",
                                        Rental:"Najem",
                                        Mixed:"Mieszany",
                                        Utility:"Gospodarczy / techniczny",
                                        Other:"Inny"
                                    })[x]}</option>`
                                ).join("")}
                            </select>
                        </label>
                        <label>Funkcja
                            <select name="FunctionType">
                                ${["FamilyHome","RentalHouse","Mixed","Utility","Other"].map(x =>
                                    `<option value="${x}" ${String(item.functionType || "") === x ? "selected" : ""}>${({
                                        FamilyHome:"Dom rodzinny",
                                        RentalHouse:"Dom na wynajem",
                                        Mixed:"Mieszany",
                                        Utility:"Gospodarczy / techniczny",
                                        Other:"Inna"
                                    })[x]}</option>`
                                ).join("")}
                            </select>
                        </label>
                        <label>Powierzchnia użytkowa m²
                            <input name="UsableArea" type="number" step="0.01" min="0" value="${esc(item.usableArea ?? "")}" />
                        </label>
                        <label>Liczba kondygnacji
                            <input name="Floors" type="number" min="0" value="${esc(item.floors ?? "")}" />
                        </label>
                        <label>Rok budowy
                            <input name="BuildYear" type="number" min="1000" max="2200" value="${esc(item.buildYear ?? "")}" />
                        </label>
                    </div>
                </div>

                <div class="property-details-group">
                    <h4>Własność i dokumenty</h4>
                    <div class="property-details-grid">
                        <label>Numer księgi wieczystej
                            <input name="LandRegisterNumber" value="${esc(d.landRegisterNumber || "")}" placeholder="jeśli budynek ma odrębne oznaczenie" />
                        </label>
                        <label>Obręb ewidencyjny
                            <input name="CadastralDistrict" value="${esc(d.cadastralDistrict || "")}" />
                        </label>
                        <label>Właściciel główny
                            <select name="PrimaryOwnerPersonId">${ownerOptions(d.primaryOwnerPersonId)}</select>
                        </label>
                        <label>Udział właściciela
                            <input name="OwnershipShare" value="${esc(d.ownershipShare || "")}" placeholder="np. 1/1, 1/2" />
                        </label>
                        <label class="property-details-full">Współwłaściciele
                            <select name="CoOwnerPersonIds" multiple size="4">${coOwnerOptions(d.coOwnerPersonIds)}</select>
                            <small>Możesz zaznaczyć kilka osób (Ctrl / Cmd + kliknięcie).</small>
                        </label>
                    </div>
                </div>

                <div class="property-details-group">
                    <h4>Dodatkowe informacje</h4>
                    <label class="property-details-full">Uwagi
                        <textarea name="Notes" rows="3">${esc(d.notes || "")}</textarea>
                    </label>
                </div>

                <div class="property-details-actions">
                    <button type="button" class="btn btn-secondary" data-property-details-cancel>Anuluj</button>
                    <button type="submit" class="btn btn-primary">Zapisz dane budynku</button>
                </div>
            </form>`;
    }

    function findItem(type, id) {
        const collection = type === "Parcel" ? state.parcels : state.buildings;
        return collection.find(x => String(x.id) === String(id));
    }

    function render() {
        document.querySelectorAll("[data-property-details-summary]").forEach(box => {
            const type = box.dataset.propertyDetailsSummary;
            const id = box.dataset.propertyDetailsId;
            const item = findItem(type, id);
            if (item) box.innerHTML = summaryHtml(type, item);
        });

        document.querySelectorAll("[data-property-details-edit]").forEach(button => {
            button.hidden = !state.canManage;
        });
    }

    function openEditor(type, id) {
        const item = findItem(type, id);
        const editor = document.querySelector(
            `[data-property-details-editor="${CSS.escape(type)}"][data-property-details-id="${CSS.escape(String(id))}"]`
        );
        if (!item || !editor) return;

        document.querySelectorAll(".property-details-editor:not([hidden])").forEach(other => {
            if (other !== editor) other.hidden = true;
        });

        editor.innerHTML = type === "Parcel" ? parcelForm(item) : buildingForm(item);
        editor.hidden = false;

        const form = editor.querySelector("[data-property-details-form]");
        const cancel = editor.querySelector("[data-property-details-cancel]");

        cancel?.addEventListener("click", () => {
            editor.hidden = true;
            editor.innerHTML = "";
        });

        form?.addEventListener("submit", async event => {
            event.preventDefault();
            const submit = form.querySelector('button[type="submit"]');
            submit.disabled = true;

            try {
                const fd = new FormData(form);
                if (token) fd.append("__RequestVerificationToken", token);

                const response = await fetch(
                    `${endpoint}/${type === "Parcel" ? "SaveParcel" : "SaveBuilding"}`,
                    {
                        method: "POST",
                        body: fd,
                        credentials: "same-origin"
                    });

                const result = await response.json().catch(() => ({}));
                if (!response.ok) {
                    throw new Error(result.message || "Nie udało się zapisać danych.");
                }

                window.alert(result.message || "Dane zapisano.");
                window.location.reload();
            } catch (error) {
                window.alert(error.message || "Nie udało się zapisać danych.");
                submit.disabled = false;
            }
        });

        editor.scrollIntoView({ behavior: "smooth", block: "nearest" });
    }

    async function init() {
        try {
            const response = await fetch(`${endpoint}/Data`, {
                credentials: "same-origin"
            });

            if (!response.ok) return;

            state = await response.json();
            render();

            document.querySelectorAll("[data-property-details-edit]").forEach(button => {
                button.addEventListener("click", () =>
                    openEditor(
                        button.dataset.propertyDetailsEdit,
                        button.dataset.propertyDetailsId
                    )
                );
            });
        } catch (error) {
            console.warn("Nie udało się wczytać szczegółów nieruchomości.", error);
        }
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
