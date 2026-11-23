// AI assistant: floating widget (open/collapse on every page) and the full page share this code and one per-user conversation kept in this browser.
(function () {
  'use strict';
  var KEY_PREFIX = 'erp-assistant:';
  var MAX_STORED = 40;

  function safeGet(k) { try { return localStorage.getItem(k); } catch (e) { return null; } }
  function safeSet(k, v) { try { localStorage.setItem(k, v); } catch (e) { } }
  function safeDel(k) { try { localStorage.removeItem(k); } catch (e) { } }

  function esc(s) { return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;'); }

  // Minimal, safe markdown: everything is HTML-escaped first, then bold, code, bullet lists and pipe tables are rebuilt.
  function inline(s) { return esc(s).replace(/`([^`]+)`/g, '<code>$1</code>').replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>'); }
  function render(md) {
    var lines = String(md).replace(/\r/g, '').split('\n'), out = [], i = 0;
    while (i < lines.length) {
      var l = lines[i];
      if (/^\s*\|.*\|\s*$/.test(l) && i + 1 < lines.length && /^\s*\|[\s:|-]+\|\s*$/.test(lines[i + 1])) {
        var cells = function (r) { return r.trim().replace(/^\||\|$/g, '').split('|').map(function (c) { return c.trim(); }); };
        var head = cells(l); i += 2;
        var html = '<div class="table-responsive"><table class="table table-sm"><thead><tr>' + head.map(function (h) { return '<th>' + inline(h) + '</th>'; }).join('') + '</tr></thead><tbody>';
        while (i < lines.length && /^\s*\|.*\|\s*$/.test(lines[i])) { html += '<tr>' + cells(lines[i]).map(function (c) { return '<td>' + inline(c) + '</td>'; }).join('') + '</tr>'; i++; }
        out.push(html + '</tbody></table></div>');
      } else if (/^\s*[-*] /.test(l)) {
        var items = [];
        while (i < lines.length && /^\s*[-*] /.test(lines[i])) { items.push('<li>' + inline(lines[i].replace(/^\s*[-*] /, '')) + '</li>'); i++; }
        out.push('<ul>' + items.join('') + '</ul>');
      } else if (l.trim() === '') { i++; }
      else { out.push('<p>' + inline(l) + '</p>'); i++; }
    }
    return out.join('');
  }

  function init(root) {
    var mode = root.getAttribute('data-assistant');
    var userKey = KEY_PREFIX + root.getAttribute('data-user');
    var voice = root.getAttribute('data-voice') === '1';
    var q = function (r) { return root.querySelector('[data-role="' + r + '"]'); };
    var box = q('messages'), hint = q('hint'), status = q('status'), form = q('form'), input = q('input'), send = q('send'), mic = q('mic'), speak = q('speak'), clear = q('clear');
    var tokenInput = root.querySelector('input[name="__RequestVerificationToken"]');
    var state = { messages: [], speak: false };
    var busy = false, recorder = null, chunks = [], currentAudio = null;

    try { var saved = JSON.parse(safeGet(userKey) || 'null'); if (saved && Array.isArray(saved.messages)) state = { messages: saved.messages, speak: !!saved.speak }; } catch (e) { }
    function save() { state.messages = state.messages.slice(-MAX_STORED); safeSet(userKey, JSON.stringify(state)); }

    function setStatus(t, isError) { status.textContent = t || ''; status.classList.toggle('text-danger', !!isError); }
    function scroll() { box.scrollTop = box.scrollHeight; }
    function bubble(role, text, nav) {
      if (hint) hint.hidden = true;
      var d = document.createElement('div');
      d.className = 'assistant-msg ' + role;
      d.innerHTML = role === 'assistant' ? render(text) : '<p>' + esc(text) + '</p>';
      if (nav && nav.url) { var a = document.createElement('a'); a.href = nav.url; a.className = 'assistant-nav'; a.textContent = 'Open ' + nav.title; d.appendChild(a); }
      if (mode === 'widget' && role === 'assistant' && (text.length > 500 || /\|.*\|/.test(text))) {
        var f = document.createElement('a'); f.href = '/Assistant'; f.className = 'assistant-nav ms-2'; f.textContent = 'See full page'; d.appendChild(f);
      }
      box.appendChild(d); scroll(); return d;
    }
    function paint() {
      Array.prototype.slice.call(box.querySelectorAll('.assistant-msg')).forEach(function (n) { n.remove(); });
      state.messages.forEach(function (m) { bubble(m.role, m.content, m.nav); });
      if (hint) hint.hidden = state.messages.length > 0;
    }

    function headers(json) { var h = { 'RequestVerificationToken': tokenInput ? tokenInput.value : '' }; if (json) h['Content-Type'] = 'application/json'; return h; }
    async function fail(res) { var msg = 'Something went wrong.'; try { msg = (await res.json()).error || msg; } catch (e) { } throw new Error(msg); }

    async function ask(text) {
      if (busy || !text.trim()) return;
      busy = true; send.disabled = true;
      state.messages.push({ role: 'user', content: text.trim() }); save(); bubble('user', text.trim());
      var thinking = bubble('assistant thinking', 'Thinking…'); thinking.textContent = 'Thinking…';
      try {
        var res = await fetch('/Assistant/Ask', {
          method: 'POST', headers: headers(true), credentials: 'same-origin',
          body: JSON.stringify({ messages: state.messages.slice(-20).map(function (m) { return { role: m.role, content: m.content }; }), currentPath: location.pathname })
        });
        if (!res.ok) await fail(res);
        var data = await res.json();
        thinking.remove();
        var nav = data.navigate ? { url: data.navigate.url, title: data.navigate.title } : null;
        state.messages.push({ role: 'assistant', content: data.text, nav: nav }); save(); bubble('assistant', data.text, nav);
        setStatus('');
        if (state.speak) await say(data.text);
        if (nav) { setStatus('Opening ' + nav.title + '…'); setTimeout(function () { location.href = nav.url; }, state.speak ? 400 : 700); }
      } catch (e) {
        thinking.remove(); state.messages.pop(); save(); paint(); input.value = text; setStatus(e.message, true);
      } finally { busy = false; send.disabled = false; input.focus(); }
    }

    async function say(text) {
      try {
        var res = await fetch('/Assistant/Speak', { method: 'POST', headers: headers(true), credentials: 'same-origin', body: JSON.stringify({ text: text.replace(/[*`|#]/g, ' ') }) });
        if (!res.ok) return;
        var url = URL.createObjectURL(await res.blob());
        if (currentAudio) currentAudio.pause();
        currentAudio = new Audio(url);
        await currentAudio.play().catch(function () { });
      } catch (e) { }
    }

    // ---- voice input: record, stop, transcribe, then treat the transcript as typed text ----
    async function toggleMic() {
      if (recorder) { recorder.stop(); return; }
      if (!navigator.mediaDevices || !window.MediaRecorder) { setStatus('This browser cannot record audio.', true); return; }
      try {
        var stream = await navigator.mediaDevices.getUserMedia({ audio: true });
        recorder = new MediaRecorder(stream); chunks = [];
        recorder.ondataavailable = function (e) { if (e.data.size) chunks.push(e.data); };
        recorder.onstop = async function () {
          stream.getTracks().forEach(function (t) { t.stop(); });
          var type = recorder.mimeType || 'audio/webm'; recorder = null;
          mic.classList.remove('recording'); mic.innerHTML = '<i class="bi bi-mic"></i>';
          if (!chunks.length) { setStatus(''); return; }
          setStatus('Transcribing…');
          try {
            var fd = new FormData(); fd.append('audio', new Blob(chunks, { type: type }), 'speech.' + (type.indexOf('ogg') >= 0 ? 'ogg' : type.indexOf('mp4') >= 0 ? 'mp4' : 'webm'));
            var res = await fetch('/Assistant/Transcribe', { method: 'POST', headers: headers(false), credentials: 'same-origin', body: fd });
            if (!res.ok) await fail(res);
            var text = ((await res.json()).text || '').trim();
            if (!text) { setStatus('I did not hear anything. Try again.', true); return; }
            setStatus(''); await ask(text);
          } catch (e) { setStatus(e.message, true); }
        };
        recorder.start(); mic.classList.add('recording'); mic.innerHTML = '<i class="bi bi-stop-circle"></i>'; setStatus('Listening… click the microphone again to send.');
      } catch (e) { recorder = null; setStatus('Microphone access was denied or is unavailable (needs HTTPS or localhost).', true); }
    }

    form.addEventListener('submit', function (e) { e.preventDefault(); var t = input.value; input.value = ''; ask(t); });
    input.addEventListener('keydown', function (e) { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); form.requestSubmit(); } });
    if (mic) mic.addEventListener('click', toggleMic);
    if (speak) { speak.checked = state.speak; speak.addEventListener('change', function () { state.speak = speak.checked; save(); }); }
    clear.addEventListener('click', function () { state.messages = []; save(); paint(); setStatus(''); });
    paint();
    return { focus: function () { input.focus(); scroll(); } };
  }

  document.addEventListener('DOMContentLoaded', function () {
    // Forget the conversation on sign out (shared computers).
    var logout = document.querySelector('#logoutModal form');
    if (logout) logout.addEventListener('submit', function () { try { Object.keys(localStorage).filter(function (k) { return k.indexOf(KEY_PREFIX) === 0; }).forEach(safeDel); } catch (e) { } });

    var api = null;
    document.querySelectorAll('[data-assistant]').forEach(function (r) { var a = init(r); if (r.getAttribute('data-assistant') === 'widget') api = a; });
    var fab = document.getElementById('assistantFab'), panel = document.getElementById('assistantPanel');
    if (!fab || !panel) return;
    function setOpen(open) {
      panel.hidden = !open; fab.hidden = open; fab.setAttribute('aria-expanded', open ? 'true' : 'false');
      safeSet('erp-assistant-open', open ? '1' : '0');
      if (open && api) api.focus();
    }
    fab.addEventListener('click', function () { setOpen(true); });
    document.getElementById('assistantCollapse').addEventListener('click', function () { setOpen(false); });
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && !panel.hidden) setOpen(false); });
    setOpen(safeGet('erp-assistant-open') === '1');
  });
})();
