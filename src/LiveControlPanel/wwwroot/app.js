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
  var reconnectTimer = null;
  var lastMessageAt = 0;
  var watchdogTimer = null;

  /*
   * The server pushes unconditionally every five seconds, so silence for four times that long means
   * the socket is gone whatever readyState claims.
   *
   * visibilitychange alone was not enough. It covers an iPad that was locked and woken, but not one
   * sitting awake on a stand while its access point fails over, and not a roam between APs — and in
   * those cases readyState stays OPEN for minutes. The panel went on showing ● 直播中, the pre-drop
   * bitrate and 0.0% dropped frames long after the stream had died, with no offline banner.
   */
  var SILENCE_LIMIT_MS = 20000;

  /* ---- websocket --------------------------------------------------------
   * FR 6.4: an iPad that has been asleep for ten minutes must recover on its own.
   * Reconnect on close, on error, and on regaining visibility.
   *
   * Invariant: at most one live socket and one pending reconnect timer. connect() always tears
   * down whatever came before it — a wake-up reconnect racing a backoff reconnect used to stack
   * parallel sockets, each one's onclose flapping the offline banner and spawning more.
   */
  function connect() {
    var protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    var url = protocol + '//' + location.host + '/ws?k=' + encodeURIComponent(L.code);

    if (reconnectTimer !== null) { window.clearTimeout(reconnectTimer); reconnectTimer = null; }
    if (socket) {
      var old = socket;
      socket = null;
      old.onopen = old.onmessage = old.onclose = old.onerror = null;
      try { old.close(); } catch (e) { /* already dead */ }
    }

    var ws;
    try {
      ws = new WebSocket(url);
    } catch (e) {
      scheduleReconnect();
      return;
    }
    socket = ws;

    ws.onopen = function () {
      if (socket !== ws) return;
      reconnectDelay = 1000;
      lastMessageAt = Date.now();
      L.show('offline', false);
      startWatchdog();

      /*
       * State, not the checks.
       *
       * /api/preflight is not a read: it queries OBS and makes two YouTube API calls, then mutates
       * and broadcasts to every client. Running it on every socket open meant every app-switch,
       * Control Centre pull and notification on any connected iPad triggered a full check run —
       * including mid-broadcast, when the checks card is hidden and the answer is discarded.
       *
       * The server pushes the current state within five seconds anyway; asking for it directly just
       * removes the wait.
       */
      L.api.get('/api/state').then(function (result) {
        if (result.data && result.data.phase) render(result.data);
      });
    };

    ws.onmessage = function (event) {
      if (socket !== ws) return;
      lastMessageAt = Date.now();
      // Parse inside the guard, render outside it: a crash in render() is a bug to surface on
      // the next frame, not a "malformed frame" to swallow forever.
      var next = null;
      try { next = JSON.parse(event.data); } catch (e) { /* genuinely malformed frame */ }
      if (next) render(next);
    };

    ws.onclose = function () {
      if (socket !== ws) return;
      L.show('offline', true);
      scheduleReconnect();
    };
    ws.onerror = function () {
      if (socket !== ws) return;
      L.show('offline', true);
    };
  }

  function startWatchdog() {
    if (watchdogTimer !== null) return;

    watchdogTimer = window.setInterval(function () {
      if (lastMessageAt === 0) return;
      if (Date.now() - lastMessageAt < SILENCE_LIMIT_MS) return;

      // Say so first: the operator may be looking at this screen right now, and everything on it is
      // stale. Then rebuild the socket rather than waiting for a close that is not coming.
      L.show('offline', true);
      lastMessageAt = Date.now();
      reconnectDelay = 1000;
      connect();
    }, 5000);
  }

  function scheduleReconnect() {
    if (reconnectTimer !== null) return;
    reconnectTimer = window.setTimeout(function () {
      reconnectTimer = null;
      reconnectDelay = Math.min(reconnectDelay * 2, 15000);
      connect();
    }, reconnectDelay);
  }

  // Always reconnect on wake, not only when the socket admits it is closed: iOS suspends the
  // TCP connection on sleep without a clean close, so after wake readyState still reads OPEN
  // for minutes while nothing arrives — the panel would sit frozen on pre-sleep state with no
  // offline banner. A fresh socket costs one round-trip; onopen repaints from live state.
  document.addEventListener('visibilitychange', function () {
    if (document.hidden) return;

    // Only when the socket has actually gone quiet. Reconnecting unconditionally dropped a healthy
    // connection on every app-switch, and each rebuild used to drag a full check run behind it.
    // The watchdog above covers the case this exists for — a socket iOS suspended without closing.
    if (socket && socket.readyState === WebSocket.OPEN
        && Date.now() - lastMessageAt < SILENCE_LIMIT_MS) {
      return;
    }

    reconnectDelay = 1000;
    connect();
  });

  /* ---- render ------------------------------------------------------------ */

  function render(next) {
    if (accessRefused) return;

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
    // Shown above the metrics, because the metrics themselves look fine while this is true.
    L.show('obs-reconnecting', !!state.obs.reconnecting && state.phase === 'Live');

    L.text('m-time-v', L.duration(state.obs.streamTimeSeconds));
    L.text('m-bitrate-v', state.obs.kbitsPerSec ? state.obs.kbitsPerSec + ' kb/s' : '—');

    // Dropped frames escalate visually: over 1% is worth a glance, over 5% the stream is suffering.
    // The label sits under the value, so the state is never carried by color alone.
    var dropped = state.obs.droppedFramesPercent || 0;
    var tile = document.getElementById('m-dropped');
    if (tile) {
      tile.classList.toggle('crit', dropped >= 5);
      tile.classList.toggle('warn', dropped >= 1 && dropped < 5);
    }
    L.text('m-dropped-v', dropped.toFixed(1) + '%');
    L.text('m-scene-v', state.obs.currentScene || '—');
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
      } else if (item.action === 'reset-capture') {
        // Armed, like the one on the status card. This switches a piece of hardware off and on, and
        // the checks card re-renders on every state push — a stray tap on a touch screen should not
        // be enough. (end-previous next door is a single tap, but that one only ends a broadcast
        // that is already finished as far as this service is concerned.)
        body.appendChild(armedActionButton(
          t('preflight.resetCapture'),
          function () { return t('status.resetCaptureArm'); },
          function (button) {
            button.disabled = true;
            L.api.post('/api/capture/reset').then(function (result) {
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

  /*
   * A two-step action button built fresh on every render.
   *
   * L.armConfirm binds to a fixed element id, which the checks card cannot offer — its rows are
   * rebuilt from state each push. The behaviour is the same: first tap arms and relabels, a second
   * within five seconds acts, and anything else disarms.
   */
  function armedActionButton(label, armedLabel, handler) {
    var button = document.createElement('button');
    var isArmed = false;
    var timer = null;

    function disarm() {
      isArmed = false;
      if (timer !== null) { window.clearTimeout(timer); timer = null; }
      button.textContent = label;
      button.classList.remove('armed');
    }

    button.textContent = label;
    button.addEventListener('click', function () {
      if (!isArmed) {
        isArmed = true;
        button.textContent = armedLabel();
        button.classList.add('armed');
        timer = window.setTimeout(disarm, 5000);
        return;
      }
      disarm();
      handler(button);
    });

    return button;
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

  /*
   * Slide numbers whose preview the presentation program could not render.
   *
   * Without this latch a 404 was retried on every state push — every five seconds, for the whole
   * sermon. Each attempt runs a full COM attach walk to find the presentation, and then a second
   * independent walk inside the export, so on the deployment the code itself expects (WPS with no
   * Slide.Export) every connected iPad was driving COM calls into the live presentation program
   * three times a minute, forever. The comment said "stay hidden"; nothing made it stay.
   */
  var previewUnavailable = {};

  var lastSlideTotal = null;

  function renderSlides() {
    var slides = state.slides || {};

    // A different deck is a different question. Total slides changing is the only signal the panel
    // gets that the presentation was swapped, so the "cannot render" latch is cleared on it.
    if (slides.total !== lastSlideTotal) {
      lastSlideTotal = slides.total;
      previewUnavailable = {};
    }
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

    if (previewUnavailable[next]) {
      previewShownFor = null;
      L.show('slide-preview', false);
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
      // 404 = this presentation program cannot render a slide image. Remembered, so it stays hidden
      // instead of being asked again on the next push.
      previewUnavailable[next] = true;
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

    renderCaptureReset();

    // FR 8: seven people share this PC, so "who did what, when" has to be on screen.
    if (state.lastAction) {
      var service = state.lastAction.service
        ? (I.lang === 'en' ? ' (' + state.lastAction.service + ')' : '（' + state.lastAction.service + '）')
        : '';
      L.text('last-action', t('status.lastAction') + L.clockTime(state.lastAction.at) + ' ' +
        pick(state.lastAction.what) + service);
    }
  }

  /*
   * The capture-card reset. Hidden unless an administrator configured it, and it stays available in
   * every phase on purpose: the card can wedge mid-sermon, when the pre-flight is nowhere on screen.
   *
   * "Already tried it at 04:41" is the first thing an operator needs to know before trying again,
   * so the last attempt is shown next to the button rather than only in the action log.
   */
  function renderCaptureReset() {
    var reset = state.captureReset || {};

    L.show('btn-reset-capture', !!reset.enabled);
    L.show('capture-reset-note', !!reset.enabled && !!reset.lastResetAt);

    if (reset.enabled && reset.lastResetAt) {
      // The date too when it was not today. HH:mm alone let a reset from a previous service read as
      // "someone already tried this morning" — the one reading that stops an operator trying.
      L.text('capture-reset-note',
        t('status.captureResetAt', { time: stampedTime(reset.lastResetAt) }));
    }
  }

  /** HH:mm for today, M/D HH:mm otherwise. Uses the server's clock, never the device's. */
  function stampedTime(iso) {
    var when = new Date(iso);
    var server = new Date(state.serverTime);

    if (isNaN(when.getTime())) return '';
    if (!isNaN(server.getTime()) && when.toDateString() === server.toDateString()) {
      return L.clockTime(iso);
    }

    return (when.getMonth() + 1) + '/' + when.getDate() + ' ' + L.clockTime(iso);
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
  // In-page two-step arm, not window.confirm — see L.armConfirm for why (OBS dock).
  L.armConfirm('btn-stop', function () { return t('live.confirmStopArm'); }, function () {
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

  /*
   * Two-step arm rather than a plain click: this switches a piece of hardware off and on, and a
   * mis-tap during a service should not do that. Same pattern as "end the broadcast".
   */
  L.armConfirm('btn-reset-capture', function () { return t('status.resetCaptureArm'); }, function () {
    var button = document.getElementById('btn-reset-capture');
    if (button) button.disabled = true;

    L.api.post('/api/capture/reset').then(function (result) {
      if (button) button.disabled = false;
      report(result);
      refreshPreflight();
    });
  });

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
        var name = document.createElement('span');
        name.textContent = template.name;
        button.appendChild(name);
        if (template.startTime) {
          var time = document.createElement('span');
          time.className = 'time';
          time.textContent = template.startTime;
          button.appendChild(time);
        }
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
    showAccessError(t('generic.noAccessCode'));
  }

  L.api.get('/api/state').then(function (result) {
    if (result.status === 403) {
      showAccessError(pick(result.data && result.data.message) || t('generic.badAccessCode'));
      return;
    }
    if (result.data) render(result.data);
    connect();
  });

  /*
   * A refused access code has to stop the page, not decorate it. Leaving the shell up shows an
   * operator a panel full of "—" placeholders with no working buttons, and the toast explaining why
   * disappears after six seconds. The conclusion they reach is "the panel is broken" and the fix
   * they need — get a fresh link — is nowhere on screen.
   */
  var accessRefused = false;

  function showAccessError(message) {
    // Latched. AccessGate falls back to the lcp_k cookie, so a page opened with no ?k= and
    // unreadable localStorage — iOS private mode, cleared site data — showed this card and then had
    // every card it hid put back by the first successful render: a permanent "cannot open the
    // panel" banner sitting above a working panel.
    accessRefused = true;

    L.text('access-error-message', message);
    L.show('access-error', true);

    ['phase-noschedule', 'phase-ready', 'phase-live', 'phase-ended', 'progress-card',
      'preflight-card', 'scene-card', 'slides-card', 'link-card', 'picker-card', 'status-card',
      'offline'].forEach(function (id) { L.show(id, false); });
  }
})();
