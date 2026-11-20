// Phase 8 accounting forms: repeatable line editors (journal lines, rule lines, allocation lines) with live totals.
(function () {
    'use strict';
    function reindex(container) {
        var prefix = container.getAttribute('data-rows');
        var re = new RegExp(prefix.replace(/[.*+?^${}()|[\]\]/g, '\$&') + '\[\d+\]', 'g');
        var reId = new RegExp(prefix.replace(/[.*+?^${}()|[\]\]/g, '\$&') + '_\d+__', 'g');
        container.querySelectorAll('[data-row]').forEach(function (row, i) {
            row.querySelectorAll('[name]').forEach(function (el) { el.name = el.name.replace(re, prefix + '[' + i + ']'); });
            row.querySelectorAll('[id]').forEach(function (el) { el.id = el.id.replace(reId, prefix + '_' + i + '__'); });
            row.querySelectorAll('label[for]').forEach(function (el) { el.htmlFor = el.htmlFor.replace(reId, prefix + '_' + i + '__'); });
            var n = row.querySelector('[data-row-no]'); if (n) n.textContent = (i + 1);
        });
    }
    function num(v) { var x = parseFloat(String(v || '').replace(/,/g, '')); return isNaN(x) ? 0 : x; }
    function totals(container) {
        var form = container.closest('form'); if (!form) return;
        var d = 0, c = 0, p = 0;
        container.querySelectorAll('[data-debit]').forEach(function (el) { d += num(el.value); });
        container.querySelectorAll('[data-credit]').forEach(function (el) { c += num(el.value); });
        container.querySelectorAll('[data-percent]').forEach(function (el) { p += num(el.value); });
        var fmt = function (x) { return x.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }); };
        form.querySelectorAll('[data-total-debit]').forEach(function (el) { el.textContent = fmt(d); });
        form.querySelectorAll('[data-total-credit]').forEach(function (el) { el.textContent = fmt(c); });
        form.querySelectorAll('[data-total-diff]').forEach(function (el) {
            var diff = Math.round((d - c) * 100) / 100;
            el.textContent = diff === 0 ? 'Balanced' : 'Difference ' + fmt(Math.abs(diff));
            el.className = diff === 0 && d > 0 ? 'badge text-bg-success' : 'badge text-bg-warning';
        });
        form.querySelectorAll('[data-total-percent]').forEach(function (el) {
            el.textContent = p.toFixed(4).replace(/\.?0+$/, '') + '%';
            el.className = Math.abs(p - 100) < 0.00005 ? 'badge text-bg-success' : 'badge text-bg-warning';
        });
    }
    document.querySelectorAll('[data-rows]').forEach(function (container) {
        var tpl = document.getElementById(container.getAttribute('data-template'));
        container.addEventListener('click', function (e) {
            var btn = e.target.closest('[data-remove-row]'); if (!btn) return;
            if (container.querySelectorAll('[data-row]').length <= (parseInt(container.getAttribute('data-min') || '1', 10))) return;
            btn.closest('[data-row]').remove(); reindex(container); totals(container);
        });
        container.addEventListener('input', function () { totals(container); });
        var add = document.querySelector('[data-add-row="' + container.id + '"]');
        if (add && tpl) add.addEventListener('click', function () {
            var i = container.querySelectorAll('[data-row]').length;
            var html = tpl.innerHTML.replace(/__i__/g, i);
            container.insertAdjacentHTML('beforeend', html); reindex(container); totals(container);
        });
        totals(container);
    });
    // Posting rule form: show the amounts, conditions and event accounts the chosen event offers.
    var ev = document.getElementById('EventType');
    if (ev && window.postingCatalog) {
        var apply = function () {
            var def = window.postingCatalog[ev.value]; if (!def) return;
            document.querySelectorAll('select[data-amount-key]').forEach(function (s) {
                var cur = s.value; s.innerHTML = '';
                def.amounts.forEach(function (k) { var o = document.createElement('option'); o.value = k; o.textContent = k; if (k === cur) o.selected = true; s.appendChild(o); });
            });
            document.querySelectorAll('select[data-account-key]').forEach(function (s) {
                var cur = s.value; s.innerHTML = '<option value="">(fixed account)</option>';
                def.accounts.forEach(function (k) { var o = document.createElement('option'); o.value = k; o.textContent = k; if (k === cur) o.selected = true; s.appendChild(o); });
            });
            var cf = document.getElementById('ConditionField');
            if (cf) { var cur = cf.value; cf.innerHTML = '<option value="">No condition</option>';
                def.conditions.forEach(function (k) { var o = document.createElement('option'); o.value = k; o.textContent = k; if (k === cur) o.selected = true; cf.appendChild(o); }); }
            var help = document.getElementById('eventHelp'); if (help) help.textContent = def.spec;
        };
        ev.addEventListener('change', apply);
        document.addEventListener('click', function (e) { if (e.target.closest('[data-add-row]')) setTimeout(apply, 0); });
        apply();
    }
})();
