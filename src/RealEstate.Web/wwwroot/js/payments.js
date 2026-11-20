// Payment allocation editor: shows the charge picker only for charge-specific allocation and the per-line amounts only for manual/specific allocation.
(function () {
  'use strict';
  document.querySelectorAll('[data-allocation-editor]').forEach(function (editor) {
    var mode = editor.querySelector('[data-allocation-mode]');
    if (!mode) return;
    function refresh() {
      var v = mode.value;
      editor.querySelectorAll('[data-show-when-mode]').forEach(function (el) {
        var show = el.getAttribute('data-show-when-mode').split(',').indexOf(v) >= 0;
        el.classList.toggle('d-none', !show);
        el.querySelectorAll('input,select').forEach(function (i) { i.disabled = !show; });
      });
      var total = 0;
      editor.querySelectorAll('[data-line-amount]').forEach(function (i) { var n = parseFloat(i.value); if (!isNaN(n)) total += n; });
      var t = editor.querySelector('[data-lines-total]');
      if (t) t.textContent = total.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    }
    mode.addEventListener('change', refresh);
    editor.addEventListener('input', function (e) { if (e.target.hasAttribute('data-line-amount')) refresh(); });
    editor.querySelectorAll('[data-fill-line]').forEach(function (b) {
      b.addEventListener('click', function () {
        var input = editor.querySelector('[data-line-amount][data-item="' + b.getAttribute('data-fill-line') + '"]');
        if (input) { input.value = b.getAttribute('data-outstanding'); refresh(); }
      });
    });
    var override = document.querySelector('[data-allocation-override]');
    if (override) {
      var toggle = function () { editor.classList.toggle('d-none', !override.checked); editor.querySelectorAll('input,select').forEach(function (i) { if (!override.checked) i.disabled = true; }); if (override.checked) refresh(); };
      override.addEventListener('change', toggle);
      toggle();
    } else {
      refresh();
    }
  });
  // Cheque fields only for cheque payments.
  var method = document.querySelector('[data-payment-method]');
  if (method) {
    var sync = function () {
      var opt = method.options[method.selectedIndex];
      var kind = opt ? opt.getAttribute('data-kind') : '';
      document.querySelectorAll('[data-cheque-only]').forEach(function (el) { el.classList.toggle('d-none', kind !== 'Cheque'); });
      var ref = document.querySelector('[data-reference-required]');
      if (ref) ref.classList.toggle('d-none', !(opt && opt.getAttribute('data-ref') === 'true'));
    };
    method.addEventListener('change', sync);
    sync();
  }
})();
