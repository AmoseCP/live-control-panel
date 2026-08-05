# Live Control Panel · 直播控制面板

教会直播电脑上的本地服务，给非技术操作员一个极简的直播控制界面。

它做三件事：

1. 通过 **YouTube Data API v3** 建播、绑定推流密钥、上传封面、结束直播
2. 通过 **obs-websocket v5** 控制 OBS Studio 推流与场景切换
3. 通过 **Win32 API** 控制 WPS 演示翻页 —— 操作员在 iPad 上没有键盘，这是 iPad 方案成立的前提

它**不做**采集、编码、推流（由 OBS 承担）。面板只是遥控器：**关掉面板不会中断正在进行的直播。**

---

## 现场约束（决定了所有设计取舍）

| 约束 | 对设计的影响 |
| --- | --- |
| 每周 8 场聚会，7 位不同的人各负责 | 无并发控制、无操作者身份、无冲突仲裁 |
| 同一人同时负责放映和直播 | 单场固定操作 ≤ 3 次（开播 → 发通知 → 停播） |
| 其中 5 场在**凌晨 04:40**，独自在场 | 零培训可用；每条错误都必须能自助处置 |
| 操作员用各自的**个人 iPad**，也可能直接用 PC | 两种方式功能完全等价 |
| 周三、周五**一日两场**，共用一个推流密钥 | 「上一场未结束」是最高风险自检项 |

---

## 快速开始

```bash
# 需要 .NET 8 SDK
dotnet test                                    # 250 个单元/接口测试
dotnet run --project src/LiveControlPanel       # 默认 http://localhost:5088
```

首次启动会在 `%ProgramData%\LiveControlPanel\` 生成配置。**每次**启动都会把访问码与设置 PIN 写进日志：

```
[INF] Access code: tftvvvgw   Settings PIN: 0000   (both in C:\ProgramData\LiveControlPanel\settings.json)
```

用 `http://localhost:5088/?k=<访问码>` 打开面板。开发时可用 `LCP_DATA_DIR` 环境变量把数据目录指到别处。

### 两个口令

| | 默认值 | 作用 | 在哪找 / 怎么改 |
| --- | --- | --- | --- |
| **访问码** `accessCode` | 随机 8 位 | 局域网门禁，URL 带 `?k=`，二维码里已含 | `settings.json` / 启动日志 / 设置页「访问地址与二维码」 |
| **设置 PIN** `settingsPin` | **`0000`**（固定） | 防七人误改设置，不是安全机制（需求 6.5） | `settings.json` / 启动日志 / 设置页「修改设置密码」 |

PIN 固定而访问码随机，是因为两者目的不同：PIN 只防误触，随机化会导致没人打得开设置页；访问码是真的门禁，必须每台机器不同。

### 发布单文件 exe

```bash
dotnet publish src/LiveControlPanel -c Release
```

得到一个**自包含单文件 exe**（约 46 MB），目标机器**无需安装 .NET**。`wwwroot` 已嵌入程序集，所以只复制这一个 exe 就能运行 —— 已验证。

### 随用户登录启动（**不要**装成 Windows 服务）

```powershell
# 计划任务：用户登录时启动，崩溃后由任务计划重启
$action  = New-ScheduledTaskAction -Execute "C:\LiveControlPanel\LiveControlPanel.exe"
$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:COMPUTERNAME\<操作账号>"
$set     = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
             -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName LiveControlPanel -Action $action -Trigger $trigger -Settings $set -RunLevel Highest
```

**这一点与需求 2 不同，原因是硬性的。** 需求 2 要求托管为 Windows 服务，需求 5.3 要求用 Win32 控制 WPS 放映窗口 —— **两者不能同时成立**：

- Windows 服务运行在**会话 0**，WPS 与 OBS 运行在操作员的**会话 1**（本机实测：`services.exe`/`svchost` 在 0，`explorer`/`OBS` 在 1）
- 窗口句柄与 COM 运行对象表（ROT）都**按会话隔离**，所以从会话 0 既找不到放映窗口，也 attach 不到 WPS

装成服务的后果：**翻页、页码、下一页预览全部静默失效**（OBS / YouTube / Telegram 不受影响，它们走 TCP/HTTP）。需求 5.3 说翻页是 iPad 方案成立的前提，这条会在凌晨无声失效。

