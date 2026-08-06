/*
 * Shared helpers for the operator page and the settings page.
 * Plain ES5-ish syntax on purpose: this has to run in the old Chromium inside OBS's browser dock
 * and on several iPad generations, so no modules, no optional chaining, no fetch niceties.
 */
(function (global) {
  'use strict';

  /* ---- access code -------------------------------------------------------
   * The QR/home-screen URL carries ?k=CODE. It is remembered so that navigating
   * between pages, or reopening from the home screen, keeps working. FR 6.4.
   */
  function accessCode() {
    var match = /[?&]k=([^&]+)/.exec(global.location.search);
    if (match) {
      var code = decodeURIComponent(match[1]);
      try { global.localStorage.setItem('lcp_k', code); } catch (e) { /* private mode */ }
      return code;
    }
    try { return global.localStorage.getItem('lcp_k') || ''; } catch (e) { return ''; }
  }

  var CODE = accessCode();

  function settingsPin() {
    try { return global.sessionStorage.getItem('lcp_pin') || ''; } catch (e) { return ''; }
  }

  function setSettingsPin(pin) {
    try { global.sessionStorage.setItem('lcp_pin', pin); } catch (e) { /* ignore */ }
  }

  /* ---- api --------------------------------------------------------------- */

  function request(method, path, body) {
    var headers = { 'X-Access-Code': CODE };
    var pin = settingsPin();
    if (pin) headers['X-Settings-Pin'] = pin;
    if (body !== undefined) headers['Content-Type'] = 'application/json';

    return fetch(path, {
      method: method,
      headers: headers,
      body: body === undefined ? undefined : JSON.stringify(body)
    }).then(function (response) {
      return response.text().then(function (text) {
        var data = null;
        if (text) { try { data = JSON.parse(text); } catch (e) { data = null; } }
        return { status: response.status, ok: response.ok, data: data };
      });
    }).catch(function () {
      // A network failure must resolve, not reject: every caller does its cleanup — most
      // importantly re-enabling the start button — inside .then(). An unhandled rejection at the
      // moment the panel restarts left the button disabled until someone thought to reload.
      return { status: 0, ok: false, data: null };
    });
  }

  var api = {
    get: function (path) { return request('GET', path); },
    post: function (path, body) { return request('POST', path, body === undefined ? {} : body); },
    put: function (path, body) { return request('PUT', path, body); }
  };

  /* ---- toast ------------------------------------------------------------- */

  var toastTimer = null;

  function toast(message, kind) {
    var element = document.getElementById('toast');
    if (!element) return;

    element.textContent = message;
    element.className = kind || '';
    if (toastTimer) global.clearTimeout(toastTimer);
    toastTimer = global.setTimeout(function () { element.className = 'hidden'; }, 6000);
  }

  /* ---- copy -------------------------------------------------------------
   * FR 6.3: one shared implementation, so the fallback path exists in exactly one
   * place. navigator.clipboard needs a secure context, which a plain-http LAN
   * address is not, so execCommand('copy') is the real path on the iPads. If both
   * fail we say so — a silent failure is the one outcome that is not allowed.
   */
  function copyText(text) {
    if (global.navigator.clipboard && global.isSecureContext) {
      global.navigator.clipboard.writeText(text).then(function () {
        var i18n = global.LCP_I18N;
        toast(i18n ? i18n.t('link.copied') : 'Copied.', 'good');
      }, function () {
        legacyCopy(text);
      });
      return;
    }
    legacyCopy(text);
  }

  function legacyCopy(text) {
    var area = document.createElement('textarea');
    area.value = text;
    area.setAttribute('readonly', 'readonly');
    area.style.position = 'fixed';
    area.style.top = '0';
    area.style.opacity = '0';
    document.body.appendChild(area);

    var ok = false;
    try {
      area.focus();
      area.setSelectionRange(0, area.value.length);   // iOS needs an explicit range
      ok = document.execCommand('copy');
    } catch (e) {
      ok = false;
    }
    document.body.removeChild(area);

    var i18n = global.LCP_I18N;
    toast(i18n ? i18n.t(ok ? 'link.copied' : 'link.copyFailed') : (ok ? 'Copied.' : 'Copy failed.'),
      ok ? 'good' : 'bad');
  }

  /* ---- misc -------------------------------------------------------------- */

  function on(id, handler) {
    var element = document.getElementById(id);
    if (element) element.addEventListener('click', handler);
  }

  function show(id, visible) {
    var element = document.getElementById(id);
    if (element) element.classList.toggle('hidden', !visible);
  }

  function text(id, value) {
    var element = document.getElementById(id);
    if (element) element.textContent = value == null ? '' : String(value);
  }

  function pad2(n) { return (n < 10 ? '0' : '') + n; }

  function clockTime(iso) {
    if (!iso) return '';
    var date = new Date(iso);
    if (isNaN(date.getTime())) return '';
    return pad2(date.getHours()) + ':' + pad2(date.getMinutes());
  }

  function duration(seconds) {
    if (!seconds || seconds < 0) seconds = 0;
    var h = Math.floor(seconds / 3600);
    var m = Math.floor((seconds % 3600) / 60);
    var s = Math.floor(seconds % 60);
    return (h > 0 ? h + ':' + pad2(m) : m) + ':' + pad2(s);
  }

  global.LCP = {
    code: CODE,
    api: api,
    toast: toast,
    copyText: copyText,
    on: on,
    show: show,
    text: text,
    clockTime: clockTime,
    duration: duration,
    settingsPin: settingsPin,
    setSettingsPin: setSettingsPin
  };
})(window);
