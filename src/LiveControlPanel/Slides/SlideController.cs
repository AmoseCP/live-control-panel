using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using LiveControlPanel.Config;
using LiveControlPanel.Core;

namespace LiveControlPanel.Slides;

public interface ISlideController
{
    IReadOnlyList<WindowInfo> EnumerateWindows();
    SlidesState GetState();
    SlideResult Next();
    SlideResult Previous();
    SlideResult Goto(int slideNumber);

    /// <summary>
    /// A rendered slide image, or null when the presentation program offers no way to get one.
    /// <paramref name="slideNumber"/> null means "the next slide".
    /// </summary>
    SlidePreview? TryGetPreview(int? slideNumber);

    /// <summary>Deployment-time report on which optional COM capabilities actually work here.</summary>
    SlideDiagnostics Diagnose();

    /// <summary>
    /// Walks the automation object model member by member and names the step that fails. The answer
    /// differs between PowerPoint and WPS, so it has to be measured on the target machine.
    /// </summary>
    string ProbeCom();
}

public sealed record SlideResult(bool Ok, string Message);

public sealed record SlidePreview(byte[] Png, int SlideNumber, int Total);

public sealed record SlideDiagnostics(
    int SessionId,
    bool SessionIsolated,
    string? ComProgId,
    bool SlideShowRunning,
    int? Current,
    int? Total,
    bool PreviewSupported,
    bool TargetWindowFound,
    string Strategy,
    string? Message,
    string? Culture);