因此程序**启动时会检测自身会话**，若在会话 0 会在日志里明确写出来，而不是让它悄悄不工作。

需求 2 想要的「OBS 启动时面板已就绪」仍然满足：M7.3 本来就要求开启 **Windows 自动登录**，机器开机后自动进入桌面，面板随登录启动且 Kestrel 绑定只要一秒，早于 OBS 初始化其浏览器停靠面板。

---

## 架构

```
src/LiveControlPanel/
  Program.cs          启动、DI、访问码中间件、静态资源、WebSocket、后台服务
  Config/             AppPaths, AppSettings, ServiceTemplate, ConfigStore, Seed
  Core/               ScheduleMatcher, StateManager, Orchestrator, Preflight,
                      NotificationService, StateHub, FriendlyError, RuntimeState
  Youtube/            YouTubeAuth, YouTubeClient, DpapiDataStore, Retry
  Obs/                ObsClient（原始 WebSocket）, ObsProtocol
  Slides/             SlideController, Win32, WpsCom
  Notify/             TelegramClient
  Net/                AccessInfoProvider（局域网地址 + 二维码）
  Api/                Endpoints, AccessGate
  wwwroot/            index.html, settings.html, app.js, settings.js, common.js, style.css
tests/LiveControlPanel.Tests/
```

### 关键设计决策

**状态全部在内存里，一律不持久化。** 面板崩溃或重启不能影响直播；重启后从 OBS 与 YouTube 重新推导状态。

**前端不直连 obs-websocket。** 从 iPad 访问时 `localhost` 指向 iPad 自己，而且 OBS 密码不该下发到浏览器。全部由后端代理。

**全程 HTTP，不配 HTTPS。** 页面要用 `ws://` 连自己的后端，HTTPS 页面会因混合内容被浏览器拦掉。

**前端不轮询。** 状态变化由服务端经 `/ws` 推送；iPad 锁屏唤醒后自动重连。

**数据目录在 `%ProgramData%`，不在用户配置目录。** 服务账户对用户目录的访问不可靠。

---

## 与需求文档的差异

以下几处是实现时必须做的判断，都不改变需求意图：

### 1. 自行实现 obs-websocket 客户端，未使用 `OBSWebsocketDotNet`

需求 4.4 的音频电平自检需要订阅 `InputVolumeMeters`。该事件属于协议的**高频事件组**（bit 16），**不在** `EventSubscription.All` 里，必须显式订阅。

而 NuGet 上的 `obs-websocket-dotnet` 5.0.1 在 Identify 报文里**根本不发送 `eventSubscriptions` 字段**（已核对程序集：字符串表中无此字段），OBS 因此使用默认掩码，`InputVolumeMeters` 永远不会到达。

需求 2.1 明确允许「自行实现原始 WebSocket + JSON」，因此 `Obs/ObsClient.cs` 直接实现协议（含 v5 的 SHA256 challenge 认证）。副产品：少一个依赖，重连逻辑完全可控。

### 2. `settings.json` 增加了三个字段

| 字段 | 原因 |
| --- | --- |
| `youTube.clientId` / `clientSecret` | 需求 5.1 要求桌面应用型 OAuth 客户端，但 3.2 的配置结构里没有存放位置 |
| `youTube.assumedValidityDays` | 需求 8 要求显示「授权剩余有效期」，但 Google 对已发布应用的 refresh token 不公布有效期。默认 180 天（对应官方的闲置失效上限），是一个偏保守的倒计时 |
| `obs.videoSourceNames` | 需求 4.4 的 `video` 自检项需要知道该检查哪些采集源 |

### 3. 时间窗默认改为 −60 / +120，上报 YouTube 的时间改为实际时刻

模板里的 `startTime` 是**对外公布的时间，不是实际发生的时间** —— 操作员会早到，也会迟到。据现场反馈，容错定为「早 1 小时、迟 2 小时」，因此 `matchWindow.afterMinutes` 默认从需求 3.2 的 `90` 改为 **`120`**（`beforeMinutes` 仍为 60）。

| 场次 | 打开面板即为 Ready 的时段 |
| --- | --- |
| Morning Service 04:40 | 03:40 – 06:40 |
| Wednesday / Friday 18:00 | 17:00 – 20:00 |
| Sunday Service 10:30 | 09:30 – 12:30 |

