(function () {
    var el = document.getElementById('blazor-error-ui');
    if (!el) return;
    el.addEventListener('click', function (e) {
        if (e.target.classList.contains('dismiss')) {
            el.classList.remove('show');
        }
    });
})();
