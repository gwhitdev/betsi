// Hub subscriptions run in the browser so they use the same cookie as the page.
window.betsiBoards = {
    subscribe(dotnet) {
        let disposed = false;
        let retry;
        const connection = new signalR.HubConnectionBuilder()
            .withUrl('/hub/boards').withAutomaticReconnect().build();
        const notify = (method, ...args) => disposed ? Promise.resolve() :
            dotnet.invokeMethodAsync(method, ...args).catch(() => {});
        connection.on('Changed', () => notify('Changed'));
        connection.onreconnecting(() => notify('ConnectionChanged', false));
        connection.onreconnected(() => notify('ConnectionChanged', true));
        connection.onclose(async () => {
            await notify('ConnectionChanged', false);
            if (!disposed) retry = setTimeout(start, 2000);
        });
        async function start() {
            if (disposed) return;
            try {
                await connection.start();
                await notify('ConnectionChanged', true);
            } catch {
                await notify('ConnectionChanged', false);
                if (!disposed) retry = setTimeout(start, 2000);
            }
        }
        void start();
        return { async dispose() {
            disposed = true;
            clearTimeout(retry);
            await connection.stop();
        } };
    }
};

(() => {
    const banner = document.getElementById('reconnect-banner');
    let recovering = false;
    let hiddenAt;
    const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
    async function recover() {
        if (recovering) return;
        recovering = true;
        banner.style.display = 'flex';
        document.querySelector('main')?.setAttribute('inert', '');
        // A fresh request revalidates login and refetches authorised data. Never hide the
        // warning and resume an old circuit with changes missed while asleep.
        for (;;) {
            try {
                if (navigator.onLine) {
                    const response = await fetch('/health/live', {
                        cache: 'no-store', signal: AbortSignal.timeout(3000)
                    });
                    if (response.ok) { location.reload(); return; }
                }
            } catch { /* Keep warning visible until the server is reachable. */ }
            await delay(2000);
        }
    }
    window.addEventListener('offline', recover);
    document.addEventListener('visibilitychange', () => {
        if (document.hidden) hiddenAt = Date.now();
        else if (hiddenAt && Date.now() - hiddenAt >= 30000) void recover();
    });
    Blazor.start({ circuit: { reconnectionHandler: {
        onConnectionDown: recover,
        onConnectionUp: () => { if (recovering) location.reload(); }
    } } });
})();
