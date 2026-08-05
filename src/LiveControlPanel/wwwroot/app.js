/*
 * Operator page. FR 6.1: the UI is a function of state.phase and never shows an action that makes
 * no sense right now — most importantly, no "stop streaming" button when nothing is live.
 *
 * FR 6.2 target: three fixed taps per service — start, notify, stop — plus page turns.
 */
(function () {
  'use strict';

  var L = window.LCP;
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
    L.show('slides-card', phase === 'Live' || phase === 'Ready');

    // FR 6.1: the broadcast id exists from creation onward, so the link is not gated on Live.
    L.show('link-card', !!(broadcast && broadcast.watchUrl));

    if (state.today) {
      L.text('ready-title', state.today.title);
      L.text('ready-time', state.today.scheduledStart
        ? '预定开始 ' + L.clockTime(state.today.scheduledStart)
        : '');
      L.text('live-title', state.today.title);
      L.text('ended-title', state.today.title);
    }

    if (state.nextService) {
      L.text('next-service', '下一场：' + state.nextService.title +
        '，' + formatWhen(state.nextService.startsAt));
    } else if (phase === 'NoSchedule') {
      L.text('next-service', '未来两周没有排期。');
    }

    if (broadcast && broadcast.watchUrl) L.text('watch-url', broadcast.watchUrl);

    renderMetrics();
    renderPreflight();
    renderSteps();
    renderScenes();
    renderSlides();
    renderStatus();
    renderTelegramButton();
  }

  function formatWhen(iso) {
    if (!iso) return '';
    var date = new Date(iso);
    if (isNaN(date.getTime())) return '';

    var today = new Date();
    var sameDay = date.toDateString() === today.toDateString();
    if (sameDay) return '今天 ' + L.clockTime(iso);

    var tomorrow = new Date(today.getTime() + 86400000);
    if (date.toDateString() === tomorrow.toDateString()) return '明天 ' + L.clockTime(iso);

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
      body.textContent = item.message;

      if (item.action === 'end-previous') {
        body.appendChild(actionButton('结束上一场直播', function (button) {
          button.disabled = true;
          L.api.post('/api/broadcast/end-previous').then(function (result) {
            button.disabled = false;
            report(result);
            refreshPreflight();
          });
        }));
      } else if (item.action === 'reauthorize') {
        body.appendChild(actionButton('去设置页重新授权', function () {
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

      var body = document.createElement('span');
      body.textContent = step.name + (step.message ? '　' + step.message : '');

      li.appendChild(mark);
      li.appendChild(body);
      host.appendChild(li);
    });

    // FR 4.2: retry resumes from the failed step; it must never restart from step 1.
    L.show('btn-retry', lastFailedStep !== null);
    var retry = document.getElementById('btn-retry');
    if (retry && lastFailedStep !== null) retry.textContent = '重试第 ' + lastFailedStep + ' 步';
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
      ? '第 ' + slides.current + ' / ' + slides.total + ' 页'
      : '');

    refreshPreview(slides);
  }

  /*
   * Next-slide preview. Only attempted when COM reported a position, and only re-fetched when the
   * page actually changed — a preview refresh must not become a periodic redraw (FR 2.2).
   * On any failure the block is hidden rather than left showing a stale or broken image.
   */
  function refreshPreview(slides) {
    if (!slides.current || !slides.total) {
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

    if (previewShownFor === next) return;
    previewShownFor = next;

    var image = document.getElementById('slide-preview-img');
    if (!image) return;

    // Cache-bust per slide so a page turn always fetches the right frame.
    var url = '/api/slides/preview?n=' + next + '&k=' + encodeURIComponent(L.code);

    image.onload = function () {
      L.show('slide-preview', true);
      L.text('slide-preview-caption', '下一页（第 ' + next + ' 页）');
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
    L.text('obs-text', state.obs.connected ? 'OBS 已连接' : 'OBS 未连接（请打开 OBS）');

    var auth = state.auth || {};
    var authDot = document.getElementById('dot-auth');
    if (authDot) authDot.className = 'dot ' + (auth.valid ? 'ok' : 'bad');
    L.text('auth-text', auth.valid
      ? '授权有效' + (auth.expiresInDays != null ? '（剩余约 ' + auth.expiresInDays + ' 天）' : '')
      : '授权需要处理');

    // FR 8: seven people share this PC, so "who did what, when" has to be on screen.
    if (state.lastAction) {
      L.text('last-action', '上次操作：' + L.clockTime(state.lastAction.at) + ' ' +
        state.lastAction.what + (state.lastAction.service ? '（' + state.lastAction.service + '）' : ''));
    }
  }

  function renderTelegramButton() {
    var button = document.getElementById('btn-telegram');
    if (!button) return;

    var sentAt = state.telegram && state.telegram.sentAt;
    button.textContent = sentAt
      ? '已发送到 Telegram（' + L.clockTime(sentAt) + '）'
      : '发送链接到 Telegram';
    button.classList.toggle('primary', !sentAt);

    if (state.telegram && state.telegram.lastError) {
      button.textContent = '重试发送到 Telegram';
      button.classList.add('danger');
    } else {
      button.classList.remove('danger');
    }
  }

  /* ---- actions ----------------------------------------------------------- */

  function report(result) {
    var data = result.data || {};
    var message = data.message || (result.ok ? '完成。' : '操作失败，请重试。');
    L.toast(message, data.ok === false || !result.ok ? 'bad' : 'good');
  }

  function refreshPreflight() {
    L.api.get('/api/preflight').then(function (result) {
      if (result.data && result.data.phase) render(result.data);
    });
  }

  L.on('btn-start', function () {
    var button = document.getElementById('btn-start');
    button.disabled = true;
    button.textContent = '正在开始…';

    L.api.post('/api/broadcast/start-today').then(function (result) {
      button.disabled = false;
      button.textContent = '开始今天的直播';
      report(result);
    });
  });

  L.on('btn-retry', function () {
    if (lastFailedStep === null) return;
    L.api.post('/api/broadcast/retry/' + lastFailedStep).then(report);
  });

  // FR 4.3: a real confirmation in front of the only irreversible action on the page.
  L.on('btn-stop', function () {
    if (!window.confirm('确定要结束这场直播吗？结束后无法继续。')) return;
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
    if (data.ok === false || !result.ok) L.toast(data.message || '翻页失败。', 'bad');
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
    if (!title) { L.toast('请先填写标题。', 'bad'); return; }

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

  if (!L.code) {
    L.toast('缺少访问码。请扫描管理员提供的二维码打开本页。', 'bad');
  }

  L.api.get('/api/state').then(function (result) {
    if (result.status === 403) {
      L.toast('访问码无效。请重新扫描二维码打开本页。', 'bad');
      return;
    }
    if (result.data) render(result.data);
    connect();
  });
})();
