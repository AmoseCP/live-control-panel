/*
 * Language switching. Chinese is the default; the choice is remembered per device.
 *
 * Two kinds of text:
 *   - Static page text, keyed by data-i18n / data-i18n-placeholder attributes and looked up here.
 *   - Server messages, which arrive carrying both languages ({zh, en}) and are resolved by pick().
 *
 * Server messages are dual-language rather than localized per request because state arrives over a
 * WebSocket push: localizing server-side would fix the language when the socket opened, so switching
 * would need a reconnect, and a PC and an iPad watching the same panel could not differ. Seven people
 * share this machine and they do not share a language.
 */
(function (global) {
  'use strict';

  var STORE_KEY = 'lcp_lang';

  var DICT = {
    /* ---- shared ---- */
    'app.title': ['直播控制面板', 'Live Control Panel'],
    'app.langButton': ['English', '中文'],
    'offline': ['连接中断，正在重新连接…', 'Connection lost, reconnecting…'],

    /* ---- phase: NoSchedule ---- */
    'noschedule.heading': ['本日无排期', 'Nothing scheduled today'],
    'noschedule.notYet': ['还没到时间', 'Not time yet'],
    'noschedule.loading': ['正在读取下一场时间…', 'Loading the next service time…'],
    'noschedule.none': ['未来两周没有排期。', 'Nothing scheduled in the next two weeks.'],
    'noschedule.pickOther': ['手动选择一场 →', 'Choose a service →'],
    'noschedule.prepareNow': ['现在就开始准备', 'Start preparing now'],
    'noschedule.next': ['下一场：', 'Next: '],
    'noschedule.willBeReady': ['到时间会自动就绪。', 'It becomes ready on its own.'],
    'noschedule.startsToday': ['今天 ', 'today at '],
    'noschedule.startsAt': [' 开始', ''],

    /* ---- phase: Ready ---- */
    'ready.heading': ['今天', 'Today'],
    'ready.start': ['开始今天的直播', "Start today's broadcast"],
    'ready.notThisOne': ['不是这一场？', 'Not this service?'],
    'ready.scheduledFor': ['预定开始 ', 'Scheduled for '],

    /* ---- progress ---- */
    'progress.heading': ['正在开始直播', 'Starting the broadcast'],
    'progress.retryStep': ['重试第 {n} 步', 'Retry step {n}'],

    /* ---- preflight ---- */
    'preflight.heading': ['开播前自检', 'Pre-start checks'],
    'preflight.note': ['自检有问题也可以照常开播。', 'You can go live even if a check fails.'],
    'preflight.endPrevious': ['结束上一场直播', 'End the previous broadcast'],
    'preflight.reauthorize': ['去设置页重新授权', 'Re-authorize on the settings page'],

    /* ---- phase: Live ---- */
    'live.heading': ['正在直播', 'On air'],
    'live.elapsed': ['已直播', 'Elapsed'],
    'live.bitrate': ['码率', 'Bitrate'],
    'live.dropped': ['丢帧', 'Dropped'],
    'live.scene': ['当前画面', 'Current scene'],
    'live.sendTelegram': ['发送链接到 Telegram', 'Send the link to Telegram'],
    'live.sentTelegram': ['已发送到 Telegram（{time}）', 'Sent to Telegram ({time})'],
    'live.retryTelegram': ['重试发送到 Telegram', 'Retry sending to Telegram'],
    'live.stop': ['结束直播', 'End the broadcast'],
    'live.confirmStop': ['确定要结束这场直播吗？结束后无法继续。',
      'End this broadcast? It cannot be resumed.'],

    /* ---- scenes / slides ---- */
    'scene.heading': ['切换画面', 'Switch scene'],
    'slides.heading': ['幻灯片', 'Slides'],
    'slides.position': ['第 {current} / {total} 页', 'Slide {current} of {total}'],
    'slides.prev': ['◀ 上一页', '◀ Previous'],
    'slides.next': ['下一页 ▶', 'Next ▶'],
    'slides.previewAlt': ['下一页预览', 'Preview of the next slide'],
    'slides.previewCaption': ['下一页（第 {n} 页）', 'Next slide ({n})'],

    /* ---- phase: Ended ---- */
    'ended.heading': ['直播已结束', 'Broadcast ended'],
    'ended.another': ['开始另一场 →', 'Start another service →'],

    /* ---- link ---- */
    'link.heading': ['直播链接', 'Broadcast link'],
    'link.copy': ['复制链接', 'Copy link'],
    'link.hint': ['复制不成功时，可长按上面的地址手动复制。',
      'If copying does not work, press and hold the address above to copy it by hand.'],
    'link.copied': ['已复制链接。', 'Link copied.'],
    'link.copyFailed': ['这台设备不允许自动复制，请长按上面的地址手动复制。',
      'This device will not copy automatically. Press and hold the address above to copy it.'],

    /* ---- picker ---- */
    'picker.heading': ['选择场次', 'Choose a service'],
    'picker.adHocLabel': ['临时直播（排期之外的额外直播，标题可以改）',
      'Ad-hoc broadcast (outside the schedule; the title can be edited)'],
    'picker.adHocCreate': ['用这个标题建播', 'Create with this title'],
    'picker.cancel': ['取消', 'Cancel'],
    'picker.titleRequired': ['请先填写标题。', 'Enter a title first.'],

    /* ---- status ---- */
    'status.heading': ['状态', 'Status'],
    'status.obsConnected': ['OBS 已连接', 'OBS connected'],
    'status.obsDisconnected': ['OBS 未连接（请打开 OBS）', 'OBS not connected (open OBS)'],
    'status.authValid': ['授权有效', 'Authorization valid'],
    'status.authValidDays': ['授权有效（剩余约 {n} 天）', 'Authorization valid (about {n} days left)'],
    'status.authProblem': ['授权需要处理', 'Authorization needs attention'],
    'status.lastAction': ['上次操作：', 'Last action: '],
    'status.recheck': ['重新自检', 'Run checks again'],
    'status.settings': ['设置 →', 'Settings →'],

    /* ---- access refused ---- */
    'access.heading': ['打不开面板', 'Cannot open the panel'],
    'access.headingSettings': ['打不开设置页', 'Cannot open the settings page'],
    'access.hint': ['访问码会在管理员重装面板后变化。请让管理员重新给你二维码或链接。',
      'The access code changes if the administrator reinstalls the panel. Ask them for a fresh QR code or link.'],

    /* ---- generic ---- */
    'generic.done': ['完成。', 'Done.'],
    'generic.failed': ['操作失败，请重试。', 'Something went wrong. Please retry.'],
    'generic.noAccessCode': ['缺少访问码。请扫描管理员提供的二维码打开本页。',
      'No access code. Open this page from the QR code the administrator gave you.'],
    'generic.badAccessCode': ['访问码无效。请重新扫描二维码打开本页。',
      'Invalid access code. Re-open this page from the QR code.'],
    'generic.pageTurnFailed': ['翻页失败。', 'The page turn failed.'],

    /* ---- settings page ---- */
    'settings.title': ['设置 · 直播控制面板', 'Settings · Live Control Panel'],
    'settings.back': ['← 返回控制面板', '← Back to the panel'],
    'settings.pinHeading': ['请输入设置密码', 'Enter the settings password'],
    'settings.pinPlaceholder': ['四到六位数字', 'four to six digits'],
    'settings.unlock': ['解锁', 'Unlock'],
    'settings.pinHint': ['默认密码 <strong>0000</strong>。改过之后忘记了，可以在直播电脑上打开 ' +
      '<code>%ProgramData%\\LiveControlPanel\\settings.json</code>，看 <code>settingsPin</code> 一项；' +
      '日志 <code>logs\\panel-&lt;日期&gt;.log</code> 每次启动也会写一行。',
      'The default is <strong>0000</strong>. If it was changed and forgotten, open ' +
      '<code>%ProgramData%\\LiveControlPanel\\settings.json</code> on the streaming PC and look at ' +
      '<code>settingsPin</code>; it is also written to <code>logs\\panel-&lt;date&gt;.log</code> on every start.'],
    'settings.pinWrong': ['设置密码不对。默认是 0000；改过的话见 settings.json。',
      'Wrong settings password. The default is 0000; if changed, see settings.json.'],
    'settings.pinEmpty': ['请输入设置密码。', 'Enter the settings password.'],

    'settings.accessHeading': ['访问地址与二维码', 'Access addresses and QR code'],
    'settings.accessHint': ['让操作员用 iPad 扫这个码，然后「添加到主屏幕」。',
      'Have the operator scan this with their iPad, then "Add to Home Screen".'],
    'settings.accessNone': ['没有找到可用的局域网地址，请检查这台电脑是否连上了教会 WiFi。',
      'No usable LAN address found. Check that this PC is on the church WiFi.'],
    'settings.accessLocal': ['本机（OBS 停靠面板用这个）', 'This PC (use this for the OBS browser dock)'],
    'settings.accessMdns': ['mDNS 名称', 'mDNS name'],
    'settings.copyAddress': ['复制这个地址', 'Copy this address'],
    'settings.qrAlt': ['访问二维码', 'Access QR code'],

    'settings.authHeading': ['YouTube 授权', 'YouTube authorization'],
    'settings.clientId': ['Client ID', 'Client ID'],
    'settings.clientSecret': ['Client Secret', 'Client Secret'],
    'settings.authorize': ['开始授权 / 重新授权', 'Authorize / re-authorize'],
    'settings.revoke': ['清除现有授权', 'Clear the existing authorization'],
    'settings.authNone': ['尚未授权或授权已失效。', 'Not authorized, or the authorization has expired.'],
    'settings.authOk': ['授权有效', 'Authorization valid'],
    'settings.authOkDays': ['授权有效，剩余约 {n} 天', 'Authorization valid, about {n} days left'],
    'settings.confirmRevoke': ['确定清除现有授权？清除后需要重新授权才能建播。',
      'Clear the existing authorization? A new one is needed before any broadcast can be created.'],

    'settings.keyHeading': ['推流密钥（一次性）', 'Stream key (one-time)'],
    'settings.keyHint': ['创建一次即可长期使用，此后每场直播只做绑定，OBS 里不必再改。',
      'Created once and reused; each broadcast only binds to it, and OBS never needs changing.'],
    'settings.keyCurrent': ['当前 streamId：', 'Current streamId: '],
    'settings.keyCreate': ['创建可复用推流密钥', 'Create a reusable stream key'],
    'settings.keyAddress': ['推流地址（填入 OBS「服务器」）', 'Ingest address (OBS "Server")'],
    'settings.keyValue': ['串流密钥（填入 OBS「串流密钥」）', 'Stream key (OBS "Stream Key")'],
    'settings.keyCopy': ['复制串流密钥', 'Copy the stream key'],
    'settings.confirmKey': ['创建一个新的可复用推流密钥？创建后需要把密钥填进 OBS。',
      'Create a new reusable stream key? It will need entering in OBS.'],

    'settings.telegramHeading': ['Telegram', 'Telegram'],
    'settings.telegramToken': ['Bot Token', 'Bot token'],
    'settings.telegramChat': ['群 ID（负数；超级群以 -100 开头）',
      'Group id (negative; supergroups start with -100)'],
    'settings.telegramTemplate': ['消息模板（可用 {title} 与 {url}）',
      'Message template (may use {title} and {url})'],
    'settings.telegramTest': ['发送测试消息', 'Send a test message'],

    'settings.obsHeading': ['OBS', 'OBS'],
    'settings.obsUrl': ['WebSocket 地址', 'WebSocket address'],
    'settings.obsPassword': ['WebSocket 密码', 'WebSocket password'],
    'settings.obsSceneCamera': ['摄像机场景名', 'Camera scene name'],
    'settings.obsSceneSlides': ['幻灯片场景名', 'Slides scene name'],
    'settings.obsAudio': ['音频输入名（用于自检）', 'Audio input name (for the checks)'],
    'settings.obsVideo': ['画面来源名（用于自检，多个用逗号分隔）',
      'Video source names (for the checks; comma-separated)'],
    'settings.obsListInputs': ['列出 OBS 里的输入名', 'List the inputs OBS knows about'],
    'settings.readFailed': ['读取失败。', 'Could not read.'],

    'settings.slidesHeading': ['幻灯片控制', 'Slide control'],
    'settings.slidesHint': ['默认<strong>不开启</strong>。开启前请先让 WPS 进入放映，点「检测可用性」' +
      '看清哪条路能用 —— 有些放映程序忽略投递的按键，只有自动化接口有效；也可能反过来。不开启时操作页不会出现翻页按钮。',
      'Off <strong>by default</strong>. Before enabling it, put WPS into presentation mode and use ' +
      '"Check availability" to see which path works — some presentation programs ignore posted ' +
      'keystrokes and only the automation interface works, and sometimes the reverse. While it is off, ' +
      'no paging controls appear on the operator page.'],
    'settings.slidesEnable': ['启用幻灯片控制', 'Enable slide control'],
    'settings.slidesProbe': ['检测可用性', 'Check availability'],
    'settings.slidesClass': ['窗口类名', 'Window class name'],
    'settings.slidesTitle': ['窗口标题正则（可留空）', 'Window title regex (optional)'],
    'settings.slidesStrategy': ['发送方式', 'Delivery method'],
    'settings.slidesPostMessage': ['PostMessage（默认，不抢焦点）', 'PostMessage (default, does not steal focus)'],
    'settings.slidesSendInput': ['SendInput（回退方案，会短暂抢焦点）', 'SendInput (fallback, briefly steals focus)'],
    'settings.slidesListWindows': ['列出所有窗口', 'List all windows'],
    'settings.slidesProbing': ['检测中…', 'Checking…'],
    'settings.slidesProbeFailed': ['检测失败。', 'The check failed.'],
    'settings.slidesFilled': ['已填入类名 {name}，别忘了保存。',
      'Filled in the class name {name} — remember to save.'],
    'settings.probeSession': ['会话', 'Session'],
    'settings.probeSessionOk': ['正常', 'normal'],
    'settings.probeSessionBad': ['会话 0，翻页无法工作！', 'session 0 — paging cannot work!'],
    'settings.probeCom': ['自动化接口', 'Automation interface'],
    'settings.probeComNone': ['未连上', 'not attached'],
    'settings.probePresenting': ['正在放映', 'Presenting'],
    'settings.probePage': ['页码', 'Slide'],
    'settings.probePageNone': ['读不到', 'unavailable'],
    'settings.probePreview': ['下一页预览', 'Next-slide preview'],
    'settings.probeAvailable': ['可用', 'available'],
    'settings.probeUnavailable': ['不可用', 'unavailable'],
    'settings.probeWindow': ['放映窗口(按键用)', 'Show window (for keystrokes)'],
    'settings.probeWindowFound': ['已找到', 'found'],
    'settings.probeWindowNone': ['未找到 / 未配置类名', 'not found / class name not configured'],
    'settings.probeNote': ['说明', 'Note'],
    'settings.probeSteps': ['逐步检测', 'Step-by-step check'],
    'settings.yes': ['是', 'yes'],
    'settings.no': ['否', 'no'],

    'settings.otherHeading': ['其他', 'Other'],
    'settings.otherHeadingTitle': ['设置', 'Settings'],
    'settings.defaultDescription': ['默认简介', 'Default description'],
    'settings.defaultThumbnail': ['默认封面文件（相对数据目录）',
      'Default thumbnail file (relative to the data directory)'],
    'settings.windowBefore': ['匹配窗口：开始前多少分钟', 'Match window: minutes before the start'],
    'settings.windowAfter': ['匹配窗口：开始后多少分钟', 'Match window: minutes after the start'],
    'settings.newPin': ['修改设置密码（留空则不改）', 'Change the settings password (blank leaves it)'],

    'settings.templatesHeading': ['场次', 'Services'],
    'settings.colName': ['名称', 'Name'],
    'settings.colWeekdays': ['星期', 'Weekdays'],
    'settings.colTime': ['时间', 'Time'],
    'settings.templatesNote': ['星期：0=周日 … 6=周六。修改场次请直接编辑服务器上的 templates.json 后重启服务。',
      'Weekdays: 0 = Sunday … 6 = Saturday. To change services, edit templates.json on the server and restart.'],
    'settings.adHocRow': ['（临时直播，不参与自动匹配）', ' (ad-hoc; never matched automatically)'],

    'settings.save': ['保存设置', 'Save settings']
  };

  function current() {
    try {
      var stored = global.localStorage.getItem(STORE_KEY);
      if (stored === 'en' || stored === 'zh') return stored;
    } catch (e) { /* private mode */ }
    return 'zh';
  }

  var lang = current();

  function set(next) {
    lang = next === 'en' ? 'en' : 'zh';
    try { global.localStorage.setItem(STORE_KEY, lang); } catch (e) { /* ignore */ }
    document.documentElement.setAttribute('lang', lang === 'en' ? 'en' : 'zh');
    apply();
  }

  function toggle() { set(lang === 'zh' ? 'en' : 'zh'); }

  /** Looks up a static key. {placeholders} are replaced from params. */
  function t(key, params) {
    var entry = DICT[key];
    var text = entry ? entry[lang === 'en' ? 1 : 0] : key;
    if (!params) return text;

    return text.replace(/\{(\w+)\}/g, function (whole, name) {
      return Object.prototype.hasOwnProperty.call(params, name) ? params[name] : whole;
    });
  }

  /** Resolves a dual-language server message. Tolerates a plain string. */
  function pick(message) {
    if (message == null) return '';
    if (typeof message === 'string') return message;
    if (lang === 'en') return message.en || message.zh || '';
    return message.zh || message.en || '';
  }

  /**
   * Re-applies every translated node. Called on load and on each switch — the whole page, both pages,
   * from one place, so nothing can be left behind in the other language.
   */
  function apply() {
    var nodes = document.querySelectorAll('[data-i18n]');
    for (var i = 0; i < nodes.length; i++) {
      var node = nodes[i];
      var key = node.getAttribute('data-i18n');
      if (node.hasAttribute('data-i18n-html')) node.innerHTML = t(key);
      else node.textContent = t(key);
    }

    var placeholders = document.querySelectorAll('[data-i18n-placeholder]');
    for (var j = 0; j < placeholders.length; j++) {
      placeholders[j].setAttribute('placeholder', t(placeholders[j].getAttribute('data-i18n-placeholder')));
    }

    var titled = document.querySelector('[data-i18n-title]');
    if (titled) document.title = t(titled.getAttribute('data-i18n-title'));

    var button = document.getElementById('btn-lang');
    if (button) button.textContent = t('app.langButton');

    // Anything rendered from state rather than from the DOM has to be rebuilt too.
    if (typeof global.LCP_onLanguageChange === 'function') global.LCP_onLanguageChange();
  }

  global.LCP_I18N = {
    t: t,
    pick: pick,
    apply: apply,
    toggle: toggle,
    set: set,
    get lang() { return lang; }
  };
})(window);
