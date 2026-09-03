# Live Control Panel · 直播控制面板

教会直播电脑上的本地服务，给非技术操作员一个极简的直播控制界面。

它做四件事：

1. 通过 **YouTube Data API v3** 建播、绑定推流密钥、上传封面、结束直播
2. 通过 **obs-websocket v5** 控制 OBS Studio 推流与场景切换
3. 通过 **Win32 API** 控制 WPS 演示翻页 —— 操作员在 iPad 上没有键盘，这是 iPad 方案成立的前提
4. 可选：把调音台原声实时送进 **Gemini 实时翻译**，生成第二条语音，同一画面出两条直播（原声 / 译音）

它**不做**采集、编码、推流（由 OBS 承担）。面板只是遥控器：**关掉面板不会中断正在进行的直播。**
双语是同一条原则的延伸：**翻译坏掉不影响原声那条直播。**

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
dotnet test                                    # 428 个单元/接口测试
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
  Translate/          GeminiProtocol, GeminiSession, TranslationService,
                      Audio（抽象）, WasapiAudioEngine（NAudio/WASAPI）
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

## 双语直播：同一画面，两条语音

可选功能，**默认关闭**。开启后，每场直播建**两条** YouTube 直播：一条走调音台原声，一条走 Gemini 实时翻译生成的人声。画面只编码一次。

```
                    ┌──── 视频编码器（唯一一份）────┐
摄像机 ──→ OBS ─────┤                              │
                    └──────────────┬───────────────┘
                                   │
                     ┌─────────────┴─────────────┐
                  音轨1 原声                  音轨2 译音
                     │                           │
                     ▼                           ▼
                  Mux #1                      Mux #2
              （OBS 主推流）            （obs-multi-rtmp 目标）
                     │                           │
                     ▼                           ▼
            YouTube 直播 A                YouTube 直播 B
              （原声）                      （译音）
```

译音那一路的声音链路全在这台 PC 内部：

```
调音台 USB（OBS 也在采的那个录音设备）
  → 面板 WASAPI 共享模式采集 → 降混单声道、重采样 16 kHz
  → Gemini Live（BidiGenerateContent，WebSocket）
  → 返回 24 kHz PCM → 播到 VB-CABLE 的 CABLE Input
  → OBS 以「音频输入采集 → CABLE Output」拿回来，只路由到音轨 2
```

### 五个决定了整块设计的取舍

**面板仍然只调一次 `StartStream`。** obs-multi-rtmp 的第二个目标勾「与 OBS 同步开始/停止」，由插件自己跟随主推流。这不是省事：上游插件**没有** obs-websocket vendor 接口（只有第三方 fork 有），面板无法单独启停它。与其依赖一个 fork，不如让面板少管一路输出——也就没有第二路输出会和主路走失。

**代价是面板看不见第二路输出，所以改由 YouTube 侧观测。** 后台每 30 秒读一次译音那条 broadcast 的 lifecycle 状态：第二路断了，YouTube 会先把那条直播结束，面板据此报「翻译流已断」并指向 OBS 多路推流面板。一次 list 调用 1 个配额单位，对每天 10000 的额度是零头。

**视频编码器共用，不是编两次。** 插件的 Video Settings 选「与 OBS 主输出相同」（Get from OBS），CPU 开销不翻倍。凌晨那台机器要同时跑采集、编码和 WPS 放映，双份编码不是可选项。

**必须两个推流密钥。** 一个 `liveStream` 同一时刻只能绑一条进行中的 broadcast，所以两条直播物理上无法共用一个密钥。设置页分别创建，各填一次，此后不动。

**采集设备必须显式选，面板不猜。** 不填不会回退到系统默认录音设备 —— 那会让翻译去采摄像头麦克风或别的什么，把翻错的内容播出去，而面板一切正常。**静音是可诊断的，自信地翻错一个房间不是。** 同理，设备下拉是异步填的，所以前端在它还没填好时不上报选择、后端也不让空值覆盖已配好的设备：一次抢在加载完成之前的保存曾经足以静默擦掉这项配置。

**翻译的存活按状态对账，不挂在某个调用点上。** 原来只有面板的「结束直播」会停它，而部署文档写明的兜底是「在 OBS 点停止推流、在 YouTube Studio 手动收播」—— 走兜底就永远不会停。跨日回收把广播从状态里清掉之后，会话还开着、调音台还占着、配额还在烧，界面上却是「翻译正在运行」而没有任何直播。现在后台循环问的是「状态里还需要翻译吗」，不需要就停。枚举「哪些路径该停它」是漏了两次的原因；对账漏不了。

