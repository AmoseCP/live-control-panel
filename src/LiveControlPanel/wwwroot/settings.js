/*
 * Settings page (FR 6.5). Kept separate from the operator page so nothing here can be reached by
 * accident during a service, and gated behind the PIN.
 *
 * All visible text comes from i18n.js or from dual-language server messages via pick().
 */
(function () {
  'use strict';

  var L = window.LCP;
  var I = window.LCP_I18N;
  var t = I.t;
  var pick = I.pick;

  var settings = null;

  function value(id, v) {
    var element = document.getElementById(id);
    if (!element) return '';
    if (v !== undefined) { element.value = v == null ? '' : v; return v; }
    return element.value;
  }

  /* ---- language ---------------------------------------------------------- */

  I.apply();
  L.on('btn-lang', function () { I.toggle(); });

  // Everything built from data has to be rebuilt on a switch, not just the static labels.
  window.LCP_onLanguageChange = function () {
    if (!settings) return;
    loadAccessInfo();
    loadTemplates();
    loadAuthStatus();
  };

  /* ---- unlock ------------------------------------------------------------ */

  L.on('btn-unlock', function () {
    var pin = value('pin').trim();
    if (!pin) { L.toast(t('settings.pinEmpty'), 'bad'); return; }

    L.setSettingsPin(pin);
    L.api.get('/api/settings').then(function (result) {
      if (result.status === 403) {
        var reason = (result.data && result.data.reason) || '';

        // An access-code failure must not discard a PIN that may well be correct, and must not tell
        // the operator to go find the PIN when the link is what is stale.
        if (reason === 'code') {
          showAccessError(pick(result.data && result.data.message) || t('generic.badAccessCode'));
          return;
        }

        L.setSettingsPin('');
        L.toast(t('settings.pinWrong'), 'bad');
        return;
      }
      settings = result.data;
      L.show('pin-card', false);
      L.show('settings-body', true);
      fill();
      loadAccessInfo();
      loadTemplates();
      loadAuthStatus();
    });
  });

  L.on('btn-back', function () {
    location.href = 'index.html?k=' + encodeURIComponent(L.code);
  });

  /*
   * Replaces the PIN box rather than sitting behind it. A stale link makes the PIN unusable, so
   * leaving the box on screen invites the operator to blame the PIN — which is what happened.
   */
  function showAccessError(message) {
    L.text('access-error-message', message);
    L.show('access-error', true);
    L.show('pin-card', false);
    L.show('settings-body', false);
  }

  /*
   * Check the access code before asking for a PIN. /api/state needs only the code, so a stale link is
   * caught on arrival instead of after the operator has typed a PIN that could never have worked.
   */
  L.api.get('/api/state').then(function (result) {
    if (result.status === 403 && (result.data && result.data.reason) === 'code') {
      showAccessError(pick(result.data.message) || t('generic.badAccessCode'));
    }
  });

  /* ---- fill / save ------------------------------------------------------- */

  function fill() {
    if (!settings) return;

    value('yt-client-id', settings.youTube && settings.youTube.clientId);
    value('yt-client-secret', settings.youTube && settings.youTube.clientSecret);
    L.text('stream-id', settings.streamId || '—');

    value('tg-token', settings.telegramBotToken);
    value('tg-chat', settings.telegramChatId);
    value('tg-message', settings.telegramMessageDefault);

    var obs = settings.obs || {};
    value('obs-url', obs.url);
    value('obs-password', obs.password);
    value('obs-scene-camera', obs.sceneCamera);
    value('obs-scene-slides', obs.sceneSlides);
    value('obs-audio', obs.audioInputName);
    value('obs-video', (obs.videoSourceNames || []).join(', '));

    var slides = settings.slides || {};
    var enabled = document.getElementById('slides-enabled');
    if (enabled) enabled.checked = !!slides.enabled;
    value('slides-class', slides.windowClass);
    value('slides-title', slides.windowTitleRegex);
    value('slides-strategy', slides.strategy || 'PostMessage');

    value('default-description', settings.defaultDescription);
    value('default-thumbnail', settings.defaultThumbnail);

    var window_ = settings.matchWindow || {};
    value('window-before', window_.beforeMinutes);
    value('window-after', window_.afterMinutes);
  }

  L.on('btn-save', function () {
    var body = {
      streamId: settings.streamId,
      defaultDescription: value('default-description'),
      defaultThumbnail: value('default-thumbnail'),
      telegramBotToken: value('tg-token'),
      telegramChatId: value('tg-chat'),
      telegramMessageDefault: value('tg-message'),
      obs: {
        url: value('obs-url'),
        password: value('obs-password'),
        sceneCamera: value('obs-scene-camera'),
        sceneSlides: value('obs-scene-slides'),
        audioInputName: value('obs-audio'),
        videoSourceNames: splitList(value('obs-video'))
      },
      slides: {
        enabled: !!(document.getElementById('slides-enabled') || {}).checked,
        windowClass: value('slides-class'),
        windowTitleRegex: value('slides-title'),
        strategy: value('slides-strategy')
      },
      matchWindow: {
        beforeMinutes: intOr(value('window-before'), 60),
        afterMinutes: intOr(value('window-after'), 120)
      },
      youTube: {
        clientId: value('yt-client-id'),
        clientSecret: value('yt-client-secret'),
        assumedValidityDays: (settings.youTube && settings.youTube.assumedValidityDays) || 180
      }
    };

    var newPin = value('new-pin').trim();
    if (newPin) body.settingsPin = newPin;

    L.api.put('/api/settings', body).then(function (result) {
      report(result);
      if (result.ok && newPin) L.setSettingsPin(newPin);
    });
  });

  function splitList(text) {
    return (text || '').split(',').map(function (s) { return s.trim(); })
      .filter(function (s) { return s.length > 0; });
  }

  // parseInt(x) || fallback turns a legitimate 0 into the fallback; 0 minutes is a valid window edge.
  function intOr(text, fallback) {
    var n = parseInt(text, 10);
    return isNaN(n) ? fallback : n;
  }

  /* ---- access info ------------------------------------------------------- */

  function loadAccessInfo() {
    L.api.get('/api/access-info').then(function (result) {
      var info = result.data;
      if (!info) return;

      var host = document.getElementById('access-addresses');
      host.innerHTML = '';

      info.addresses.forEach(function (address) {
        host.appendChild(addressRow(address.url, address.adapterName));
      });
      if (info.mdnsUrl) host.appendChild(addressRow(info.mdnsUrl, t('settings.accessMdns')));
      host.appendChild(addressRow(info.localUrl, t('settings.accessLocal')));

      if (info.addresses.length === 0) {
        var warning = document.createElement('p');
        warning.className = 'subtle';
        warning.textContent = t('settings.accessNone');
        host.appendChild(warning);
      }

      var qr = document.getElementById('qr-holder');
      qr.innerHTML = '';
      var image = document.createElement('img');
      image.alt = t('settings.qrAlt');
      image.src = 'data:image/png;base64,' + info.qrPngBase64;
      qr.appendChild(image);
    });
  }

  function addressRow(url, label) {
    var wrapper = document.createElement('div');
    wrapper.style.marginBottom = '10px';

    var caption = document.createElement('label');
    caption.textContent = label;
    wrapper.appendChild(caption);

    var box = document.createElement('div');
    box.className = 'linkbox';
    box.textContent = url;
    wrapper.appendChild(box);

    var copy = document.createElement('button');
    copy.className = 'ghost';
    copy.textContent = t('settings.copyAddress');
    copy.addEventListener('click', function () { L.copyText(url); });
    wrapper.appendChild(copy);

    return wrapper;
  }

  /* ---- auth -------------------------------------------------------------- */

  function loadAuthStatus() {
    L.api.get('/api/state').then(function (result) {
      var auth = result.data && result.data.auth;
      if (!auth) return;

      L.text('auth-status', !auth.valid ? t('settings.authNone')
        : auth.expiresInDays != null ? t('settings.authOkDays', { n: auth.expiresInDays })
        : t('settings.authOk'));
    });
  }

  // Full page load, not fetch: this is a redirect to Google's consent screen.
  L.on('btn-authorize', function () {
    location.href = '/auth/start?k=' + encodeURIComponent(L.code);
  });

  L.on('btn-revoke', function () {
    if (!window.confirm(t('settings.confirmRevoke'))) return;
    L.api.post('/api/auth/revoke').then(function (result) {
      report(result);
      loadAuthStatus();
    });
  });

  /* ---- stream key -------------------------------------------------------- */

  L.on('btn-create-key', function () {
    if (!window.confirm(t('settings.confirmKey'))) return;

    L.api.post('/api/stream-key/create').then(function (result) {
      var data = result.data || {};
      report(result);
      if (!data.ingestionKey) return;

      settings.streamId = data.streamId;
      L.text('stream-id', data.streamId);
      L.text('ingest-address', data.ingestionAddress);
      L.text('ingest-key', data.ingestionKey);
      L.show('stream-key-result', true);
    });
  });

  L.on('btn-copy-key', function () {
    var element = document.getElementById('ingest-key');
    if (element) L.copyText(element.textContent);
  });

  /* ---- telegram test ----------------------------------------------------- */

  L.on('btn-test-telegram', function () {
    // Save first, otherwise the test uses whatever was stored before this edit.
    L.api.put('/api/settings', {
      telegramBotToken: value('tg-token'),
      telegramChatId: value('tg-chat'),
      telegramMessageDefault: value('tg-message')
    }).then(function () {
      return L.api.post('/api/telegram/test');
    }).then(report);
  });

  /* ---- diagnostics ------------------------------------------------------- */

  L.on('btn-list-inputs', function () {
    L.api.get('/api/diag/obs-inputs').then(function (result) {
      var element = document.getElementById('obs-inputs');
      if (!element) return;

      L.show('obs-inputs', true);
      element.textContent = Array.isArray(result.data)
        ? result.data.join('\n')
        : (result.data && pick(result.data.message)) || t('settings.readFailed');
    });
  });

  // Reports which of the two paging paths actually works on this machine, so enabling the feature is
  // an informed decision rather than a guess.
  L.on('btn-probe-com', function () {
    var out = document.getElementById('com-probe-out');
    if (out) { L.show('com-probe-out', true); out.textContent = t('settings.slidesProbing'); }

    L.api.get('/api/diag/slides').then(function (result) {
      var d = result.data || {};
      var lines = [
        t('settings.probeSession') + ': ' + d.sessionId +
          '（' + t(d.sessionIsolated ? 'settings.probeSessionBad' : 'settings.probeSessionOk') + '）',
        t('settings.probeCom') + ': ' + (d.comProgId || t('settings.probeComNone')),
        t('settings.probePresenting') + ': ' + t(d.slideShowRunning ? 'settings.yes' : 'settings.no'),
        t('settings.probePage') + ': ' + (d.current == null
          ? t('settings.probePageNone') : d.current + ' / ' + d.total),
        t('settings.probePreview') + ': ' +
          t(d.previewSupported ? 'settings.probeAvailable' : 'settings.probeUnavailable'),
        t('settings.probeWindow') + ': ' +
          t(d.targetWindowFound ? 'settings.probeWindowFound' : 'settings.probeWindowNone'),
        d.message ? t('settings.probeNote') + ': ' + pick(d.message) : ''
      ];
      return L.api.get('/api/diag/com-probe').then(function (probe) {
        var detail = (probe.data && probe.data.report) || '';
        if (out) out.textContent = lines.filter(Boolean).join('\n') +
          (detail ? '\n\n' + t('settings.probeSteps') + ':\n' + detail.split(' | ').join('\n') : '');
      });
    }).catch(function () {
      if (out) out.textContent = t('settings.slidesProbeFailed');
    });
  });

  L.on('btn-list-windows', function () {
    L.api.get('/api/diag/windows').then(function (result) {
      var host = document.getElementById('windows-list');
      if (!host || !Array.isArray(result.data)) return;

      host.innerHTML = '';
      result.data.filter(function (w) { return w.visible && (w.title || w.className); })
        .forEach(function (w) {
          var button = document.createElement('button');
          button.className = 'ghost';
          button.style.textAlign = 'left';
          button.textContent = w.className + (w.title ? '　—　' + w.title : '');
          button.addEventListener('click', function () {
            value('slides-class', w.className);
            L.toast(t('settings.slidesFilled', { name: w.className }), 'good');
          });
          host.appendChild(button);
        });
    });
  });

  /* ---- shared ------------------------------------------------------------ */

  function report(result) {
    var data = result.data || {};
    var message = pick(data.message) || t(result.ok ? 'generic.done' : 'generic.failed');
    L.toast(message, data.ok === false || !result.ok ? 'bad' : 'good');
  }

  function loadTemplates() {
    L.api.get('/api/templates').then(function (result) {
      var body = document.getElementById('templates-body');
      if (!body || !Array.isArray(result.data)) return;

      body.innerHTML = '';
      result.data.forEach(function (template) {
        var row = document.createElement('tr');
        var name = template.id === 'custom'
          ? (template.name || '') + t('settings.adHocRow')
          : template.name;
        [name, (template.weekdays || []).join(','), template.startTime || '—']
          .forEach(function (cell) {
            var td = document.createElement('td');
            td.textContent = cell;
            row.appendChild(td);
          });
        body.appendChild(row);
      });
    });
  }
})();