/// <summary>
/// Slide paging over Win32 (FR 5.3). This is what makes the iPad workflow possible: the operator
/// has no PC keyboard, so page turns must arrive without the panel ever holding focus.
///
/// Default strategy is <c>PostMessage</c>, which delivers straight to the presentation window's
/// message queue and leaves the browser in the foreground. Some applications read raw input
/// instead and ignore posted messages, so <c>SendInput</c> remains available as a fallback.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SlideController : ISlideController
{
    /// <summary>Wide enough to read a slide title on an iPad, small enough to be a few tens of KB.</summary>
    private const int PreviewWidth = 480;

    private static readonly TimeSpan PreviewMemoTtl = TimeSpan.FromSeconds(10);

    private readonly ConfigStore _config;
    private readonly ILogger<SlideController> _log;
    private readonly object _previewGate = new();

    private SlidePreview? _lastPreview;
    private DateTime _lastPreviewAt;

    public SlideController(ConfigStore config, ILogger<SlideController> log)
    {
        _config = config;
        _log = log;
    }

    public IReadOnlyList<WindowInfo> EnumerateWindows() =>
        Win32.EnumerateTopLevelWindows()
            .OrderByDescending(w => w.Visible)
            .ThenBy(w => w.ClassName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public SlidesState GetState()
    {
        var position = WpsCom.TryGetPosition();
        return new SlidesState
        {
            Available = FindTargetWindow() is not null,
            Current = position?.Current,
            Total = position?.Total,
        };
    }

    /// <summary>
    /// Renders a slide to PNG. Serialized because COM is not reentrant, and memoised on the last
    /// slide rendered so a PC and an iPad both showing the panel do not each pay for the export.
    /// </summary>
    public SlidePreview? TryGetPreview(int? slideNumber)
    {
        lock (_previewGate)
        {
            var position = WpsCom.TryGetPosition();
            if (position is null) return null;

            var target = slideNumber ?? position.Current + 1;
            if (target < 1 || target > position.Total) return null;

            if (_lastPreview is not null
                && _lastPreview.SlideNumber == target
                && _lastPreview.Total == position.Total
                && DateTime.UtcNow - _lastPreviewAt < PreviewMemoTtl)
            {
                return _lastPreview;
            }

            var path = Path.Combine(_config.Paths.PreviewDirectory, "next.png");
            try
            {
                Directory.CreateDirectory(_config.Paths.PreviewDirectory);
                if (File.Exists(path)) File.Delete(path);

                if (WpsCom.TryExportSlide(target, path, PreviewWidth) is null) return null;

                var preview = new SlidePreview(File.ReadAllBytes(path), target, position.Total);
                _lastPreview = preview;
                _lastPreviewAt = DateTime.UtcNow;
                return preview;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Rendering a preview of slide {Slide} failed", target);
                return null;
            }
        }
    }

    public string ProbeCom() => WpsCom.ProbeStrategies();

    public SlideDiagnostics Diagnose()
    {
        var sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var target = FindTargetWindow();
        var report = WpsCom.Diagnose(Path.Combine(_config.Paths.PreviewDirectory, "probe.png"));

        // Session 0 is the service session. Window handles and the COM running-object table are both
        // per-session, so from there neither paging nor preview can ever reach the operator's desktop.
        var isolated = sessionId == 0;
        var message = isolated
            ? "本进程运行在会话 0（Windows 服务会话）。幻灯片翻页与预览在此会话下无法工作 —— " +
              "窗口句柄与 COM 运行对象表都按会话隔离。请改为在用户登录时启动本程序。"
            : report.Error;

        return new SlideDiagnostics(
            SessionId: sessionId,
            SessionIsolated: isolated,
            ComProgId: report.ProgId,
            SlideShowRunning: report.SlideShowRunning,
            Current: report.Current,
            Total: report.Total,
            PreviewSupported: report.ExportSupported,
            TargetWindowFound: target is not null,
            Strategy: _config.Settings.Slides.Strategy,
            Message: message,
            Culture: report.Culture);
    }

    public SlideResult Next() => Advance(forward: true);

    public SlideResult Previous() => Advance(forward: false);

    /// <summary>
    /// Automation first, keystrokes second.
    ///
    /// FR 5.3 specifies directed keystrokes as the baseline, and they remain the fallback for a
    /// presentation program with no automation interface. But measured against PowerPoint 16 they do
    /// not work at all: posted WM_KEYDOWN is ignored, and SendInput would need the foreground window,
    /// which Windows will not hand to a background process. The automation call moves the show
    /// reliably, needs no focus, and reports the resulting position.
    /// </summary>
    private SlideResult Advance(bool forward)
    {
        var what = forward ? "下一页" : "上一页";

        if (forward ? WpsCom.TryNext() : WpsCom.TryPrevious())
            return new SlideResult(true, $"已翻到{what}。");

        var keystroke = SendKey(forward ? Win32.VK_RIGHT : Win32.VK_LEFT, what);
        if (keystroke.Ok) return keystroke;

        _log.LogWarning("Paging {What} failed through both automation and keystrokes", what);
        return keystroke;
    }

    public SlideResult Goto(int slideNumber)
    {
        if (slideNumber < 1) return new SlideResult(false, "页码必须大于 0。");

        // Absolute jumps need automation; there is no keystroke equivalent.
        if (WpsCom.TryGoto(slideNumber)) return new SlideResult(true, $"已跳转到第 {slideNumber} 页。");

        return new SlideResult(false, "无法跳页：当前放映程序未提供跳页接口，请用上一页/下一页。");
    }

    private SlideResult SendKey(ushort virtualKey, string what)
    {
        var strategy = _config.Settings.Slides.Strategy;

        if (string.Equals(strategy, "SendInput", StringComparison.OrdinalIgnoreCase))
            return SendViaSendInput(virtualKey, what);

        var target = FindTargetWindow();
        if (target is null)
            return new SlideResult(false, "找不到放映窗口：请确认幻灯片已进入放映状态。");

        var handle = new IntPtr(target.Handle);
        var key = new IntPtr(virtualKey);

        var down = Win32.PostMessage(handle, Win32.WM_KEYDOWN, key, IntPtr.Zero);
        var up = Win32.PostMessage(handle, Win32.WM_KEYUP, key, IntPtr.Zero);

        if (!down || !up)
        {
            _log.LogWarning("PostMessage to slide window {Handle} failed", target.Handle);
            return new SlideResult(false, "翻页指令发送失败：请在设置页改用 SendInput 方式后重试。");
        }

        return new SlideResult(true, $"已发送{what}。");
    }

    private SlideResult SendViaSendInput(ushort virtualKey, string what)
    {
        var target = FindTargetWindow();
        if (target is null)
            return new SlideResult(false, "找不到放映窗口：请确认幻灯片已进入放映状态。");

        // Steals focus briefly — the documented cost of this fallback.
        Win32.SetForegroundWindow(new IntPtr(target.Handle));
        Win32.SendKey(virtualKey);
        return new SlideResult(true, $"已发送{what}。");
    }

    /// <summary>
    /// Matches by configured class name and/or title regex. Both blank means unconfigured, which is
    /// reported as "no presentation window" rather than guessing — a wrong guess would send arrow
    /// keys into an arbitrary application.
    /// </summary>
    internal WindowInfo? FindTargetWindow()
    {
        var settings = _config.Settings.Slides;
        return MatchWindow(EnumerateWindows(), settings.WindowClass, settings.WindowTitleRegex);
    }

    internal static WindowInfo? MatchWindow(
        IEnumerable<WindowInfo> windows, string? windowClass, string? titleRegex)
    {
        var hasClass = !string.IsNullOrWhiteSpace(windowClass);
        var hasTitle = !string.IsNullOrWhiteSpace(titleRegex);
        if (!hasClass && !hasTitle) return null;

        Regex? regex = null;
        if (hasTitle)
        {
            try { regex = new Regex(titleRegex!, RegexOptions.IgnoreCase); }
            catch (ArgumentException) { return null; }
        }

        return windows.FirstOrDefault(w =>
            w.Visible
            && (!hasClass || string.Equals(w.ClassName, windowClass, StringComparison.OrdinalIgnoreCase))
            && (regex is null || regex.IsMatch(w.Title)));
    }
}