放宽后仍然**无歧义**：周三、周五一日两场，早场窗口 06:40 关闭，晚场窗口 17:00 才打开，中间不重叠。`Morning_and_evening_windows_never_overlap_on_a_two_service_day` 会把这两天逐分钟走一遍来守住这条性质。

超出窗口（例如迟到 3 小时）仍按需求 6.1 落到「本日无排期 + 下一场时间 + 手动选择入口」，由操作员手动选择场次，不做额外猜测。

**但「还没到时间」与「今天真的没有」分开措辞。** 需求 6.1 对 `NoSchedule` 只规定了一种文案，而这个相位实际涵盖两种完全不同的情形。提前到场的操作员（15:30 等 18:00 的场，或 03:00 等 04:40 的场）看到「本日无排期」，会以为自己记错了日子 —— 与事实正好相反。因此当下一场就在今天时，改为：

```
还没到时间
8/5/2026 Wednesday Service
今天 18:00 开始，还有约 2 小时 26 分钟。到时间会自动就绪。
[ 现在就开始准备 ]
[ 手动选择一场 → ]
```

「现在就开始准备」一键进入 Ready（`NextServiceState` 新增 `templateId`，前端才知道该开哪一场，不必走选择器）。今天确实没有排期、或今天的场次已过窗口时，文案仍是「本日无排期 + 下一场」。判断"是否今天"用的是 `serverTime` 而非设备时钟 —— 理由与临时直播标题相同。

相应地，`liveBroadcasts.insert` 的 `snippet.scheduledStartTime` 改为**实际按下开始的时刻**，而不是模板的名义时间。原因：`enableAutoStart=true`，整条编排从建播到 live 只有几十秒，18:25 才开播却告诉 YouTube「预定 18:00」，只会让观看页显示一个已经过去的时间。名义时间仍然用于场次匹配、标题生成，以及界面上的「预定开始 18:00」。

### 4. 临时直播的默认标题由服务端按系统日期生成

固定排期之外偶尔会有额外直播，走「手动选择一场」下半部分的临时直播入口（内置 `custom` 模板）。

需求 3.1 写的是「各字段留空，供手填」，但让操作员手打整条标题会引入两类错误：日期补零成 `08/05/2026`（违反需求 4.1），以及跨零点后打错日期。**标题一旦建播就改不了。** 因此 `custom` 模板改为带 `name = "Service"` 与标准的 `titleFormat`，默认标题即 `8/5/2026 Service`，操作员按需修改。

默认标题由 **`/api/templates/list` 在服务端渲染**（字段 `defaultTitle`），不在浏览器里算 —— iPad 的日期或时区设错时，不能让它造出错误日期的标题。走的是和固定场次完全同一条 `ScheduleMatcher.FormatTitle` 逻辑，所以月日永不补零。

`custom` 仍然没有 `weekdays` 与 `startTime`，因此永不参与自动匹配；`scheduledStart` 取当下。Telegram 通知与固定场次一致，发往同一个群（需求 9：确认只需一个群）。

### 5. 幻灯片控制：默认关闭，自动化接口优先，并新增下一页预览

**默认不控制 PPT**（`settings.slides.enabled = false`）。这是全部功能里唯一会伸手到别的程序里去的一项 —— 往窗口投按键、attach 到 COM 自动化对象 —— 而这两条路哪条能用是**每台机器不一样的**。关闭时：不枚举窗口、不尝试 COM、操作页不出现任何翻页按钮、`/api/slides/*` 明确回「没有启用」。

在设置页「幻灯片控制」勾选启用。同一张卡片上有「检测可用性」按钮，一次性报出会话号、自动化接口是否连上、页码、预览是否可用、放映窗口是否找到，以及逐个成员的检测结果 —— 先看清再决定要不要开。两个诊断接口在关闭状态下仍然可用，因为它们正是用来判断该不该开的。

需求 5.3 把 `PostMessage` 定向按键定为基线。**在 PowerPoint 16 上实测，按键两条路都不工作：**