**中英不定用 `echoTargetLanguage` 解决，不是两条译音流。** 讲员可能讲中文也可能讲英文，甚至一场里换。会话配置里目标语言固定（每个场次可配），`echoTargetLanguage: true` 让「讲的已经是目标语言」的段落原样透传而不是静音。所以一个 Gemini 会话就够：目标语言设 `en` 时第二条流永远是英文，设 `zh-CN` 时永远是中文。源语言由模型自动识别，不需要指定。

### 翻译不影响原声，是被测出来的，不是被声称的

开播序列里翻译占第 4 步（在开始推流之前，让音轨 2 从第一帧就有内容）。这一步以及创建、绑定、封面里所有与译音相关的失败，状态都是 **`warn`** 而不是 `failed`：不中断序列、不提供「重试这一步」、服务照常上线，译音那条静音。`DualBroadcastTests` 逐条钉住了这些路径——翻译服务拒绝启动、抛异常、第二条建播失败、绑定失败、封面失败、译音那条始终不上线，每一条都断言 `outcome.Ok` 且原声那条 `live`。

反过来也钉住了：`EndPreviousAsync` 的遗留清理会排除**本场的两条** broadcast，否则它会把几分钟前自己创建的译音那条结束掉。

### 部署（只做单语可整段跳过）

已经跑起来的单语部署**不需要改动任何现有配置**：原有的推流密钥、场景、音频设备、计划任务全部照旧。下面全是**加法**，而且总开关关闭时面板上不会出现任何与翻译相关的东西。

新机部署的完整版（含每一步的逐条验收与故障处置）在 `docs/DEPLOYMENT.md` 的 3.4 / 7.7 / 8.7 三节；这里是能照着做完的浓缩版。

#### 1. 装两样东西，重启 OBS

| 装什么 | 哪里拿 | 干什么用 |
| --- | --- | --- |
| **VB-CABLE** 虚拟声卡（免费） | https://vb-audio.com/Cable/ ，**管理员身份**运行 `VBCABLE_Setup_x64.exe` → Install Driver → **重启电脑** | 面板把译音播到 `CABLE Input`，OBS 从 `CABLE Output` 拿回来。声音全程留在这台 PC 内部 |
| **obs-multi-rtmp** 插件 | https://github.com/sorayuki/obs-multi-rtmp/releases ，选与 OBS 大版本匹配的安装包 | 把同一份编码好的画面推第二路 RTMP |

装完 Windows 声音设置里应出现 `CABLE Input`（播放）与 `CABLE Output`（录音）这一对，OBS 的 **Docks** 菜单里应出现 **Multiple output**。

> **别**把 `CABLE Input` 设成 Windows 默认播放设备。面板是按设备 id 明确指定的，设成默认只会让系统提示音也灌进译音流。

#### 2. 面板设置页：创建第二个推流密钥

设置页「AI 翻译（第二条直播）」→ **「创建翻译用推流密钥」**（两步确认），把出来的串流密钥留着，第 4 步要填。

一个 `liveStream` 同一时刻只能绑一条进行中的 broadcast，所以两条直播**物理上无法共用一个密钥**。主推流那个密钥不要动。

#### 3. OBS：开多音轨，把译音接进来，做音轨路由

**a. Settings → Output → Output Mode 改为 `Advanced`**（多音轨的前提）
- **Streaming** 标签页 → **Audio Track** 选 **1**
- **Recording** 标签页 → **Audio Track** 把 **1** 和 **2** 都勾上（这一格决定编码器实际编哪几轨；不勾 2，音轨 2 永远是空的）

**b. 加译音源**：Sources → **+** → **Audio Input Capture** → 命名 `翻译音频` → Device 选 **CABLE Output** → OK。
两个场景都要有这个源（第二个场景用 **Add Existing** 引用同一个），否则切场景时译音会断。

**c. 音轨路由**（整套配置最容易错的一格）：Audio Mixer → 右键任一来源 → **Advanced Audio Properties**：

| 来源 | 音轨 1 | 音轨 2 | Audio Monitoring |
| --- | --- | --- | --- |
| `ProFX`（调音台） | ✅ | ⬜ | Monitor Off |
| `翻译音频`（CABLE Output） | ⬜ | ✅ | Monitor Off |

两行都**只勾一个**。调音台若也勾了音轨 2，第二条直播会同时听到原声和译音，两种语言叠在一起。

