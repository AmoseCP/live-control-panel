# Live Control Panel · 新机部署文档

> 适用:在一台全新的 Windows 10/11 电脑上从零部署直播控制面板 + OBS Studio(英文版)。
> 按本文顺序操作即可完成部署;每一部分末尾都有**验收标准**,当前步骤验收通过后再进入下一部分。
> 文中 OBS 的按钮与菜单一律使用英文界面名称。

---

## 目录

1. [部署架构与前提](#1-部署架构与前提)
2. [准备清单(动手前收集齐)](#2-准备清单)
3. [安装软件](#3-安装软件)
4. [Windows 系统设置](#4-windows-系统设置)
5. [面板首次启动](#5-面板首次启动)
6. [开机自动启动(脚本)](#6-开机自动启动)
7. [OBS 配置(英文界面)](#7-obs-配置)
8. [面板设置页配置](#8-面板设置页配置)
9. [操作界面说明](#9-操作界面说明)
10. [上线前验收测试](#10-上线前验收测试)
11. [故障处理手册](#11-故障处理手册)
12. [附录:路径与命令速查](#12-附录)

---

## 1. 部署架构与前提

这台电脑上会运行三样东西:

| 组件 | 作用 | 由谁启动 |
| --- | --- | --- |
| **LiveControlPanel.exe** | 本地服务,提供操作网页(端口 5088),控制 YouTube / OBS / WPS | 计划任务,随用户登录自动启动 |
| **OBS Studio** | 采集、编码、推流(真正干活的) | 开机自启快捷方式 |
| **WPS Office / PowerPoint**(可选) | 放映幻灯片,面板可远程翻页 | 操作员手动打开 |

关键原则(与故障处理直接相关):

- **面板只是遥控器。** 关掉面板、面板崩溃,都不会中断正在进行的直播——推流是 OBS 在做。
- **面板不能装成 Windows 服务。** 服务运行在会话 0,而翻页功能必须和桌面程序在同一会话,装成服务翻页会静默失效。所以用「随登录启动的计划任务 + Windows 自动登录」实现开机即就绪。
- **推流密钥固定不变。** 部署完成后 OBS 里的密钥永不再改;每场直播的观看链接由面板自动新建,场场不同,结束后自动变成回放。

---

## 2. 准备清单

动手之前,确认以下东西都在手边:

**硬件**
- [ ] 直播电脑(Windows 10/11,建议接网线;确认有第二个显示输出口给投影)
- [ ] 摄像机 + HDMI 线 + USB 采集卡(如 AVerMedia)
- [ ] 调音台(如 Mackie ProFX 系列)+ USB 线
- [ ] 建议:UPS 不间断电源

**账号与信息**
- [ ] 教会 YouTube 频道的 Google 账号密码,且频道**已开通直播功能**(首次开通需 24 小时生效,提前办)
- [ ] Google Cloud Console 的 OAuth 客户端 **Client ID 和 Client Secret**(见 8.2 节;如果是换机部署,直接用原来的那对,不用新建)
- [ ] Telegram Bot Token 和群的 chat_id(见 8.4 节;换机部署直接沿用)

**文件**
- [ ] `LiveControlPanel.exe`(单文件,约 46 MB,构建方法见 3.3 节)
- [ ] 通用封面图:1280×720、2 MB 以内的 JPG

---

## 3. 安装软件

### 3.1 OBS Studio

1. 从 https://obsproject.com 下载最新版(28 以上即可,WebSocket 服务器已内置),安装时一路 Next。
2. 首次启动出现 **Auto-Configuration Wizard** 时选 **Optimize for streaming**,或直接 **Cancel**(后面手动配)。

### 3.2 WPS Office / PowerPoint(需要翻页功能才装)

正常安装即可。装好后打开一次、登录/激活,确保能进入全屏放映。

### 3.3 获得面板 exe(两种方式二选一)

无论哪种方式,产物都是同一个**自包含单文件 exe**——运行它的电脑不需要安装 .NET,网页文件已嵌入,不需要旁边放任何其它文件。

#### 方式 A:在开发机上构建,拷贝 exe 过去

新电脑上什么都不用装。在**开发机**上执行:

```powershell
cd <仓库目录>\live-control-panel
dotnet publish src/LiveControlPanel -c Release
```

产物在:

```
src\LiveControlPanel\bin\Release\net8.0-windows\win-x64\publish\LiveControlPanel.exe
```

把这**一个文件**用 U 盘或网络拷到新电脑的 `C:\LiveControlPanel\` 目录(自己新建这个目录)。

以后更新面板:开发机上重新 publish,再拷一次 exe(先结束新机上正在运行的面板)。

#### 方式 B:在新机上直接构建(新机装有 .NET 8 SDK 时)

新电脑需要先安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)(注意是 **SDK**,不是 Runtime——用 `dotnet --list-sdks` 有输出即对)和 [Git](https://git-scm.com)(或改从 GitHub 网页下载 ZIP 解压,则不需要 Git)。

> ⚠️ **先确认开发机上的代码已全部 `git push`**——clone 拿到的是 GitHub 上的版本,开发机上没推送的修复不会包含在内。

在**新电脑**上执行:

```powershell
git clone https://github.com/AmoseCP/live-control-panel.git
cd live-control-panel

dotnet test          # 可选但建议:全部测试通过说明构建环境正常

dotnet publish src/LiveControlPanel -c Release

New-Item -ItemType Directory -Force C:\LiveControlPanel
Copy-Item src\LiveControlPanel\bin\Release\net8.0-windows\win-x64\publish\LiveControlPanel.exe C:\LiveControlPanel\
```

以后更新面板:新机上进入仓库目录执行 `git pull`,再重复 publish 和 Copy-Item 两步(先结束正在运行的面板:`Stop-Process -Name LiveControlPanel -Force`)。

> 不要直接用 `dotnet run` 长期运行面板——它跑的是 Debug 构建且依赖源码目录。日常运行一律用 `C:\LiveControlPanel\LiveControlPanel.exe`,配合第 6 节的计划任务。

**✅ 验收:** `C:\LiveControlPanel\LiveControlPanel.exe` 存在;OBS 能打开。

---

## 4. Windows 系统设置

以下每一步都不可省略,它们对应真实的故障模式。

### 4.1 电源:永不睡眠

`Settings → System → Power` (电源):
- **Screen off**:随意(屏幕可以关)
- **Sleep:Never**(绝不允许睡眠——睡着了凌晨 4:40 就没有直播)

### 4.2 Windows Update 活跃时间

`Settings → Windows Update → Advanced options → Active hours`:改为**手动**,设为 **04:00 – 20:00**。
否则 Windows 默认在凌晨装更新重启,正好撞上 04:40 的场次。

### 4.3 Windows 自动登录

断电恢复后机器必须自动进桌面(计划任务才会启动面板)。推荐用微软官方 Sysinternals 工具:

1. 下载 https://learn.microsoft.com/sysinternals/downloads/autologon (Autologon.exe,免安装)
2. 运行,填入操作账号的用户名、域(本机名)、密码,点 **Enable**。密码会加密存储。

> 备选:Win+R 运行 `netplwiz`,取消勾选 "Users must enter a user name and password"。Windows 11 上若看不到该勾选项,先把 `Settings → Accounts → Sign-in options → "For improved security…"` 关掉。

### 4.4 防火墙放行 5088(iPad 能否访问就看这条)

以**管理员**打开 PowerShell,执行:

```powershell
New-NetFirewallRule -DisplayName "LiveControlPanel 5088" `
  -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5088 `
  -Profile Private,Public
```

规则必须同时覆盖 **Private 和 Public**——教会 WiFi 的网络配置文件分类可能变化。

### 4.5 确认 5088 没被系统保留

部分电脑(开了 Hyper-V/WSL)会把 5088 划进系统保留端口,面板会启动失败。检查:

```powershell
netsh interface ipv4 show excludedportrange protocol=tcp
```

若输出的某个区间**覆盖了 5088**:等面板首次启动生成配置后,把 `C:\ProgramData\LiveControlPanel\settings.json` 里的 `"port"` 改成一个不在保留区间里的值(如 5090),并把本文所有 5088 换成该值(防火墙规则也要重建)。

**✅ 验收:** 睡眠=Never;活跃时间 04:00–20:00;重启一次电脑能自动进桌面;防火墙规则存在;5088 不在保留区间。

---

## 5. 面板首次启动

1. 双击 `C:\LiveControlPanel\LiveControlPanel.exe`。**窗口没有任何输出是正常的**——所有日志都写到文件里。
2. 打开今天的日志(日期换成当天):

   ```
   C:\ProgramData\LiveControlPanel\logs\panel-<yyyyMMdd>.log
   ```

   找到这几行:

   ```
   [INF] Local URL: http://localhost:5088/?k=xxxxxxxx
   [INF] LAN URL:   http://192.168.x.x:5088/?k=xxxxxxxx (Wi-Fi)
   [INF] Access code: xxxxxxxx   Settings PIN: 0000
   [INF] Running in session 1; slide control can reach the desktop.
   ```

3. **抄下访问码(Access code)**。它是随机生成、这台机器专属的,永久有效;忘了就去 `C:\ProgramData\LiveControlPanel\settings.json` 里看 `accessCode` 字段。
4. 在本机浏览器打开 `http://localhost:5088/?k=<访问码>`——应看到面板主页(此时显示"本日无排期"或"还没到时间"都正常)。
5. 用手机/iPad 连**同一个 WiFi**,打开日志里的 LAN URL——也应能打开。打不开就是 4.4 防火墙没做对。

**✅ 验收:** 本机和 iPad 都能打开面板页面;日志里有 `Running in session 1` 字样。

---

## 6. 开机自动启动

### 6.1 面板:计划任务(不要装成 Windows 服务!)

以**管理员**打开 PowerShell,把 `<操作账号>` 换成实际登录用户名后整段执行:

```powershell
$action  = New-ScheduledTaskAction -Execute "C:\LiveControlPanel\LiveControlPanel.exe"
$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:COMPUTERNAME\<操作账号>"
$set     = New-ScheduledTaskSettingsSet -RestartCount 999 `
             -RestartInterval (New-TimeSpan -Minutes 1) `
             -ExecutionTimeLimit ([TimeSpan]::Zero) `
             -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName LiveControlPanel `
  -Action $action -Trigger $trigger -Settings $set -RunLevel Highest
```

这段配置的含义:

| 参数 | 作用 |
| --- | --- |
| `-AtLogOn` | 用户登录桌面时启动(配合 4.3 自动登录 = 开机即启动) |
| `-RestartCount 999` + `-RestartInterval 1分钟` | 面板崩溃后 1 分钟内自动重启,几乎无限次 |
| `-ExecutionTimeLimit Zero` | 不限运行时长(默认 72 小时会被强杀,必须清零) |
| `-MultipleInstances IgnoreNew` | 已在运行时不重复启动第二份 |
| `-RunLevel Highest` | 以最高权限运行 |

**为什么不装 Windows 服务:** 服务运行在会话 0,窗口句柄和 COM 对象表按会话隔离,从会话 0 永远找不到 WPS 的放映窗口——翻页、页码、下一页预览会**全部静默失效**,且 OBS/YouTube 一切正常,极难排查。面板启动时会自检:若日志出现 `Running in session 0` 警告,说明装错了。

### 6.2 OBS:启动文件夹快捷方式

1. 右键 `C:\Program Files\obs-studio\bin\64bit\obs64.exe` → **Show more options → Create shortcut**(放桌面)。
2. 右键该快捷方式 → **Properties**,在 **Start in** 填 `C:\Program Files\obs-studio\bin\64bit`(否则 OBS 报找不到 locale)。
3. Win+R 运行 `shell:startup` 回车,把快捷方式**移动**进打开的文件夹。

### 6.3 验证整条链

先手动结束现有进程(任务管理器结束 `LiveControlPanel.exe` 和 OBS),然后**重启电脑**,什么都不碰,等 2 分钟:

- 电脑自动进入桌面(4.3 生效)
- OBS 自动打开(6.2 生效)
- 浏览器打开 `http://localhost:5088/?k=<访问码>` 能访问(6.1 生效)

**✅ 验收:** 重启后不碰键盘,面板和 OBS 都自己起来了。

---

## 7. OBS 配置

以下按 OBS **英文界面**描述。所有配置只做一次。

### 7.1 启用 WebSocket 服务器(面板与 OBS 的通信通道)

1. 菜单栏 **Tools → WebSocket Server Settings**
2. 勾选 **Enable WebSocket Server**
3. **Server Port** 保持 `4455`
4. 保持 **Enable Authentication** 勾选,点 **Show Connect Info**,抄下 **Server Password**(第 8.1 节要填进面板)
5. **Apply → OK**

> 注意:**打开 OBS 不等于开了 WebSocket 服务器**,它出厂默认是关的。面板显示"OBS 未连接"时第一个查这里。

### 7.2 视频与输出参数

**Settings → Video:**
- Base (Canvas) Resolution:`1920x1080`
- Output (Scaled) Resolution:`1920x1080`
- FPS:`30`

**Settings → Output**(Output Mode: Simple 即可):
- Video Bitrate:`4500 Kbps`(网络上行富余可到 6000)
- Encoder:有 **Hardware (NVENC/QSV/AMD)** 就选硬件,没有选 x264

### 7.3 场景(Scenes)

面板按**名字**切换场景,名字必须与面板设置页完全一致(默认 `摄像机` 和 `PPT`):

1. 左下 **Scenes** 面板:把默认 "Scene" 右键 → **Rename** 为 `摄像机`;点 **+** 新建 `PPT`。
2. 选中 `摄像机` 场景 → **Sources** 面板点 **+** → **Video Capture Device** → 命名 `主摄像机` → **Device** 下拉选采集卡 → OK。
   - 预览显示采集卡的 "No Signal" 提示图 = OBS 侧正常,是摄像机→采集卡这段没通:确认摄像机开机、HDMI 插在采集卡 **HDMI IN**(不是 OUT)。
3. 选中 `PPT` 场景 → **Sources** 点 **+** → **Display Capture** → 命名 `PPT画面` → **Display** 选扩展屏(投影那块)→ OK。
   - 画面没铺满:右键该来源 → **Transform → Fit to Screen**(Ctrl+F)。
   - 若 PPT 是另一台电脑经采集卡进来,则改用 **Video Capture Device**,命名 `PPT采集`。

### 7.4 音频(要求最严格的一块,逐条照做)

1. **Settings → Audio → Global Audio Devices:**
   - **Desktop Audio:`Disabled`**(必须禁用,否则系统提示音进直播)
   - **Mic/Auxiliary Audio:** 选调音台的 USB 设备(如 Mackie ProFX)
   - 其余槽位全部 `Disabled`
2. 主界面 **Audio Mixer** 面板:右键该来源 → **Rename** → 改为 `ProFX`(必须与面板设置页「音频输入名称」一致,默认就是 ProFX)。
3. 右键该来源 → **Advanced Audio Properties** → **Audio Monitoring** 列确认为 **Monitor Off**(默认即关,确认即可;开着会现场啸叫)。
4. **绝不**把音频作为来源加进任何单个场景——必须走全局设备,否则切场景瞬间声音会断。
5. 电平标准:有人对麦克风正常讲话时,Audio Mixer 电平主要落在黄色区(约 −20 ~ −10 dB),不顶红。偏大偏小在**调音台上**调,OBS 音量条留在 0 dB。

### 7.5 推流设置(密钥在 8.3 节由面板生成后再填)

**Settings → Stream:**
- **Service:`YouTube - RTMPS`**
- **Server:`Primary YouTube ingest server`**
- 连接方式必须选 **Use Stream Key**(**不要**用 "Connect Account"——账号绑定模式会和面板的编排打架)
- **Stream Key:** 暂时留空,做完 8.3 回来填

### 7.6 把面板嵌进 OBS(浏览器停靠面板)

1. 菜单 **Docks → Custom Browser Docks…**
2. Dock Name 填 `控制面板`,URL 填 `http://localhost:5088/?k=<访问码>`,点 **Apply**
3. 把浮出的面板拖到 OBS 界面右侧停靠

> 面板的「结束直播」等按钮在这里是**两步确认**:点一下按钮变红提示再点一次,5 秒内再点才执行——这是特意为 OBS 内置浏览器设计的,没有弹窗是正常的。

**✅ 验收:** OBS 两个场景画面都正确;讲话时电平跳动;WebSocket 已启用并记下密码;浏览器停靠面板能显示面板页面。

---

## 8. 面板设置页配置

浏览器打开 `http://localhost:5088/settings.html?k=<访问码>`,输入 PIN(初始 `0000`)解锁。
**每改完一段都要点「保存」**,PIN 弹窗输 `0000`。

### 8.1 OBS 连接

- OBS 地址:保持 `ws://localhost:4455`
- OBS 密码:填 7.1 抄下的 Server Password
- 场景名(摄像机)/(PPT):保持 `摄像机` / `PPT`(若 OBS 里用了别的名字,这里改成一样)
- 音频输入名称:`ProFX`(与 OBS 混音器里的名字一致)
- **画面来源名:`主摄像机`**(只填会丢信号的硬件采集来源;显示器采集不填。多个用英文逗号分隔,如 `主摄像机, PPT采集`)

保存后回主页:自检里「OBS 已连接」应变绿。

### 8.2 YouTube 授权

**Google Cloud Console 侧(换机部署可跳过,直接用原 Client ID/Secret):**

1. https://console.cloud.google.com → 新建项目
2. **APIs & Services → Library** → 搜索并启用 **YouTube Data API v3**
3. **OAuth consent screen**:User Type 选 **External**;填完基本信息后,发布状态必须推到 **In production**(留在 Testing 状态的话 refresh token **7 天就过期**,面板每周都会要求重新授权)
4. **Credentials → Create Credentials → OAuth client ID**:Application type 选 **Desktop app**,创建后得到 Client ID 和 Client Secret

**面板侧(顺序很重要):**

1. 设置页粘贴 Client ID 和 Client Secret
2. **先点「保存」**(不保存直接授权会报"请先填写 Client ID")
3. 再点**「开始授权 / 重新授权」**→ 跳到 Google → **用教会频道的 Google 账号**登录并同意 → 看到「授权成功」页即完成
4. 授权必须**在这台电脑的浏览器**里完成(回调地址是 localhost),不能在 iPad 上做

### 8.3 创建可复用推流密钥

1. 设置页点**「创建可复用推流密钥」**(两步确认:再点一次红色按钮)
2. 页面显示 **串流密钥** → 复制
3. 回 OBS:**Settings → Stream → Stream Key** 粘贴 → OK
4. **此后永不再改。** 也不要再点这个创建按钮——再点会生成新密钥,OBS 里的旧密钥就作废了

### 8.4 Telegram 通知(用不到可跳过)

1. Telegram 里找 **@BotFather** → `/newbot` → 得到 **Bot Token**
2. 把 bot 拉进通知群,群里发一句 `/start`
3. 浏览器打开 `https://api.telegram.org/bot<TOKEN>/getUpdates`,找 `"chat":{"id":-100xxxxxxxxxx}` —— 这个**负数**就是 chat_id
4. 设置页填 Token 和 chat_id → 点「发送测试消息」→ 群里收到即成功

### 8.5 封面图

把 1280×720、2 MB 以内的 JPG 放到:

```
C:\ProgramData\LiveControlPanel\thumbnails\default.jpg
```

### 8.6 幻灯片控制(需要翻页功能才开)

1. 设置页「幻灯片控制」勾选**启用**,保存
2. 让 WPS/PowerPoint 打开一个课件并进入**全屏放映**
3. 点设置页的**「检测可用性」**:看自动化接口是否连上、页码能否读到、预览是否可用
4. 若检测不通:浏览器访问 `http://localhost:5088/api/diag/com-probe?k=<访问码>&pin=<PIN>`,它会逐步走完自动化链并指出断在哪一层;COM 走不通时在「列出所有窗口」里找到放映窗口,把窗口类名填入设置,退回按键方案

**✅ 验收:** 主页自检五项——OBS、声音(需有人对麦讲话)、上一场、授权、画面——全部绿色。

---

## 9. 操作界面说明

面板主页只有一个原则:**界面只显示当前有意义的操作**。右上角可切换中/英文(按设备记忆)。

### 9.1 四种页面状态

| 状态 | 什么时候出现 | 能做什么 |
| --- | --- | --- |
| **还没到时间 / 本日无排期** | 不在任何场次的时间窗内 | 看下一场信息;「现在就开始准备」提前进入就绪;「手动选择一场」处理计划外直播 |
| **就绪(Ready)** | 场次时间窗内(提前 1 小时到延后 2 小时) | 看五项自检;点**「开始直播」** |
| **直播中(Live)** | 直播进行时 | 看时长/码率/丢帧;切换画面(摄像机/PPT);翻页和下一页预览;「发送到 Telegram」;**「结束直播」(两步确认)** |
| **已结束(Ended)** | 停播后到当天午夜 | 看观看链接;「开始另一场」(一日两场时用) |

### 9.2 开始直播

点「开始直播」后面板自动执行六步:创建直播 → 绑定推流密钥 → 上传封面 → 切换画面 → 开始推流 → 等待 YouTube 上线。每步实时显示进度;**某一步失败不用从头来**,修复提示的问题后点「重试这一步」。多点几次「开始直播」不会创建重复直播,放心。

### 9.3 结束直播

点红色「结束直播」→ 按钮变成「再点一次,确认结束」→ 5 秒内再点一次才真正执行(不点自动恢复)。执行后 OBS 先停推流,再通知 YouTube 收播。**结束后链接自动变成回放,长期有效。**

### 9.4 自检项与一键修复

自检失败**不阻断开播**,但每条都要看:

- **上一场未结束**:一日两场共用密钥,上一场没收播会导致开播失败。点提示里的**「结束上一场直播」**一键清理。
- **声音没有电平**:自检读的是实时声音,需要有人对麦讲话时看这项。
- **画面没有图像**:摄像机没开机/采集卡线松。注意采集卡的 "No Signal" 提示图在 OBS 看来"有画面",开播前扫一眼 OBS 预览确认不是黑图仍是好习惯。

### 9.5 iPad 操作

1. iPad 连教会 WiFi,打开设置页「访问地址与二维码」里的 LAN 地址(或直接扫二维码)
2. Safari 分享菜单 → **添加到主屏幕**——以后一键进入
3. iPad 锁屏再解锁,面板会自动重连并刷新到最新状态;短暂显示离线横幅属正常

---

## 10. 上线前验收测试

按顺序完整走一遍,全部通过才算部署完成:

1. [ ] **自检全绿**:主页五项自检全部绿色(测声音时对麦讲话)
2. [ ] **完整直播闭环**:手动选一场(或用临时直播)→ 开始直播 → YouTube 上线 → 切换摄像机/PPT 画面各一次 → 翻页一次 → 发送 Telegram → 结束直播
3. [ ] **回放验证**:结束后等约 5 分钟,用**另一台设备**打开 Telegram 里的链接,确认能看回放
4. [ ] **iPad 全流程**:上述第 2 步全部在 iPad 上再做一遍(可以另开一场测试)
5. [ ] **断电恢复演练**:直接强制关机再开机,不碰键盘等 3 分钟——自动登录、面板、OBS 全部自己回来,面板页面可访问
6. [ ] **面板崩溃演练**:直播进行中,在任务管理器结束 `LiveControlPanel.exe` → **确认 YouTube 直播不受影响** → 1 分钟内计划任务把面板拉起来,刷新页面恢复"直播中"状态
7. [ ] **僵尸清理**:测试产生的未开播场次,用自检里的「结束上一场直播」清干净

> 正式切换请**分批**:先周日 10:30 → 再周三/周五 18:00 → 最后才是凌晨 04:40 的五场。凌晨场最不该用来试新东西。

---

## 11. 故障处理手册

**总原则:面板出任何问题都不影响正在进行的直播。** 最坏情况的兜底永远可用:直接在 OBS 点 **Start Streaming / Stop Streaming**(密钥固定),在 YouTube Studio 手动建播收播。

### 11.1 快速定位表

| 症状 | 处理 |
| --- | --- |
| **面板打不开(本机)** | 任务管理器看 `LiveControlPanel.exe` 在不在 → 不在就双击 exe 手动启动并看日志最后几行;确认 URL 带了 `?k=访问码` |
| **面板打不开(仅 iPad)** | 本机能开、iPad 不能开 = 防火墙问题,重做 4.4;确认 iPad 和电脑在同一 WiFi |
| **访问码/PIN 忘了** | 看 `C:\ProgramData\LiveControlPanel\settings.json` |
| **启动即退出,日志提示端口** | 端口被占或被保留:按 4.5 检查,改 `settings.json` 的 `port` 后重启面板 |
| **「OBS 未连接」** | ① OBS 开着吗 ② **Tools → WebSocket Server Settings 勾了 Enable 吗**(最常见) ③ 面板设置页密码和 OBS 里一致吗 |
| **「声音没有电平」** | 调音台开机了吗 → USB 插好了吗 → 通道推子推起来了吗 → 对麦讲话再看 |
| **「画面没有图像」** | 摄像机开机 → HDMI 在采集卡 HDMI IN → 换线试 |
| **「上一场未结束」** | 点提示里的「结束上一场直播」一键清理 |
| **「授权已失效」** | 设置页 → 重新授权(用教会频道账号);面板会在剩余 14 天时提前预警 |
| **开始直播卡在某一步** | 看该步的错误提示照做,然后「重试这一步」;反复失败看日志 |
| **翻页没反应** | ① 放映真的在全屏吗 ② 设置页「检测可用性」看断在哪 ③ 日志有 `Running in session 0` 说明被装成服务了,按第 6 节重装计划任务 |
| **Telegram 没发出去** | 直播中页面点「重试发送」;检查 Token/chat_id;bot 还在群里吗 |
| **页面显示不动/疑似卡死** | 刷新页面;iPad 上锁屏再解锁会强制重连 |

### 11.2 配置文件损坏(极少见)

若日志出现 `Could not read ...settings.json`:面板已自动用默认配置启动(**访问码是新的**,旧书签会 403),原文件保留在:

```
C:\ProgramData\LiveControlPanel\settings.json.bad-<时间戳>
```

处理:停面板 → 用记事本打开 `.bad` 备份,把 `accessCode`、`obs.password`、`telegram*`、`youTube.*`、`streamId` 等值抄回新的 `settings.json`(或直接把备份改回原名,前提是能修好 JSON 语法)→ 重启面板。

### 11.3 收集信息找维护者

日志按天存放、保留 31 天:

```
C:\ProgramData\LiveControlPanel\logs\panel-<yyyyMMdd>.log
```

报障时给出:出问题的时间点 + 当天日志文件 + 页面截图。

---

## 12. 附录

**路径速查**

| 内容 | 路径 |
| --- | --- |
| 程序 | `C:\LiveControlPanel\LiveControlPanel.exe` |
| 配置(访问码/PIN/密钥都在里面) | `C:\ProgramData\LiveControlPanel\settings.json` |
| 场次模板 | `C:\ProgramData\LiveControlPanel\templates.json` |
| 日志 | `C:\ProgramData\LiveControlPanel\logs\` |
| 封面图 | `C:\ProgramData\LiveControlPanel\thumbnails\default.jpg` |
| 操作页 | `http://localhost:5088/?k=<访问码>` |
| 设置页 | `http://localhost:5088/settings.html?k=<访问码>` |

**命令速查(PowerShell)**

```powershell
# 面板进程在吗
Get-Process LiveControlPanel -ErrorAction SilentlyContinue

# 强制结束面板(不影响直播;计划任务或手动可再启)
Stop-Process -Name LiveControlPanel -Force

# 实时看日志
Get-Content "C:\ProgramData\LiveControlPanel\logs\panel-$(Get-Date -Format yyyyMMdd).log" -Wait -Tail 20

# 5088 被谁占着
netstat -ano | Select-String ":5088"

# 计划任务状态
Get-ScheduledTask -TaskName LiveControlPanel

# 端口保留区间
netsh interface ipv4 show excludedportrange protocol=tcp
```