| 方式 | 实测结果 |
| --- | --- |
| `PostMessage(WM_KEYDOWN, VK_RIGHT)` | 接口返回成功，但幻灯片**不动** —— 放映窗口忽略投递的按键消息 |
| `SendInput` | 同样不动。`SetForegroundWindow` 在后台进程里被 Windows 的前台锁拒绝，按键落到了别的窗口 |
| COM `View.Next()` / `Previous()` / `GotoSlide(n)` | **每次都成功**，不需要焦点，还能读回当前页码 |

所以 `SlideController` 改为**先走 COM，失败再退回按键**。需求 5.3 的按键实现完整保留 —— WPS 若没有自动化接口，它就是唯一的路。需求本身也预留了这个可能（「部分应用使用 raw input，`PostMessage` 可能无效，故必须保留回退」），实测证实了这一点，只是可用的那条路和文档假设的相反。

**下一页预览**（新增）：`GET /api/slides/preview[?n=]` 用 COM 的 `Slides.Item(n).Export` 渲染 PNG。COM 不可用时返回 404，前端据此隐藏整块，不显示坏图。实测延迟：进程内首次约 2.1 秒（一次性 COM/JIT 预热），之后同页命中 10 秒 memo 约 16ms，换页重新导出约 30ms。

#### 两个必须同时满足的前提（各花了不少时间才定位）

**（a）`GetActiveObject` 的出参必须按 `IDispatch` 封送，不能用 `IUnknown`。** 用 `IUnknown` 时 .NET 8 给出的 RCW 没接上 IDispatch 后期绑定，取任何成员都是 `DISP_E_UNKNOWNNAME`，尽管对象本身是对的。.NET Framework 在这里更宽容 —— 同样的写法在 PowerShell 里能跑，所以很容易误判成「平台不支持」。

**（b）`Presentation` 挂在 `SlideShowWindow` 上，不在 `View` 上。** 读 `View.Presentation` 会 `DISP_E_UNKNOWNNAME`。

`GET /api/diag/com-probe` 就是为定位这类问题加的：它逐个成员走完整条链并指出断在哪一步。上线时在教会那台机器上让 WPS 进入放映后调用一次，就能确定 WPS 支持到哪一层，不必靠文档猜。

### 6. 每周场次数：需求文档自身不一致

需求 1 的正文写「每周七场」，但 3.1 的模板表（`weekdays` 是**必须**照抄的种子数据）实际是 **8 场**：

```
morning-service   [1,2,3,4,5] 04:40  → 5
wednesday-service [3]         18:00  → 1
friday-prayer     [5]         18:00  → 1
sunday-service    [0]         10:30  → 1
                                     ─────
                                        8
```

需求 4.1 也提到「周三、周五的早晚场」，与表格一致。**实现以表格为准**（表格是硬性种子数据），测试 `The_seeded_week_matches_the_mandated_table_and_leaves_saturday_empty` 断言 8 场。周六无排期两种读法都一致。

> 这一处需要确认：是正文的「七场」笔误，还是排期表里某一场应当去掉。

### 7. 端口 5088 在部分 Windows 上被系统占用

本机上 Hyper-V 保留了 4990–5089，绑定 5088 直接 `SocketException 10013`。默认值仍按需求保持 5088，但：

- 启动失败时日志给出可操作的说明，而不是 bind 堆栈
- 端口可在 `settings.json` 里改

部署前建议先查：`netsh interface ipv4 show excludedportrange protocol=tcp`

---

## 测试

250 个测试，全部不接触真实的 YouTube / OBS / Telegram。

```bash
dotnet test
```

| 测试文件 | 覆盖 |
| --- | --- |
| `ScheduleMatcherTests` | 开发计划 M1.3 的全部判据表 + 时间窗边界 + 早到/迟到容错 + 一日两场窗口不重叠 + 标题不补零 |
| `OrchestratorTests` | 幂等（连点 5 次 / 并发 5 次）、失败可从该步重试、停播、一日两场 |
| `PreflightTests` | 五项自检的每条分支；自检失败**不阻断**开播 |
| `NotificationTests` | Telegram 幂等、失败可重试、模板渲染 |
| `ConfigStoreTests` | 种子数据、删目录后重建、损坏文件降级、访问码生成 |
| `StateManagerTests` | 四相位状态机、快照深拷贝、并发安全 |
| `EndpointTests` | 真实路由表 + 访问码/PIN 门禁 + 各接口契约 + 临时直播默认标题 |
| `SupportingTests` | obs v5 认证算法、虚拟网卡过滤、错误文案不含技术术语、重试策略、窗口匹配 |
| `SlideControlTests` | 默认关闭、关闭时不碰 Win32/COM、诊断接口关闭时仍可用、启用后无放映时的提示 |
| `SettingsPinTests` | PIN 为固定默认值、两台安装 PIN 相同但访问码不同、改过的 PIN 不被重置 |
| `EndpointTests`（幻灯片部分） | 预览返回 PNG、COM 不可用时 404、显式页码透传、访问码门禁、诊断接口需 PIN |