#### 4. OBS：第二路推流（Docks → Multiple output → Add new target）

| 项 | 填什么 |
| --- | --- |
| Name | `英文`（或 `译音`） |
| RTMP Server | `rtmp://a.rtmp.youtube.com/live2` |
| RTMP Key | 第 2 步创建的**翻译用**密钥（**不是**主推流那个） |
| Video Settings → Encoder | **「与 OBS 主输出相同」/ Get from OBS** ← **必须这样选** |
| Audio Settings → Audio Track | **2** |
| Other Settings | 勾 **Sync start with OBS** 与 **Sync stop with OBS** |

Video Encoder 选「与 OBS 主输出相同」是关键：画面只编码一次，两路共用同一份编码结果。选具体编码器就会再编一遍，凌晨那台机器同时跑采集、编码和 WPS 放映，撑不住第二份。

勾了同步启停之后，**面板和操作员都不需要再碰这个面板**：面板点「开始直播」→ OBS 主推流启动 → 插件自动带起第二路。

#### 5. 面板设置页：填「AI 翻译」这一段

Gemini 用的是 **Google AI Studio 的普通 API Key**（https://aistudio.google.com/apikey → Create API key），不需要服务账号、不走 Vertex。

| 项 | 填什么 |
| --- | --- |
| 启用 AI 翻译 | 勾上（总开关） |
| Gemini API Key | 上面创建的 key |
| 模型 | 默认 `models/gemini-3.5-live-translate-preview`。Google 改预览模型名时改这里就行，不用重新编译 |
| 目标语言 | `en`（译音说英文）或 `zh-CN`（译音说中文）。**源语言不填**，模型自己识别 |
| 讲员已在说目标语言时原样透传 | **勾上**（`echoTargetLanguage`）。中英混着讲时，已经是目标语言的段落原样过去而不是静音 |
| 第二条直播的标题后缀 | `" (English)"`。第二条标题 = 原标题 + 后缀，两者**必须不同**（丢失响应的建播靠标题精确匹配来认领，同名会互相认错） |
| 采集设备 | 调音台那个**录音**设备（如 ProFX）—— 就是 OBS 在用的同一个，WASAPI 共享模式下两边都能打开。**必填**：不填面板不会拿系统默认设备去凑，翻译直接不启动 |
| 播放设备 | **CABLE Input**。⚠️ **绝不能选调音台的播放端** —— 那会把译音送进 PA、混回原声那条直播，还可能啸叫。面板对此不做「回退到系统默认」，找不到指定设备就报错 |
| OBS 里翻译音频源的名称 | `翻译音频`（与第 3b 步一致）。填了自检才会检查这个源存在 |
| 翻译音频用第几条音轨 | `2`（与第 3c 步一致）。这一格只用于自检提示与文档 —— OBS 的音轨路由无法由面板设置 |

#### 6. 按场次开关（可选）

不是每场都要双语，也不是每场都翻同一个方向。`%ProgramData%\LiveControlPanel\templates.json` 里每个场次可加两个字段（改完重启面板）。下面只列与翻译有关的字段，**每个场次原有的字段保持原样**：

```json
{ "id": "sunday-service", "translate": true,  "targetLanguage": "en"    }
{ "id": "morning-service", "translate": true,  "targetLanguage": "zh-CN" }
{ "id": "friday-prayer",   "translate": false                            }
```

- `translate` —— 该场是否出译音那条。**缺省 `true`**，即总开关一开就全场次生效；不想双语的场次显式写 `false`
- `targetLanguage` —— 该场的目标语言，覆盖设置页的值

中文讲道那场出英文、英文讲道那场出中文，用的是同一台面板、同一个摄像机、同一份编码。设置页的场次表有一列「翻译」，可以一眼核对哪几场是双语、翻到哪个语言。

#### 7. 验证（这四项都要做）

1. **链路测试**：设置页点「测试翻译链路（约 20 秒）」，对麦**连续讲几句**。通了会回显「听到的原话」和「翻译结果」；不通会直接说是 key 错、采集设备错、还是没听到声音
2. **自检多一项**：双语场次的开播前自检从五项变六项，多出的「翻译」也要绿（缺 key、缺第二个密钥、虚拟声卡不在、OBS 里找不到译音源，各有对应提示）
3. **两条都上线**：开播后「直播链接」出现两个链接，两个都能打开，**画面一样、声音不同**
4. **拔网线演练（最重要的一项）**：直播进行中拔掉网线或关掉 WiFi 十几秒再恢复 —— 面板「AI 翻译」应变红或变黄，而**原声那条直播不能断**；恢复网络后点「重新连接翻译」应能恢复

