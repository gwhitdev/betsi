// The only script this interface has. Blazor's default reconnection UI is a styled overlay that
// says little; a ward screen needs to be told, in words, that what it shows is no longer live.
(function () {
    const banner = document.getElementById('reconnect-banner');
    if (!banner) return;

    const show = () => { banner.style.display = 'flex'; };
    const hide = () => { banner.style.display = 'none'; };

    window.addEventListener('beforeunload', hide);

    Blazor.start({
        circuit: {
            reconnectionHandler: {
                onConnectionDown: show,
                onConnectionUp: hide
            }
        }
    });
})();