### 已验证的行为

单元测试之外，实机跑过：

- 单文件 exe 在**只有该 exe**的目录里启动，`wwwroot` 从程序集读出，中文完好
- 访问码门禁：无码 403，错码 403，query / header / cookie 三种方式均可
- `/auth/callback` 不需要访问码（Google 的重定向带不了）
- WebSocket：错码被拒、对码连上、收到首帧快照、服务端状态变化被推送
- `/api/diag/windows` 真实枚举到顶层窗口
- `/api/access-info` 只列出真实 Wi-Fi 地址（172.16.x.x），过滤掉环回与虚拟网卡，并给出 mDNS 名与二维码
- 排期匹配：周三 11:48 → `NoSchedule` + 下一场为当日 18:00 Wednesday Service（正是需求 M1.3 的判据之一）
- 幻灯片：面板作为**独立进程** attach 到另行启动的 PowerPoint 放映（`POWERPNT.EXE /S deck.pptx`），
  `next` 1→2→3→4、`prev` 4→3→2、`goto 4` 全部生效，页码经 `/api/state` 读回一致
- 下一页预览：`/api/slides/preview` 返回有效 PNG；越界页码返回 404；预览在界面上正确显示为「下一页（第 N 页）」
- 会话隔离：实测服务进程在会话 0、桌面应用在会话 1，据此改为随登录启动并加了启动检测

### 两个测试找出来的真实缺陷

1. **`IYouTubeClient.BindAsync` 撞上 Minimal API 的绑定约定。** ASP.NET Core 把任何 `BindAsync(...)` 当作参数绑定约定，路由表构建直接失败 —— **所有**接口一律 500。全部单元测试却照样通过，因为没有一个测试会去构建 Web 应用。已改名为 `BindStreamAsync`，并补上 `EndpointTests.The_whole_route_table_materializes` 守住这一类问题。

2. **手动选定的场次会被排期刷新冲掉。** `StateManager` 每次刷新都用自动匹配结果覆盖 `today`，于是需求 6.1 的「不是这一场？」和临时直播选完就没了。已加 `TodayState.Manual`：显式选择优先于日历，只有「开始另一场」会清除它。

---

## 接口

写操作一律校验访问码；设置类接口另需 `X-Settings-Pin`。两道门都返回 403，响应体的 `reason` 区分是哪一道：`"code"` = 访问码无效，`"pin"` = 设置密码不对。

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/state` | 完整状态快照 |
| WS | `/ws` | 状态推送 |
| GET | `/api/preflight` | 触发一次自检 |
| POST | `/api/broadcast/start-today` | 一键开播编排（六步，幂等） |
| POST | `/api/broadcast/retry/{step}` | 从指定步骤重试 |
| POST | `/api/broadcast/create` | 手动建播（模板 / 日期 / 标题覆盖） |
| POST | `/api/broadcast/stop` | 结束直播（需 `confirm: true`） |
| POST | `/api/broadcast/end-previous` | 结束遗留的上一场 |
| POST | `/api/broadcast/start-another` | 清空状态以开始一日中的第二场 |
| POST | `/api/obs/scene` | 切换场景 |
| POST | `/api/slides/next` \| `/prev` \| `/goto` | 幻灯片控制（先走 COM，失败退回按键） |
| GET | `/api/slides/preview` | 下一页预览 PNG；COM 不可用时 404 |
| POST | `/api/telegram/send` | 发送通知（幂等） |
| GET | `/api/access-info` | 局域网地址、mDNS 名、二维码 |
| GET | `/auth/start` \| `/auth/callback` | OAuth 授权 |
| GET/PUT | `/api/settings` \| `/api/templates` | 设置（需 PIN） |
| POST | `/api/stream-key/create` | 创建可复用推流密钥（一次性，需 PIN） |
| GET | `/api/diag/windows` | 枚举顶层窗口，用于确定 WPS 放映窗口（需 PIN） |
| GET | `/api/diag/slides` | 会话号、COM 可用性、当前/总页数、预览是否可用（需 PIN） |
| GET | `/api/diag/com-probe` | 逐个成员走完自动化对象链，指出断在哪一步（需 PIN） |

---

## 部署检查清单

**人工前置条件**

1. Google Cloud 项目启用 YouTube Data API v3；OAuth 同意屏幕用户类型 **External**，发布状态必须是 **`In production`**（`Testing` 状态下 refresh token 7 天过期，撑不过一周一次的使用）
2. OAuth 客户端类型 **桌面应用**，重定向地址 `http://localhost:5088/auth/callback`
3. BotFather 建 Telegram bot，拉进群，群里发 `/start`，从 `getUpdates` 取 chat_id（**负数**，超级群带 `-100` 前缀）
4. OBS：推流用**推流密钥模式**；音频源放在**全局音频设备**里（不要放进任何单个场景，否则切场景时声音会断）；桌面音频禁用；该音频源的监听关闭；Tools → WebSocket Server Settings 启用并设密码
5. 通用封面 1280×720、2 MB 以内，放到 `%ProgramData%\LiveControlPanel\thumbnails\default.jpg`