第 4 项是这块设计的核心声明，不要跳过。凌晨独自值守时看到翻译报警，正确处置是「点一次重新连接，不行就不管它，把这场播完」——**绝不要为了修翻译去停直播**。

#### 最容易错的四处

| 症状 | 几乎总是这一处 |
| --- | --- |
| 译音那条**没声音** | 播放设备不是 `CABLE Input`，或 `翻译音频` 没勾音轨 2 |
| 译音那条**根本没上线** | 多路推流目标没勾「与 OBS 同步开始」，或密钥填的是主推流那个 |
| 译音那条**能听到两种语言叠在一起** | 调音台那一行同时勾了音轨 1 和音轨 2 |
| **CPU 直接顶满** | 多路推流目标的 Video Encoder 没选「与 OBS 主输出相同」，在编第二份 |

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

### 7. 中英文切换：服务端消息带双语，不按请求本地化

面板右上角有全局语言按钮，**默认中文**，选择按设备记住，操作页与设置页共用。

服务端**每条面向操作员的消息都同时带中英文**（`{zh, en}`），由前端选择显示哪一种。没有做按请求本地化，原因是状态经 WebSocket 推送：服务端渲染会让语言在建立连接时就被固定，切换需要重连，而且 PC 与 iPad 看同一个面板时无法各用一种语言 —— 七人共用一台电脑，语言偏好并不共用。代价是每条消息多一个短字符串，而状态包只有几 KB。

静态界面文字走 `data-i18n` 键，由 `wwwroot/i18n.js` 解析；切换时它会重新渲染所有由状态生成的内容，不会有一半留在另一种语言。

`LocalizationTests` 遍历面板真正会产生的消息 —— 每项自检的每条分支、每个编排步骤、每种错误映射、每种 Telegram 失败、幻灯片各路径 —— 断言中英双语都非空、两者不同、且**英文侧不含汉字**。半中半英比全中文更糟：操作员无法判断缺的那半是 bug 还是面板不会说。

### 8. 端口 5088 在部分 Windows 上被系统占用

本机上 Hyper-V 保留了 4990–5089，绑定 5088 直接 `SocketException 10013`。默认值仍按需求保持 5088，但：

- 启动失败时日志给出可操作的说明，而不是 bind 堆栈
- 端口可在 `settings.json` 里改

部署前建议先查：`netsh interface ipv4 show excludedportrange protocol=tcp`

---

## 测试

428 个测试，全部不接触真实的 YouTube / OBS / Telegram / Gemini，也不碰声卡。

```bash
dotnet test
```

| 测试文件 | 覆盖 |
| --- | --- |
| `ScheduleMatcherTests` | 开发计划 M1.3 的全部判据表 + 时间窗边界 + 早到/迟到容错 + 一日两场窗口不重叠 + 标题不补零 |
| `OrchestratorTests` | 幂等（连点 5 次 / 并发 5 次）、失败可从该步重试、停播、一日两场 |
| `PreflightTests` | 五项自检的每条分支；OBS 三种失败原因各给对应处置；自检失败**不阻断**开播；双语场次多出的第六项只在该场次真的双语时出现 |
| `NotificationTests` | Telegram 幂等、失败可重试、模板渲染 |
| `ConfigStoreTests` | 种子数据、删目录后重建、损坏文件降级、访问码生成 |
| `StateManagerTests` | 四相位状态机、快照深拷贝、并发安全 |
| `EndpointTests` | 真实路由表 + 访问码/PIN 门禁 + 各接口契约 + 临时直播默认标题 |
| `SupportingTests` | obs v5 认证算法、虚拟网卡过滤、错误文案不含技术术语、重试策略、窗口匹配 |
| `SlideControlTests` | 默认关闭、关闭时不碰 Win32/COM、诊断接口关闭时仍可用、启用后无放映时的提示 |
| `SettingsPinTests` | PIN 为固定默认值、两台安装 PIN 相同但访问码不同、改过的 PIN 不被重置 |
| `LocalizationTests` | 每条自检/步骤/错误/Telegram/幻灯片消息都有中英双语、两者不同、英文侧不含汉字 |
| `EndpointTests`（幻灯片部分） | 预览返回 PNG、COM 不可用时 404、显式页码透传、访问码门禁、诊断接口需 PIN |
| `DualBroadcastTests` | 双建播/双绑定/双封面、标题后缀、只调一次 `StartStream`、译音那条各种失败都只 `warn` 且原声照常上线、遗留清理排除本场两条、Telegram 带两个链接 |
| `TranslationServiceTests` | 会话生命周期、断线重连与 `goAway` 立即重连、译音写进虚拟声卡、缺 Key/缺播放设备时拒绝启动且不碰声卡、设备中途掉线上报、20 秒链路测试的三种结论 |
| `TranslationHealthTests` | **不猜采集设备**（缺设备时拒绝启动、自检提前报出）、以及翻译的存活按状态对账：没有译音广播就停、广播还活着就不动、广播已结束就停、**在 YouTube Studio 收播 + 跨日回收之后不会留着会话继续烧配额** |
| `GeminiProtocolTests` | setup 报文（含 `translationConfig`、不声明源语言）、音频分片格式、多段音频按序拼接、坏帧/非 JSON 帧不致命 |
| `TranslationPlanTests` | 总开关与单场次开关、目标语言的场次级覆盖、`ready` 需要第二个密钥与 API Key、译音标题必与原声不同 |
| `TranslationAudioTests` | 峰值电平（含 `short.MinValue` 不溢出成负数）、WASAPI 不定长块重新分帧不丢音 |

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

