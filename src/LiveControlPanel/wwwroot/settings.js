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
    // The device list carries a translated "(none selected)" entry, so it is rebuilt too.
    loadCaptureDevices();
    // The device lists carry a translated "(system default)" entry, so they are rebuilt too.
    loadAudioDevices();
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

      // A network failure resolves as {status: 0, data: null} rather than rejecting. Treating that
      // as success "unlocked" into an empty shell — every card visible, nothing in any of them, no
      // message — which happens exactly when the panel is restarting. Stay on the PIN card instead.
      if (!result.ok || !result.data) {
        L.toast(t('settings.loadFailed'), 'bad');
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
    value('obs-frozen', (obs.frozenFrameSourceNames || []).join(', '));

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

    fillCaptureReset();
    fillTranslation();
  }

  /* ---- capture-card recovery --------------------------------------------- */

  function fillCaptureReset() {
    var capture = settings.captureReset || {};

    var enabled = document.getElementById('capture-enabled');
    if (enabled) enabled.checked = !!capture.enabled;

    value('capture-obs-input', capture.obsInputName);
    loadCaptureDevices();
  }

  /*
   * Present devices only, and the class-dangerous ones are filtered out server-side. The picker
   * shows the class alongside the name because "AVerMedia HDMI Capture" and the same card's audio
   * endpoint look nearly identical by name alone.
   */
  function loadCaptureDevices() {
    L.api.get('/api/diag/usb-devices').then(function (result) {
      var element = document.getElementById('capture-device');
      if (!element) return;

      var capture = (settings && settings.captureReset) || {};
      var devices = result.data;

      if (!Array.isArray(devices)) {
        L.toast(t('settings.captureDevicesFailed'), 'bad');
        return;
      }

      element.innerHTML = '';
      element.appendChild(deviceOption('', t('settings.captureDeviceNone')));

      var found = false;
      devices.forEach(function (device) {
        var label = device.name + '　·　' + (device.class || '?') +
          (device.status && device.status !== 'OK' ? '　·　' + device.status : '');
        element.appendChild(deviceOption(device.instanceId, label));
        if (device.instanceId === capture.deviceInstanceId) found = true;
      });

      // A configured device Windows no longer reports stays visible and stays selected, so the
      // admin can see *what* is missing instead of the field silently emptying itself.
      if (capture.deviceInstanceId && !found) {
        element.appendChild(deviceOption(capture.deviceInstanceId,
          (capture.deviceName || capture.deviceInstanceId) + '　·　（?）'));
      }

      element.value = capture.deviceInstanceId || '';
    });
  }

  function deviceOption(v, label) {
    var element = document.createElement('option');
    element.value = v;
    element.textContent = label;
    return element;
  }

  /* ---- AI translation ---------------------------------------------------- */

  function fillTranslation() {
    var tr = settings.translation || {};

    var enabled = document.getElementById('tr-enabled');
    if (enabled) enabled.checked = !!tr.enabled;
    var echo = document.getElementById('tr-echo');
    if (echo) echo.checked = tr.echoTargetLanguage !== false;

    value('tr-api-key', tr.apiKey);
    value('tr-model', tr.model);
    value('tr-target', tr.targetLanguage);
    value('tr-suffix', tr.titleSuffix);
    value('tr-obs-input', tr.obsInputName);
    value('tr-track', tr.obsAudioTrack || 2);
    L.text('tr-stream-id', tr.streamId || '—');

    loadAudioDevices();
  }

  /*
   * Device ids, not names: Windows reports the same friendly name for two different endpoints often
   * enough, and the id is what the service actually opens. The list is re-read every time this page
   * loads because a mixer moved to another USB port comes back with a new id.
   */
  function loadAudioDevices() {
    L.api.get('/api/audio-devices').then(function (result) {
      var data = result.data;
      var tr = (settings && settings.translation) || {};

      if (!data) {
        L.toast(t('settings.translationDevicesFailed'), 'bad');
        return;
      }

      fillDevices('tr-capture', data.capture || [], tr.captureDeviceId,
        t('settings.translationDefaultDevice'));
      fillDevices('tr-playback', data.playback || [], tr.playbackDeviceId, '—');
    });
  }

  function fillDevices(id, devices, selected, emptyLabel) {
    var element = document.getElementById(id);
    if (!element) return;

    element.innerHTML = '';
    element.appendChild(option('', emptyLabel));

    var found = false;
    devices.forEach(function (device) {
      element.appendChild(option(device.id, device.name + (device.isDefault ? ' ★' : '')));
      if (device.id === selected) found = true;
    });

    // A configured device that Windows no longer reports must stay visible and stay selected —
    // silently falling back to "system default" is how the translated voice ends up in the PA.
    if (selected && !found) element.appendChild(option(selected, selected + ' （?）'));

    element.value = selected || '';
  }

  function option(v, label) {
    var element = document.createElement('option');
    element.value = v;
    element.textContent = label;
    return element;
  }

  L.on('btn-list-devices', loadCaptureDevices);

  /*
   * The capture-reset section of the save body.
   *
   * The device picker fills in asynchronously — the WMI query behind it routinely takes one to
   * three seconds — and it is left empty when that query fails. Reading an unpopulated <select>
   * yields '', and because the server replaces the whole section, that silently erased the
   * configured capture card whenever an administrator saved *anything* within those seconds. The
   * reset button then disappeared from the operator page with no message anywhere, and nobody found
   * out until 04:40, when it was needed.
   *
   * So a selection is only reported when the picker can actually represent one. An empty picker
   * means "I do not know", not "the administrator chose nothing".
   */
  function captureResetBody() {
    var stored = settings.captureReset || {};
    var picker = document.getElementById('capture-device');
    var populated = !!picker && picker.options.length > 0;

    return {
      enabled: !!(document.getElementById('capture-enabled') || {}).checked,
      deviceInstanceId: populated ? picker.value : (stored.deviceInstanceId || ''),
      deviceName: populated ? selectedDeviceName() : (stored.deviceName || ''),
      obsInputName: value('capture-obs-input')
    };
  }

  /** The chosen device's display name, so the settings page can show what is configured. */
  function selectedDeviceName() {
    var element = document.getElementById('capture-device');
    if (!element || !element.value) return '';

    var option = element.options[element.selectedIndex];
    if (!option) return '';

    // Strip the class/status suffix this page appended when it built the option.
    return option.textContent.split('\u3000·\u3000')[0];
  }

  /*
   * A device selection, but only when the picker can actually represent one.
   *
   * The lists fill in asynchronously and are left empty when the request fails. Reading an
   * unpopulated <select> yields '', and the server replaces the whole translation section — so an
   * administrator who changed anything at all within that window silently lost the configured
   * devices. That is worse here than it looks: an empty captureDeviceId falls through to the
   * *system default recording device*, so the translator would go on happily translating a webcam
   * microphone while the panel showed everything as fine.
   *
   * An empty picker means "I do not know", not "the administrator chose nothing".
   */
  function pickedDevice(id, storedKey) {
    var stored = (settings.translation || {})[storedKey] || '';
    var picker = document.getElementById(id);

    if (!picker || picker.options.length === 0) return stored;
    return picker.value;
  }

  L.on('btn-save', function () {
    saveSettings().then(function (result) {
      report(result);
    });
  });

  /*
   * The whole form as one PUT, returned as a promise.
   *
   * Extracted from the save button because the translation smoke test has to run against stored
   * settings, not against what happens to be on screen: testing an API key the operator just typed
   * and has not saved would report a failure that does not exist, or a success that vanishes on the
   * next restart.
   */
  function saveSettings() {
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
        videoSourceNames: splitList(value('obs-video')),
        frozenFrameSourceNames: splitList(value('obs-frozen'))
      },
      captureReset: captureResetBody(),
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
      },
      translation: {
        enabled: !!(document.getElementById('tr-enabled') || {}).checked,
        apiKey: value('tr-api-key'),
        model: value('tr-model'),
        targetLanguage: value('tr-target'),
        echoTargetLanguage: !!(document.getElementById('tr-echo') || {}).checked,
        titleSuffix: value('tr-suffix'),
        // Created through its own endpoint; the server keeps the stored one when this is blank.
        streamId: (settings.translation && settings.translation.streamId) || '',
        captureDeviceId: pickedDevice('tr-capture', 'captureDeviceId'),
        playbackDeviceId: pickedDevice('tr-playback', 'playbackDeviceId'),
        obsInputName: value('tr-obs-input'),
        obsAudioTrack: intOr(value('tr-track'), 2)
      }
    };

    var newPin = value('new-pin').trim();
    if (newPin) body.settingsPin = newPin;

    return L.api.put('/api/settings', body).then(function (result) {
      if (result.ok && newPin) L.setSettingsPin(newPin);

      // Keep the in-memory copy in step: the pickers rebuild themselves from it, and a stale copy
      // would show a previously selected device as still selected after a change.
      if (result.ok) {
        settings.translation = body.translation;
        settings.captureReset = body.captureReset;
        settings.obs = body.obs;
        settings.slides = body.slides;
      }

      return result;
    });
  }

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
      var host = document.getElementById('access-addresses');
      if (!host) return;

      var info = result.data;
      if (!info) {
        // A silent return here left the card permanently empty after a transient failure.
        host.innerHTML = '';
        var warning = document.createElement('p');
        warning.className = 'subtle';
        warning.textContent = t('settings.loadFailed');
        host.appendChild(warning);

        var retry = document.createElement('button');
        retry.className = 'ghost';
        retry.textContent = t('settings.retryLoad');
        retry.addEventListener('click', loadAccessInfo);
        host.appendChild(retry);
        return;
      }

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
    // The PIN travels as a query parameter: this is a top-level navigation (a redirect to
    // Google's consent page), so the X-Settings-Pin header cannot be attached.
    location.href = '/auth/start?k=' + encodeURIComponent(L.code) +
      '&pin=' + encodeURIComponent(L.settingsPin());
  });

  // Two-step arm instead of window.confirm — see L.armConfirm (OBS's browser dock has no dialogs).
  L.armConfirm('btn-revoke', function () { return t('settings.confirmRevokeArm'); }, function () {
    L.api.post('/api/auth/revoke').then(function (result) {
      report(result);
      loadAuthStatus();
    });
  });

  /* ---- stream key -------------------------------------------------------- */

  L.armConfirm('btn-create-key', function () { return t('settings.confirmKeyArm'); }, function () {
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

  /* ---- translation stream key and smoke test ----------------------------- */

  L.armConfirm('btn-create-tr-key', function () { return t('settings.confirmTranslationKeyArm'); },
    function () {
      L.api.post('/api/stream-key/create?slot=translation').then(function (result) {
        var data = result.data || {};
        report(result);
        if (!data.ingestionKey) return;

        settings.translation = settings.translation || {};
        settings.translation.streamId = data.streamId;
        L.text('tr-stream-id', data.streamId);
        L.text('tr-ingest-address', data.ingestionAddress);
        L.text('tr-ingest-key', data.ingestionKey);
        L.show('tr-key-result', true);
      });
    });

  L.on('btn-copy-tr-key', function () {
    var element = document.getElementById('tr-ingest-key');
    if (element) L.copyText(element.textContent);
  });

  /*
   * Four things have to line up for the translated stream to have sound — the key, the network, the
   * mixer endpoint and the virtual cable — and all four fail the same silent way. This runs the real
   * path for twenty seconds and says which one is wrong, at deploy time rather than at 04:40.
   */
  L.on('btn-test-translation', function () {
    var button = document.getElementById('btn-test-translation');
    var out = document.getElementById('tr-test-out');

    if (out) { L.show('tr-test-out', true); out.textContent = t('settings.translationTesting'); }
    if (button) button.disabled = true;

    // Save first: the test runs against stored settings, not against what is on screen.
    saveSettings().then(function () {
      return L.api.post('/api/translate/test');
    }).then(function (result) {
      if (button) button.disabled = false;

      var data = result.data || {};
      var lines = [pick(data.message)];
      if (data.inputTranscript) lines.push(t('settings.translationHeard') + ': ' + data.inputTranscript);
      if (data.outputTranscript) lines.push(t('settings.translationSaid') + ': ' + data.outputTranscript);

      if (out) out.textContent = lines.filter(Boolean).join('\n');
      L.toast(pick(data.message) || t('generic.done'), data.ok ? 'good' : 'bad');
    }).catch(function () {
      if (button) button.disabled = false;
      if (out) out.textContent = t('generic.failed');
    });
  });

  /* ---- telegram test ----------------------------------------------------- */

  L.on('btn-test-telegram', function () {
    // Save first, otherwise the test uses whatever was stored before this edit.
    L.api.put('/api/settings', {
      telegramBotToken: value('tg-token'),
      telegramChatId: value('tg-chat'),
      telegramMessageDefault: value('tg-message')
    }).then(function (saved) {
      // Gated on the save. Chaining straight through meant a rejected save — a 403 after the PIN
      // was cleared, any 4xx — silently tested the *previously stored* token and reported a pass or
      // fail for credentials the operator had not just typed.
      if (!saved.ok) return saved;
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
        // The translation column is read from the stored template, not from the global switch: a
        // service that opted out must be visibly different from one that did not.
        var translate = template.translate === false
          ? t('settings.no')
          : (template.targetLanguage || (settings.translation && settings.translation.targetLanguage) || 'en');

        [name, (template.weekdays || []).join(','), template.startTime || '—', translate]
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
