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