### 一次全面审查补上的缺陷（摘要）

- **部分保存会重置未提交的设置段**（数据丢失）：`PUT /api/settings` 曾绑定 `AppSettings`，其各段属性带初始化器、永不为 null —— 设置页「发送测试消息」那次只含三个字段的保存，会把 OBS 密码、场景名、幻灯片配置、时间窗静默重置为默认值。已改为真正可空的 `SettingsPatch`，并有回归测试钉住。
- **遗留直播一键清理对没开播过的会报错**：`created`/`ready` 状态不能 `transition(complete)`（YouTube 拒绝 `invalidTransition`），但它们照样占着共享推流密钥 —— 这类现在走 `delete`，只有 `live`/`testing` 才 transition。
- **没有日切**：面板长期运行时，周三晚场的「已结束」会留到周四凌晨，04:40 的操作员看到的不是自动就绪的 Ready。现在过了午夜自动清理已完成的场次与未开播的手动选择（正在直播的永不动），`StateManager` 时钟可注入以便测试。
- **等待上线对已结束的直播会空轮询满 60 秒**，然后给出永远不会成功的「重试」——现在立即失败并说明原因。
- **OBS 密码错误引发每秒重连**：TCP 连接成功就重置退避，而密码被拒正是「连接成功后被关闭」——现在只有完成身份认证的会话才重置退避；另外给单条消息加了 8MB 上限。
- **WebSocket 推送可能乱序**：每条消息 `Task.Run` 抢信号量，编排快速连推时旧状态可能覆盖新状态 —— 改为按客户端串联发送，有顺序测试守护。
- **前端网络失败无 catch**：面板重启瞬间点「开始直播」，按钮会永久禁用 —— fetch 失败现在解析为失败结果而不是 rejection。
- **输入校验**：时间窗限 0–720 分钟（守住双场次日不歧义）、PIN 强制四到六位数字、模板 weekday 限 0–6 且排期场次必须有可解析的开始时间。

---

## 接口

