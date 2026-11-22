document.addEventListener('DOMContentLoaded', function () {
  // Toasts rendered from TempData
  document.querySelectorAll('.toast[data-auto-show]').forEach(function (el) { new bootstrap.Toast(el).show(); });

  // Loading state: disable submit and show spinner once the form is valid
  document.querySelectorAll('form[data-loading-form]').forEach(function (form) {
    form.addEventListener('submit', function () {
      if (window.jQuery && jQuery.validator && !jQuery(form).valid()) return;
      var btn = form.querySelector('button[type=submit]');
      if (!btn) return;
      var spinner = btn.querySelector('.spinner-border');
      if (spinner) spinner.classList.remove('d-none');
      setTimeout(function () { btn.disabled = true; }, 0);
    });
  });
});

// ---- Shell behaviour: theme, sidebar, collapsible nav groups ----
document.addEventListener('DOMContentLoaded', function () {
  var root = document.documentElement;
  function store(k, v) { try { localStorage.setItem(k, v); } catch (e) { } }
  function load(k) { try { return localStorage.getItem(k); } catch (e) { return null; } }

  var themeBtn = document.getElementById('themeToggle');
  function syncThemeIcon() {
    if (!themeBtn) return;
    var dark = root.getAttribute('data-bs-theme') === 'dark';
    themeBtn.innerHTML = '<i class="bi ' + (dark ? 'bi-sun' : 'bi-moon-stars') + '"></i>';
  }
  syncThemeIcon();
  if (themeBtn) themeBtn.addEventListener('click', function () {
    var next = root.getAttribute('data-bs-theme') === 'dark' ? 'light' : 'dark';
    root.setAttribute('data-bs-theme', next); store('erp-theme', next); syncThemeIcon();
  });

  var sb = document.getElementById('sidebarToggle');
  if (sb) sb.addEventListener('click', function () {
    var c = root.classList.toggle('sidebar-collapsed'); store('erp-sidebar', c ? 'collapsed' : 'open');
  });

  var openGroups = []; try { openGroups = JSON.parse(load('erp-nav-open') || '[]'); } catch (e) { }
  document.querySelectorAll('.nav-group').forEach(function (g) {
    var btn = g.querySelector('.nav-group-toggle'); var key = btn.textContent.trim();
    function set(open) { g.classList.toggle('open', open); btn.setAttribute('aria-expanded', open); }
    set(!!g.querySelector('.nav-link.active') || openGroups.indexOf(key) >= 0);
    btn.addEventListener('click', function () {
      if (root.classList.contains('sidebar-collapsed')) { root.classList.remove('sidebar-collapsed'); store('erp-sidebar', 'open'); set(true); }
      else set(!g.classList.contains('open'));
      var list = []; document.querySelectorAll('.nav-group.open .nav-group-toggle').forEach(function (b) { list.push(b.textContent.trim()); });
      store('erp-nav-open', JSON.stringify(list));
    });
  });
  var active = document.querySelector('.side-nav .nav-link.active');
  if (active && active.scrollIntoView) active.scrollIntoView({ block: 'nearest' });
});