**面板里做的**

6. 设置页填 Client ID / Secret → 「开始授权」
7. 设置页「创建可复用推流密钥」→ 把串流密钥填进 OBS（**此后永不再改**）
8. 设置页填 Telegram token 与 chat_id → 「发送测试消息」确认
9. 让 WPS 进入全屏放映，然后**调用 `GET /api/diag/com-probe`** 确认 WPS 的自动化接口支持到哪一层。
   若整条链到 `Slide.Export` 都通过，翻页走 COM、页码与下一页预览都可用；
   若在中途断掉，翻页会退回按键 —— 此时必须在设置页「列出所有窗口」里选中放映窗口填入类名，
   并实测按 `PostMessage` 能否真的翻页，不行就把 `strategy` 改成 `SendInput`（会短暂抢焦点）
10. 设置页填 `obs.videoSourceNames`（采集卡、电视采集源的名字），让 `video` 自检生效

**系统配套**

11. 防火墙放行端口 5088，**规则须同时覆盖「专用」与「公用」**（教会 WiFi 的网络配置文件分类可能变化）；
    先用 `netsh interface ipv4 show excludedportrange protocol=tcp` 确认 5088 没被系统保留
12. Windows 自动登录（配合 UPS，断电重启后自动恢复）—— **这是面板随登录启动方案的前提**
13. Windows Update「使用时间」覆盖 **04:00–20:00** —— 默认会在凌晨装更新并重启，正好撞上 04:40 的场次
14. OBS 开机自启，浏览器停靠面板指向 `http://localhost:5088/?k=<访问码>`（`localhost` 属安全上下文，剪贴板 API 可用）

**分批上线**，不要七场一起切：Sunday 10:30 → Wednesday/Friday 18:00 → 最后才是 04:40 的五场。**凌晨场最不该用来试新东西。**

---

## 运维

日志：`%ProgramData%\LiveControlPanel\logs\panel-<date>.log`，按天轮转，保留 31 天。

| 症状 | 处理 |
| --- | --- |
| 面板打不开 | 确认服务在跑；检查防火墙；用 `?k=` 带访问码的完整地址 |
| 启动即退出，日志说端口 | 端口被占用或被 Windows 保留，改 `settings.json` 的 `port` |
| OBS 未连接 | 打开 OBS 即可，面板会自动重连，不必重启服务 |
| 授权失效 | 设置页「重新授权」；面板会在剩余 14 天内主动预警 |
| 上一场未结束 | 自检会提示并提供一键结束 |
| 忘记访问码 / PIN | 看 `%ProgramData%\LiveControlPanel\settings.json` |

**兜底路径**（面板不可用时）：直接在 OBS 点开始推流（推流密钥固定不变）；YouTube API 不可用时在 Studio 手动建播、用同一个密钥。

---

## 许可

本仓库为教会内部使用而开发。