写操作一律校验访问码；设置类接口另需 `X-Settings-Pin`。两道门都返回 403，响应体的 `reason` 区分是哪一道：`"code"` = 访问码无效，`"pin"` = 设置密码不对。

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/state` | 完整状态快照 |
| WS | `/ws` | 状态推送 |
| GET | `/api/preflight` | 触发一次自检 |
| POST | `/api/broadcast/start-today` | 一键开播编排（七步，幂等） |
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
| POST | `/api/stream-key/create` | 创建可复用推流密钥（一次性，需 PIN）；`?slot=translation` 创建译音那条用的第二个密钥 |
| GET | `/api/audio-devices` | 列出录音/播放设备，供设置页选采集设备与虚拟声卡（需 PIN） |
| POST | `/api/translate/test` | 20 秒实链路测试，返回听到的原话与翻译结果（需 PIN） |
| POST | `/api/translate/restart` | 直播中重连翻译（只需访问码——这是译音静音时操作员唯一有用的动作） |
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
6. 做双语才要：**Google AI Studio 的 API Key**（<https://aistudio.google.com/apikey>，普通 API Key，不需要服务账号）

**面板里做的**

7. 设置页填 Client ID / Secret → 「开始授权」
8. 设置页「创建可复用推流密钥」→ 把串流密钥填进 OBS（**此后永不再改**）
9. 设置页填 Telegram token 与 chat_id → 「发送测试消息」确认
10. 让 WPS 进入全屏放映，然后**调用 `GET /api/diag/com-probe`** 确认 WPS 的自动化接口支持到哪一层。
   若整条链到 `Slide.Export` 都通过，翻页走 COM、页码与下一页预览都可用；
   若在中途断掉，翻页会退回按键 —— 此时必须在设置页「列出所有窗口」里选中放映窗口填入类名，
   并实测按 `PostMessage` 能否真的翻页，不行就把 `strategy` 改成 `SendInput`（会短暂抢焦点）
11. 设置页填 `obs.videoSourceNames`（采集卡、电视采集源的名字），让 `video` 自检生效

**双语（不做双语可整段跳过）**

全部是加法，不改动上面任何一条。逐步操作见前面「双语直播：同一画面，两条语音」里的**部署**一节，这里只是勾选清单：

- 装 **VB-CABLE** 与 **obs-multi-rtmp**，重启电脑与 OBS
- 设置页「创建翻译用推流密钥」（**必须是第二个密钥**，一个 liveStream 只能绑一条进行中的直播）
- OBS 输出模式改 **Advanced**；Streaming 音轨 **1**、Recording 音轨勾 **1+2**
- OBS 加源「音频输入采集 → CABLE Output」，命名 `翻译音频`，**两个场景都要有**
- 高级音频属性：调音台**只勾音轨 1**、`翻译音频`**只勾音轨 2**，两者监听都 **Monitor Off**
- 多路推流第二个目标：翻译用密钥 + 视频编码器**「与 OBS 主输出相同」** + 音轨 **2** + 勾**同步开始/停止**
- 设置页「AI 翻译」：Gemini API Key、目标语言、采集设备（调音台那个**录音**设备）、播放设备（**CABLE Input**，⚠️ 绝不能选调音台的播放端）
- 点「测试翻译链路」对麦讲几句，确认回显了听到的原话与翻译结果；确认自检第六项「翻译」为绿
- **拔网线演练**：直播中断网十几秒，确认原声那条不断、翻译报警、恢复后能重连

**系统配套**

12. 防火墙放行端口 5088，**规则须同时覆盖「专用」与「公用」**（教会 WiFi 的网络配置文件分类可能变化）；
    先用 `netsh interface ipv4 show excludedportrange protocol=tcp` 确认 5088 没被系统保留
13. Windows 自动登录（配合 UPS，断电重启后自动恢复）—— **这是面板随登录启动方案的前提**
14. Windows Update「使用时间」覆盖 **04:00–20:00** —— 默认会在凌晨装更新并重启，正好撞上 04:40 的场次
15. OBS 开机自启，浏览器停靠面板指向 `http://localhost:5088/?k=<访问码>`（`localhost` 属安全上下文，剪贴板 API 可用）

**分批上线**，不要七场一起切：Sunday 10:30 → Wednesday/Friday 18:00 → 最后才是 04:40 的五场。**凌晨场最不该用来试新东西。**

---

## 运维

日志：`%ProgramData%\LiveControlPanel\logs\panel-<date>.log`，按天轮转，保留 31 天。

| 症状 | 处理 |
| --- | --- |
| 面板打不开 | 确认服务在跑；检查防火墙；用 `?k=` 带访问码的完整地址 |
| 启动即退出，日志说端口 | 端口被占用或被 Windows 保留，改 `settings.json` 的 `port` |
| OBS 未连接 | **打开 OBS 不等于开了 WebSocket 服务器** —— obs-websocket 默认是关的。在 OBS 里点 工具 → WebSocket 服务器设置 → 勾选「启用 WebSocket 服务器」，并把密码填进设置页。自检会按实际原因给出对应提示，日志也会写明 |
| 授权失效 | 设置页「重新授权」；面板会在剩余 14 天内主动预警 |
| 上一场未结束 | 自检会提示并提供一键结束 |
| 忘记访问码 / PIN | 看 `%ProgramData%\LiveControlPanel\settings.json` |

**兜底路径**（面板不可用时）：直接在 OBS 点开始推流（推流密钥固定不变）；YouTube API 不可用时在 Studio 手动建播、用同一个密钥。

---

## 许可

本仓库为教会内部使用而开发。
