(() => {
    const bell =
        document.querySelector(
            "[data-notification-bell]");

    if (!bell) {
        return;
    }

    const badge =
        bell.querySelector(
            "[data-notification-badge]");

    if (!badge) {
        return;
    }

    let lastCount = null;

    const setCount = count => {
        const numeric =
            Math.max(
                0,
                Number(count) || 0);

        badge.textContent =
            numeric > 99
                ? "99+"
                : String(numeric);

        badge.hidden =
            numeric <= 0;

        bell.classList.toggle(
            "has-unread",
            numeric > 0);

        bell.setAttribute(
            "aria-label",
            numeric > 0
                ? `Powiadomienia — ${numeric} nieodczytanych`
                : "Powiadomienia — brak nieodczytanych");

        bell.setAttribute(
            "title",
            numeric > 0
                ? `Powiadomienia: ${numeric} nieodczytanych`
                : "Powiadomienia");

        if (lastCount !== null
            && numeric > lastCount) {
            bell.classList.remove(
                "notification-bell-new");

            // Restart krótkiej animacji po pojawieniu się nowego
            // powiadomienia w trakcie pracy z aplikacją.
            void bell.offsetWidth;

            bell.classList.add(
                "notification-bell-new");

            window.setTimeout(
                () =>
                    bell.classList.remove(
                        "notification-bell-new"),
                1800);
        }

        lastCount =
            numeric;
    };

    const refresh = async () => {
        try {
            const response =
                await fetch(
                    "/Notifications/UnreadCount",
                    {
                        credentials:
                            "same-origin",
                        cache:
                            "no-store",
                        headers: {
                            "Accept":
                                "application/json"
                        }
                    });

            if (!response.ok) {
                return;
            }

            const data =
                await response.json();

            setCount(
                data.unreadCount);
        } catch {
            // Brak połączenia z endpointem nie może blokować
            // działania pozostałej części aplikacji.
        }
    };

    refresh();

    // Aktualizacja w trakcie pracy z aplikacją.
    window.setInterval(
        refresh,
        60000);

    // Po powrocie do karty odśwież od razu, bez czekania minuty.
    document.addEventListener(
        "visibilitychange",
        () => {
            if (!document.hidden) {
                refresh();
            }
        });
})();
