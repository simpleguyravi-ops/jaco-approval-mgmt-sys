// Platform standard: never use window.confirm()/alert() -- always this in-app modal.
// Usage: <form data-confirm="Message to show the user"> ... </form>
// For a dynamic message, update the form's data-confirm attribute via JS before the user submits.
(function () {
    function showConfirm(message) {
        return new Promise(function (resolve) {
            var overlay = document.createElement('div');
            overlay.className = 'jaco-confirm-overlay';
            overlay.innerHTML =
                '<div class="jaco-confirm-modal" role="alertdialog" aria-modal="true">' +
                    '<p></p>' +
                    '<div class="jaco-confirm-actions">' +
                        '<button type="button" class="jaco-btn jaco-btn-outline" data-act="cancel">Cancel</button>' +
                        '<button type="button" class="jaco-btn jaco-btn-primary" data-act="ok">OK</button>' +
                    '</div>' +
                '</div>';
            overlay.querySelector('p').textContent = message;
            document.body.appendChild(overlay);

            function close(result) {
                document.body.removeChild(overlay);
                document.removeEventListener('keydown', onKey);
                resolve(result);
            }
            function onKey(e) { if (e.key === 'Escape') close(false); }

            document.addEventListener('keydown', onKey);
            overlay.addEventListener('click', function (e) { if (e.target === overlay) close(false); });
            overlay.querySelector('[data-act="cancel"]').addEventListener('click', function () { close(false); });
            overlay.querySelector('[data-act="ok"]').addEventListener('click', function () { close(true); });
            overlay.querySelector('[data-act="ok"]').focus();
        });
    }

    document.addEventListener('submit', function (e) {
        var form = e.target;
        if (!(form instanceof HTMLFormElement)) return;
        var message = form.getAttribute('data-confirm');
        if (!message || form.dataset.jacoConfirmed === '1') return;
        e.preventDefault();
        showConfirm(message).then(function (ok) {
            if (ok) {
                form.dataset.jacoConfirmed = '1';
                form.submit();
            }
        });
    }, true);

    window.jacoConfirm = showConfirm;
})();
