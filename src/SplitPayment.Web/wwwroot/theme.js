(function () {
    var key = 'sp-theme';
    var root = document.documentElement;
    var stored = null;
    try { stored = localStorage.getItem(key); } catch (e) { }
    var prefersLight = window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches;
    root.setAttribute('data-theme', stored === 'light' || stored === 'dark' ? stored : (prefersLight ? 'light' : 'dark'));

    document.addEventListener('click', function (event) {
        var target = event.target;
        if (!target || !target.closest || !target.closest('[data-theme-toggle]')) { return; }
        var next = root.getAttribute('data-theme') === 'light' ? 'dark' : 'light';
        root.setAttribute('data-theme', next);
        try { localStorage.setItem(key, next); } catch (e) { }
    });

    document.addEventListener('click', function (event) {
        var button = event.target && event.target.closest ? event.target.closest('[data-copy]') : null;
        if (!button || !navigator.clipboard) { return; }
        navigator.clipboard.writeText(button.getAttribute('data-copy')).then(function () {
            var previous = button.textContent;
            button.textContent = 'Copiado!';
            setTimeout(function () { button.textContent = previous; }, 1500);
        });
    });
})();
