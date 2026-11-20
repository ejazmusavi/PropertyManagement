// Phase 5 screens: frequency-dependent fields, custom schedule entry rows, customer picker, amendment type switching and modal helpers.
(function () {
  'use strict';
  function $(s, r) { return (r || document).querySelector(s); }
  function $$(s, r) { return Array.prototype.slice.call((r || document).querySelectorAll(s)); }
  function selText(sel) { var o = sel.options[sel.selectedIndex]; return o ? o.text.trim() : ''; }
  function setEnabled(el, on) { $$('input,select,textarea', el).forEach(function (i) { i.disabled = !on; }); }

  // ----- frequency-dependent fields (scoped by the nearest [data-freq-scope]) -----
  function applyFrequency(sel) {
    var scope = sel.closest('[data-freq-scope]') || document, f = selText(sel);
    $$('[data-show-when-freq]', scope).forEach(function (el) {
      var on = el.getAttribute('data-show-when-freq').split(',').indexOf(f) >= 0;
      el.classList.toggle('d-none', !on);
      setEnabled(el, on);
    });
  }
  $$('.freq-select').forEach(function (sel) { applyFrequency(sel); sel.addEventListener('change', function () { applyFrequency(sel); }); });

  // ----- amount basis (plan components) -----
  var basis = $('.basis-select');
  function applyBasis() {
    var b = selText(basis);
    $$('[data-show-when-basis]').forEach(function (el) {
      var on = el.getAttribute('data-show-when-basis').split(',').indexOf(b) >= 0;
      el.classList.toggle('d-none', !on);
      setEnabled(el, on);
    });
  }
  if (basis) { applyBasis(); basis.addEventListener('change', applyBasis); }

  // ----- waiver: maximum percent only applies when waivers are allowed -----
  $$('.waiver-check').forEach(function (cb) {
    var scope = cb.closest('[data-freq-scope]') || document, pct = $('.waiver-pct', scope);
    function sync() { if (!pct) return; pct.readOnly = !cb.checked; if (!cb.checked) pct.value = '0'; }
    sync(); cb.addEventListener('change', sync);
  });

  // ----- custom entry rows -----
  $$('.entry-editor').forEach(function (box) {
    var rows = $('.entry-rows', box), tpl = $('template', box);
    $('.add-entry', box).addEventListener('click', function () {
      var i = $$('.entry-row', box).length, d = document.createElement('div');
      d.innerHTML = tpl.innerHTML.replace(/__I__/g, i).trim();
      rows.appendChild(d.firstElementChild);
    });
    box.addEventListener('click', function (e) {
      var b = e.target.closest('.remove-entry'); if (!b) return;
      b.closest('.entry-row').remove();
      $$('.entry-row', box).forEach(function (row, i) {
        $$('input', row).forEach(function (el) { if (el.name) el.name = el.name.replace(/\[\d+\]/, '[' + i + ']'); });
      });
    });
  });

  // ----- customer picker -----
  $$('.cust-picker').forEach(function (p) {
    var box = $('.cust-search', p), sel = $('.cust-select', p), label = $('.cust-label', p), timer;
    function setLabel() { if (label) label.value = sel.options[sel.selectedIndex] ? sel.options[sel.selectedIndex].textContent : ''; }
    sel.addEventListener('change', setLabel);
    box.addEventListener('input', function () {
      clearTimeout(timer);
      timer = setTimeout(function () {
        fetch(p.dataset.lookupUrl + '?term=' + encodeURIComponent(box.value), { credentials: 'same-origin' }).then(function (r) { return r.ok ? r.json() : []; }).then(function (rows) {
          var keep = sel.value ? sel.options[sel.selectedIndex].cloneNode(true) : null; sel.innerHTML = '';
          rows.forEach(function (x) { var o = document.createElement('option'); o.value = x.id; o.textContent = x.customerCode + ' – ' + x.fullName + ' (' + x.mobile + ')'; sel.appendChild(o); });
          if (!sel.options.length && keep) sel.appendChild(keep);
          if (!sel.options.length) { var o = document.createElement('option'); o.value = ''; o.textContent = 'No matches'; sel.appendChild(o); }
          setLabel();
        });
      }, 250);
    });
  });

  // ----- plan form: target fields by plan type -----
  var planType = $('.plan-type-select');
  function applyPlanType() {
    var t = selText(planType);
    $$('[data-show-when-plan]').forEach(function (el) {
      var on = el.getAttribute('data-show-when-plan').split(',').indexOf(t) >= 0;
      el.classList.toggle('d-none', !on);
      setEnabled(el, on);
    });
  }
  if (planType) { applyPlanType(); planType.addEventListener('change', applyPlanType); }

  // ----- amendment form: show only the section of the selected type -----
  var amType = $('#amendmentType');
  function showAmendmentSection() {
    var t = selText(amType);
    $$('.amend-section').forEach(function (s) {
      var on = s.getAttribute('data-type') === t;
      s.classList.toggle('d-none', !on);
      setEnabled(s, on);
    });
    $$('.amend-section:not(.d-none) .freq-select').forEach(applyFrequency);
  }
  if (amType) { showAmendmentSection(); amType.addEventListener('change', showAmendmentSection); }

  // ----- modals that carry ids and labels from the clicked button -----
  $$('[data-fill-modal]').forEach(function (m) {
    m.addEventListener('show.bs.modal', function (e) {
      var d = e.relatedTarget ? e.relatedTarget.dataset : {};
      $$('[data-fill]', m).forEach(function (el) {
        var v = d[el.getAttribute('data-fill')];
        if (v !== undefined) { if (el.tagName === 'INPUT') el.value = v; else el.textContent = v; }
      });
      var f = $('form', m); if (f && d.action) f.setAttribute('action', d.action);
    });
  });
})();
