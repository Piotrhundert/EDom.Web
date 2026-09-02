(() => {
    const form = document.getElementById("operatorInvoiceForm");
    if (!form) return;

    const q = name => form.querySelector(`[name="${name}"]`);
    const tariff = document.getElementById("tariffCode");
    const nightZone = document.getElementById("nightZone");

    const num = name => {
        const value = Number.parseFloat(q(name)?.value?.replace(",", ".") || "0");
        return Number.isFinite(value) ? value : 0;
    };

    const money = value => new Intl.NumberFormat("pl-PL", {
        minimumFractionDigits: 2,
        maximumFractionDigits: 2
    }).format(value) + " PLN";

    const quantity = value => new Intl.NumberFormat("pl-PL", {
        minimumFractionDigits: 0,
        maximumFractionDigits: 3
    }).format(value) + " kWh";

    const round2 = value => Math.round((value + Number.EPSILON) * 100) / 100;

    function line(label, zone, qty, rate, unit = "kWh") {
        if (qty <= 0 || rate <= 0) return null;
        return {
            label, zone, qty, rate, unit,
            amount: round2(qty * rate)
        };
    }

    function recalc() {
        const isG12 = tariff.value === "G12";
        const day = Math.max(0, num("CurrentDay") - num("PreviousDay"));
        const night = isG12
            ? Math.max(0, num("CurrentNight") - num("PreviousNight"))
            : 0;
        const months = Math.max(0, num("BillingMonths"));

        document.getElementById("dayConsumption").textContent = quantity(day);
        document.getElementById("nightConsumption").textContent = quantity(night);
        document.getElementById("totalConsumption").textContent = quantity(day + night);

        const lines = [];

        if (isG12) {
            lines.push(
                line("Energia czynna", "DAY", day, num("EnergyDayRate")),
                line("Energia czynna", "NIGHT", night, num("EnergyNightRate")),
                line("Opłata jakościowa", "DAY", day, num("QualityRate")),
                line("Opłata jakościowa", "NIGHT", night, num("QualityRate")),
                line("Opłata zmienna sieciowa", "DAY", day, num("VariableNetworkDayRate")),
                line("Opłata zmienna sieciowa", "NIGHT", night, num("VariableNetworkNightRate")),
                line("Opłata OZE", "DAY", day, num("OzeRate")),
                line("Opłata OZE", "NIGHT", night, num("OzeRate")),
                line("Opłata kogeneracyjna", "DAY", day, num("CogenerationRate")),
                line("Opłata kogeneracyjna", "NIGHT", night, num("CogenerationRate"))
            );
        } else {
            lines.push(
                line("Energia czynna", "ALL", day, num("EnergyDayRate")),
                line("Opłata jakościowa", "ALL", day, num("QualityRate")),
                line("Opłata zmienna sieciowa", "ALL", day, num("VariableNetworkDayRate")),
                line("Opłata OZE", "ALL", day, num("OzeRate")),
                line("Opłata kogeneracyjna", "ALL", day, num("CogenerationRate"))
            );
        }

        lines.push(
            line("Opłata stała sieciowa", "ALL", months, num("FixedNetworkMonthlyRate"), "mies."),
            line("Opłata abonamentowa", "ALL", months, num("SubscriptionMonthlyRate"), "mies."),
            line("Opłata mocowa", "ALL", months, num("CapacityMonthlyRate"), "mies.")
        );

        const active = lines.filter(Boolean);
        const tbody = document.getElementById("componentPreview");

        if (!active.length) {
            tbody.innerHTML = '<tr><td colspan="5">Wprowadź odczyty i stawki.</td></tr>';
        } else {
            tbody.innerHTML = active.map(x => `
                <tr>
                    <td>${x.label}</td>
                    <td>${x.zone}</td>
                    <td>${new Intl.NumberFormat("pl-PL", {maximumFractionDigits:3}).format(x.qty)} ${x.unit}</td>
                    <td>${new Intl.NumberFormat("pl-PL", {minimumFractionDigits:4, maximumFractionDigits:6}).format(x.rate)}</td>
                    <td>${money(x.amount)}</td>
                </tr>`).join("");
        }

        const net = round2(active.reduce((sum, x) => sum + x.amount, 0));
        const vat = round2(net * Math.max(0, num("VatRate")) / 100);
        const gross = round2(net + vat);

        document.getElementById("netTotal").textContent = money(net);
        document.getElementById("vatTotal").textContent = money(vat);
        document.getElementById("grossTotal").textContent = money(gross);
    }

    function syncTariff() {
        const isG12 = tariff.value === "G12";
        nightZone.hidden = !isG12;
        form.querySelectorAll(".g12-only").forEach(x => x.hidden = !isG12);

        document.getElementById("dayZoneTitle").textContent = isG12 ? "Strefa dzienna" : "Strefa ALL";
        document.getElementById("energyDayLabel").textContent = isG12 ? "Dzienna zł/kWh" : "Energia zł/kWh";
        document.getElementById("networkDayLabel").textContent = isG12 ? "Sieciowa dzienna zł/kWh" : "Sieciowa zł/kWh";

        recalc();
    }

    tariff.addEventListener("change", syncTariff);
    form.querySelectorAll("[data-calc]").forEach(input => input.addEventListener("input", recalc));
    form.querySelectorAll("input[name]").forEach(input => {
        if (!input.hasAttribute("data-calc") && input.type === "number") {
            input.addEventListener("input", recalc);
        }
    });

    syncTariff();
})();
