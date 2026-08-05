/*
 * Operator page. FR 6.1: the UI is a function of state.phase and never shows an action that makes no
 * sense right now — most importantly, no "stop streaming" button when nothing is live.
 *
 * FR 6.2 target: three fixed taps per service — start, notify, stop — plus page turns.
 *
 * Every visible string comes either from i18n.js (static text) or from a dual-language server
 * message resolved through pick(). Nothing user-facing is written inline here.
 */
(function () {
  'use strict';

  var L = window.LCP;
  var I = window.LCP_I18N;
  var t = I.t;
  var pick = I.pick;

  var state = null;
  var lastFailedStep = null;
  var socket = null;
  var reconnectDelay = 1000;

  /* ---- websocket --------------------------------------------------------
   * FR 6.4: an iPad that has been asleep for ten minutes must recover on its own.
   * Reconnect on close, on error, and on regaining visibility.
   */
  function connect() {
    var protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    var url = protocol + '//' + location.host + '/ws?k=' + encodeURIComponent(L.code);

    try {
      socket = new WebSocket(url);
    } catch (e) {
      scheduleReconnect();
      return;
    }

    socket.onopen = function () {
      reconnectDelay = 1000;
      L.show('offline', false);
      refreshPreflight();
    };

    socket.onmessage = function (event) {
      try { render(JSON.parse(event.data)); } catch (e) { /* ignore malformed frame */ }
    };

    socket.onclose = function () { L.show('offline', true); scheduleReconnect(); };
    socket.onerror = function () { L.show('offline', true); };
  }

  function scheduleReconnect() {
    window.setTimeout(function () {
      reconnectDelay = Math.min(reconnectDelay * 2, 15000);
      connect();
    }, reconnectDelay);
  }

  document.addEventListener('visibilitychange', function () {
    if (!document.hidden && (!socket || socket.readyState > 1)) {
      reconnectDelay = 1000;
      connect();
    }
  });

  /* ---- render ------------------------------------------------------------ */

  function render(next) {
    state = next;
    var phase = state.phase;
    var broadcast = state.broadcast;

    L.show('phase-noschedule', phase === 'NoSchedule');
    L.show('phase-ready', phase === 'Ready' && !state.starting);
    L.show('phase-live', phase === 'Live');
    L.show('phase-ended', phase === 'Ended');

    L.show('preflight-card', phase === 'Ready' && !state.starting);
    L.show('progress-card', (state.steps && state.steps.length > 0) && phase !== 'Ended');
    L.show('scene-card', phase === 'Live' && state.obs.connected);
    // Off by default; nothing about slides appears until it is switched on in settings.
    L.show('slides-card', state.slides.enabled && (phase === 'Live' || phase === 'Ready'));

    // FR 6.1: the broadcast id exists from creation onward, so the link is not gated on Live.
    L.show('link-card', !!(broadcast && broadcast.watchUrl));

    if (state.today) {
      L.text('ready-title', state.today.title);
      L.text('ready-time', state.today.scheduledStart
        ? t('ready.scheduledFor') + L.clockTime(state.today.scheduledStart)
        : '');
      L.text('live-title', state.today.title);
      L.text('ended-title', state.today.title);
    }

    if (phase === 'NoSchedule') renderNoSchedule();

    if (broadcast && broadcast.watchUrl) L.text('watch-url', broadcast.watchUrl);

    renderMetrics();
    renderPreflight();
    renderSteps();
    renderScenes();
    renderSlides();
    renderStatus();
    renderTelegramButton();
  }

  /*
   * "Nothing scheduled" and "not time yet" are different situations. An operator who turned up two
   * hours early for a service that is definitely happening must not be told 本日无排期 — at 04:40,
   * alone, that reads as "you came on the wrong day".
   *
   * Uses serverTime, not the device clock: an iPad with a wrong date must not change which day the
   * panel thinks it is.
   */
  function renderNoSchedule() {
    var next = state.nextService;

    if (!next || !next.startsAt) {
      L.text('noschedule-heading', t('noschedule.heading'));
      L.text('noschedule-title', '');
      L.text('next-service', t('noschedule.none'));
      L.show('btn-prepare-now', false);
      return;
    }

    if (!isSameDayAsServer(next.startsAt)) {
      // Either a day with no services at all, or today's service has already gone past its window.
      L.text('noschedule-heading', t('noschedule.heading'));
      L.text('noschedule-title', '');
      L.text('next-service', t('noschedule.next') + next.title + '，' + formatWhen(next.startsAt));
      L.show('btn-prepare-now', false);
      return;
    }

    L.text('noschedule-heading', t('noschedule.notYet'));
    L.text('noschedule-title', next.title);
    L.text('next-service',
      t('noschedule.startsToday') + L.clockTime(next.startsAt) + t('noschedule.startsAt') +
      untilText(next.startsAt) + ' ' + t('noschedule.willBeReady'));

    var prepare = document.getElementById('btn-prepare-now');
    if (prepare) {
      prepare.classList.toggle('hidden', !next.templateId);
      prepare.dataset.templateId = next.templateId || '';
    }
  }

  function isSameDayAsServer(iso) {
    var when = new Date(iso);
    var server = new Date(state.serverTime);
    if (isNaN(when.getTime()) || isNaN(server.getTime())) return false;
    return when.toDateString() === server.toDateString();
  }

  /** Coarse on purpose — state arrives every few seconds, so this must not look like a live clock. */
  function untilText(iso) {
    var minutes = Math.round((new Date(iso) - new Date(state.serverTime)) / 60000);
    if (isNaN(minutes) || minutes <= 0) return '';

    if (I.lang === 'en') {
      if (minutes < 60) return ', about ' + minutes + ' min from now.';
      var h = Math.floor(minutes / 60), m = minutes % 60;
      return ', about ' + h + ' h' + (m > 0 ? ' ' + m + ' min' : '') + ' from now.';
    }

    if (minutes < 60) return '，还有约 ' + minutes + ' 分钟。';
    var hours = Math.floor(minutes / 60), rest = minutes % 60;
    return '，还有约 ' + hours + ' 小时' + (rest > 0 ? ' ' + rest + ' 分钟' : '') + '。';
  }

  function formatWhen(iso) {
    if (!iso) return '';
    var date = new Date(iso);
    if (isNaN(date.getTime())) return '';

    var today = new Date(state ? state.serverTime : Date.now());
    if (date.toDateString() === today.toDateString()) {
      return (I.lang === 'en' ? 'today ' : '今天 ') + L.clockTime(iso);
    }

    var tomorrow = new Date(today.getTime() + 86400000);
    if (date.toDateString() === tomorrow.toDateString()) {
      return (I.lang === 'en' ? 'tomorrow ' : '明天 ') + L.clockTime(iso);
    }

    return (date.getMonth() + 1) + '/' + date.getDate() + ' ' + L.clockTime(iso);
  }

  function renderMetrics() {
    L.text('m-time', L.duration(state.obs.streamTimeSeconds));
    L.text('m-bitrate', state.obs.kbitsPerSec ? state.obs.kbitsPerSec + ' kb/s' : '—');
    L.text('m-dropped', (state.obs.droppedFramesPercent || 0).toFixed(1) + '%');
    L.text('m-scene', state.obs.currentScene || '—');
  }

  function renderPreflight() {
    var host = document.getElementById('preflight');
    if (!host) return;

    host.innerHTML = '';
    (state.preflight || []).forEach(function (item) {
      var row = document.createElement('div');
      row.className = 'check ' + (item.ok ? 'ok' : 'bad');

      var mark = document.createElement('div');
      mark.className = 'mark';
      mark.textContent = item.ok ? '✓' : '!';

      var body = document.createElement('div');
      body.className = 'body';
      body.textContent = pick(item.message);

      if (item.action === 'end-previous') {
        body.appendChild(actionButton(t('preflight.endPrevious'), function (button) {
          button.disabled = true;
          L.api.post('/api/broadcast/end-previous').then(function (result) {
            button.disabled = false;
            report(result);
            refreshPreflight();
          });
        }));
      } else if (item.action === 'reauthorize') {
        body.appendChild(actionButton(t('preflight.reauthorize'), function () {
          location.href = 'settings.html?k=' + encodeURIComponent(L.code);
        }));
      }

      row.appendChild(mark);
      row.appendChild(body);
      host.appendChild(row);
    });
  }

  function actionButton(label, handler) {
    var button = document.createElement('button');
    button.textContent = label;
    button.addEventListener('click', function () { handler(button); });
    return button;
  }

  function renderSteps() {
    var host = document.getElementById('steps');
    if (!host) return;

    host.innerHTML = '';
    lastFailedStep = null;

    (state.steps || []).forEach(function (step) {
      if (step.status === 'failed') lastFailedStep = step.step;

      var li = document.createElement('li');
      li.className = step.status;

      var mark = document.createElement('span');
      mark.className = 'mark';
      mark.textContent = step.status === 'done' || step.status === 'skipped' ? '✓'
        : step.status === 'failed' ? '✕'
        : step.status === 'running' ? '…' : '·';

      var detail = pick(step.message);
      var body = document.createElement('span');
      body.textContent = pick(step.name) + (detail ? '　' + detail : '');

      li.appendChild(mark);
      li.appendChild(body);
      host.appendChild(li);
    });

    // FR 4.2: retry resumes from the failed step; it must never restart from step 1.
    L.show('btn-retry', lastFailedStep !== null);
    var retry = document.getElementById('btn-retry');
    if (retry && lastFailedStep !== null) retry.textContent = t('progress.retryStep', { n: lastFailedStep });
  }

  function renderScenes() {
    var host = document.getElementById('scene-buttons');
    if (!host) return;

    var scenes = state.obs.scenes || [];
    if (host.dataset.rendered === scenes.join('|')) {
      highlightScene();
      return;
    }
    host.dataset.rendered = scenes.join('|');
    host.innerHTML = '';

    scenes.forEach(function (scene) {
      var button = document.createElement('button');
      button.textContent = scene;
      button.dataset.scene = scene;
      button.addEventListener('click', function () {
        L.api.post('/api/obs/scene', { scene: scene }).then(report);
      });
      host.appendChild(button);
    });

    highlightScene();
  }

  function highlightScene() {
    var host = document.getElementById('scene-buttons');
    if (!host) return;

    Array.prototype.forEach.call(host.children, function (button) {
      button.classList.toggle('primary', button.dataset.scene === state.obs.currentScene);
    });
  }

  var previewShownFor = null;

  function renderSlides() {
    var slides = state.slides || {};
    L.text('slide-pos', slides.current && slides.total
      ? t('slides.position', { current: slides.current, total: slides.total })
      : '');

    var image = document.getElementById('slide-preview-img');
    if (image) image.setAttribute('alt', t('slides.previewAlt'));

    refreshPreview(slides);
  }

  /*
   * Next-slide preview. Only attempted when COM reported a position, and only re-fetched when the
   * page actually changed — a preview refresh must not become a periodic redraw (FR 2.2).
   * On any failure the block is hidden rather than left showing a stale or broken image.
   */
  function refreshPreview(slides) {
    if (!slides.enabled || !slides.current || !slides.total) {
      previewShownFor = null;
      L.show('slide-preview', false);
      return;
    }

    var next = slides.current + 1;
    if (next > slides.total) {
      previewShownFor = null;
      L.show('slide-preview', false);
      L.text('slide-preview-caption', '');
      return;
    }

    if (previewShownFor === next) {
      // Already showing the right slide; only the caption language may have changed.
      L.text('slide-preview-caption', t('slides.previewCaption', { n: next }));
      return;
    }
    previewShownFor = next;

    var image = document.getElementById('slide-preview-img');
    if (!image) return;

    // Cache-bust per slide so a page turn always fetches the right frame.
    var url = '/api/slides/preview?n=' + next + '&k=' + encodeURIComponent(L.code);

    image.onload = function () {
      L.show('slide-preview', true);
      L.text('slide-preview-caption', t('slides.previewCaption', { n: next }));
    };
    image.onerror = function () {
      // 404 = this presentation program cannot render a slide image. Stay hidden.
      previewShownFor = null;
      L.show('slide-preview', false);
    };
    image.src = url;
  }

  function renderStatus() {
    var obsDot = document.getElementById('dot-obs');
    if (obsDot) obsDot.className = 'dot ' + (state.obs.connected ? 'ok' : 'bad');
    L.text('obs-text', t(state.obs.connected ? 'status.obsConnected' : 'status.obsDisconnected'));

    var auth = state.auth || {};
    var authDot = document.getElementById('dot-auth');
    if (authDot) authDot.className = 'dot ' + (auth.valid ? 'ok' : 'bad');
    L.text('auth-text', !auth.valid ? t('status.authProblem')
      : auth.expiresInDays != null ? t('status.authValidDays', { n: auth.expiresInDays })
      : t('status.authValid'));

    // FR 8: seven people share this PC, so "who did what, when" has to be on screen.
    if (state.lastAction) {
      L.text('last-action', t('status.lastAction') + L.clockTime(state.lastAction.at) + ' ' +
        pick(state.lastAction.what) +
        (state.lastAction.service ? '（' + state.lastAction.service + '）' : ''));
    }
  }

  function renderTelegramButton() {
    var button = document.getElementById('btn-telegram');
    if (!button) return;

    var telegram = state.telegram || {};
    var sentAt = telegram.sentAt;

    if (telegram.lastError) {
      button.textContent = t('live.retryTelegram');
      button.classList.add('danger');
      button.classList.remove('primary');
      return;
    }

    button.classList.remove('danger');
    button.textContent = sentAt
      ? t('live.sentTelegram', { time: L.clockTime(sentAt) })
      : t('live.sendTelegram');
    button.classList.toggle('primary', !sentAt);
  }

  /* ---- actions ----------------------------------------------------------- */

  function report(result) {
    var data = result.data || {};
    var message = pick(data.message) || t(result.ok ? 'generic.done' : 'generic.failed');
    L.toast(message, data.ok === false || !result.ok ? 'bad' : 'good');
  }

  function refreshPreflight() {
    L.api.get('/api/preflight').then(function (result) {
      if (result.data && result.data.phase) render(result.data);
    });
  }

  L.on('btn-lang', function () { I.toggle(); });

  // Re-render from the last state so everything built from data follows the switch too.
  window.LCP_onLanguageChange = function () { if (state) render(state); };

  L.on('btn-start', function () {
    var button = document.getElementById('btn-start');
    button.disabled = true;

    L.api.post('/api/broadcast/start-today').then(function (result) {
      button.disabled = false;
      button.textContent = t('ready.start');
      report(result);
    });
  });

  L.on('btn-retry', function () {
    if (lastFailedStep === null) return;
    L.api.post('/api/broadcast/retry/' + lastFailedStep).then(report);
  });

  // FR 4.3: a real confirmation in front of the only irreversible action on the page.
  L.on('btn-stop', function () {
    if (!window.confirm(t('live.confirmStop'))) return;
    L.api.post('/api/broadcast/stop', { confirm: true }).then(report);
  });

  L.on('btn-telegram', function () {
    L.api.post('/api/telegram/send').then(report);
  });

  L.on('btn-copy', function () {
    if (state && state.broadcast && state.broadcast.watchUrl) L.copyText(state.broadcast.watchUrl);
  });

  L.on('btn-next', function () { L.api.post('/api/slides/next').then(quietReport); });
  L.on('btn-prev', function () { L.api.post('/api/slides/prev').then(quietReport); });

  // Page turns happen dozens of times per sermon; only surface failures.
  function quietReport(result) {
    var data = result.data || {};
    if (data.ok === false || !result.ok) {
      L.toast(pick(data.message) || t('generic.pageTurnFailed'), 'bad');
    }
  }

  L.on('btn-refresh', refreshPreflight);

  L.on('btn-settings', function () {
    location.href = 'settings.html?k=' + encodeURIComponent(L.code);
  });

  L.on('btn-another', function () {
    L.api.post('/api/broadcast/start-another').then(function (result) {
      report(result);
      refreshPreflight();
    });
  });

  // One tap from "not time yet" into Ready, instead of picker → find the service → tap.
  L.on('btn-prepare-now', function () {
    var button = document.getElementById('btn-prepare-now');
    var templateId = button && button.dataset.templateId;
    if (!templateId) { L.toast(t('picker.titleRequired'), 'bad'); return; }

    L.api.post('/api/broadcast/create', { templateId: templateId }).then(function (result) {
      report(result);
      refreshPreflight();
    });
  });

  L.on('btn-not-this', openPicker);
  L.on('btn-pick-other', openPicker);
  L.on('btn-picker-cancel', function () { L.show('picker-card', false); });

  function openPicker() {
    L.show('picker-card', true);

    L.api.get('/api/templates/list').then(function (result) {
      var host = document.getElementById('template-list');
      if (!host || !result.data) return;

      host.innerHTML = '';
      result.data.forEach(function (template) {
        // "custom" is not a scheduled service; it drives the ad-hoc field below instead.
        // Its defaultTitle comes from the server's clock, never this device's.
        if (template.id === 'custom') {
          var input = document.getElementById('custom-title');
          if (input && !input.value) input.value = template.defaultTitle || '';
          return;
        }

        var button = document.createElement('button');
        button.textContent = template.name + (template.startTime ? '　' + template.startTime : '');
        button.addEventListener('click', function () {
          L.api.post('/api/broadcast/create', { templateId: template.id }).then(function (created) {
            report(created);
            L.show('picker-card', false);
            refreshPreflight();
          });
        });
        host.appendChild(button);
      });
    });
  }

  L.on('btn-create-custom', function () {
    var input = document.getElementById('custom-title');
    var title = input ? input.value.trim() : '';
    if (!title) { L.toast(t('picker.titleRequired'), 'bad'); return; }

    L.api.post('/api/broadcast/create', { templateId: 'custom', title: title }).then(function (result) {
      report(result);
      L.show('picker-card', false);
      // Cleared so reopening the picker re-reads today's date from the server rather than
      // reusing a title typed before midnight.
      if (result.ok && input) input.value = '';
      refreshPreflight();
    });
  });

  /* ---- boot -------------------------------------------------------------- */

  I.apply();

  if (!L.code) {
    L.toast(t('generic.noAccessCode'), 'bad');
  }

  L.api.get('/api/state').then(function (result) {
    if (result.status === 403) {
      L.toast(pick(result.data && result.data.message) || t('generic.badAccessCode'), 'bad');
      return;
    }
    if (result.data) render(result.data);
    connect();
  });
})();
