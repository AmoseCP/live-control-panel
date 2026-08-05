/*
 * Settings page (FR 6.5). Kept separate from the operator page so nothing here can be reached by
 * accident during a service, and gated behind the PIN.
 */
(function () {
  'use strict';

  var L = window.LCP;
  var settings = null;

  function value(id, v) {
    var element = document.getElementById(id);
    if (!element) return '';
    if (v !== undefined) { element.value = v == null ? '' : v; return v; }
    return element.value;
  }

  /* ---- unlock ------------------------------------------------------------ */

  L.on('btn-unlock', function () {
    var pin = value('pin').trim();
    if (!pin) { L.toast('请输入设置密码。', 'bad'); return; }

    L.setSettingsPin(pin);
    L.api.get('/api/settings').then(function (result) {
      if (result.status === 403) {
        L.setSettingsPin('');
        L.toast('设置密码不对。', 'bad');
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
        beforeMinutes: parseInt(value('window-before'), 10) || 60,
        afterMinutes: parseInt(value('window-after'), 10) || 120
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
      if (info.mdnsUrl) host.appendChild(addressRow(info.mdnsUrl, 'mDNS 名称'));
      host.appendChild(addressRow(info.localUrl, '本机（OBS 停靠面板用这个）'));

      if (info.addresses.length === 0) {
        var warning = document.createElement('p');
        warning.className = 'subtle';
        warning.textContent = '没有找到可用的局域网地址，请检查这台电脑是否连上了教会 WiFi。';
        host.appendChild(warning);
      }

      var qr = document.getElementById('qr-holder');
      qr.innerHTML = '';
      var image = document.createElement('img');
      image.alt = '访问二维码';
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
    copy.textContent = '复制这个地址';
    copy.addEventListener('click', function () { L.copyText(url); });
    wrapper.appendChild(copy);

    return wrapper;
  }

  /* ---- auth -------------------------------------------------------------- */

  function loadAuthStatus() {
    L.api.get('/api/state').then(function (result) {
      var auth = result.data && result.data.auth;
      if (!auth) return;

      L.text('auth-status', auth.valid
        ? '授权有效' + (auth.expiresInDays != null ? '，剩余约 ' + auth.expiresInDays + ' 天' : '')
        : '尚未授权或授权已失效。');
    });
  }

  // Full page load, not fetch: this is a redirect to Google's consent screen.
  L.on('btn-authorize', function () {
    location.href = '/auth/start?k=' + encodeURIComponent(L.code);
  });

  L.on('btn-revoke', function () {
    if (!window.confirm('确定清除现有授权？清除后需要重新授权才能建播。')) return;
    L.api.post('/api/auth/revoke').then(function (result) {
      report(result);
      loadAuthStatus();
    });
  });

  /* ---- stream key -------------------------------------------------------- */

  L.on('btn-create-key', function () {
    if (!window.confirm('创建一个新的可复用推流密钥？创建后需要把密钥填进 OBS。')) return;

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
        : (result.data && result.data.message) || '读取失败。';
    });
  });

  // Reports which of the two paging paths actually works on this machine, so enabling the feature is
  // an informed decision rather than a guess.
  L.on('btn-probe-com', function () {
    var out = document.getElementById('com-probe-out');
    if (out) { L.show('com-probe-out', true); out.textContent = '检测中…'; }

    L.api.get('/api/diag/slides').then(function (result) {
      var d = result.data || {};
      var lines = [
        '会话: ' + d.sessionId + (d.sessionIsolated ? '（会话 0，翻页无法工作！）' : '（正常）'),
        '自动化接口: ' + (d.comProgId || '未连上'),
        '正在放映: ' + (d.slideShowRunning ? '是' : '否'),
        '页码: ' + (d.current == null ? '读不到' : d.current + ' / ' + d.total),
        '下一页预览: ' + (d.previewSupported ? '可用' : '不可用'),
        '放映窗口(按键用): ' + (d.targetWindowFound ? '已找到' : '未找到 / 未配置类名'),
        d.message ? '说明: ' + d.message : ''
      ];
      return L.api.get('/api/diag/com-probe').then(function (probe) {
        var detail = (probe.data && probe.data.report) || '';
        if (out) out.textContent = lines.filter(Boolean).join('\n') +
          (detail ? '\n\n逐步检测:\n' + detail.split(' | ').join('\n') : '');
      });
    }).catch(function () {
      if (out) out.textContent = '检测失败。';
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
            L.toast('已填入类名 ' + w.className + '，别忘了保存。', 'good');
          });
          host.appendChild(button);
        });
    });
  });

  /* ---- shared ------------------------------------------------------------ */

  function report(result) {
    var data = result.data || {};
    var message = data.message || (result.ok ? '完成。' : '操作失败。');
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
          ? (template.name || '') + '（临时直播，不参与自动匹配）'
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
