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
}

public sealed record SlideResult(bool Ok, string Message);

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
    private readonly ConfigStore _config;
    private readonly ILogger<SlideController> _log;

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

    public SlideResult Next() => SendKey(Win32.VK_RIGHT, "下一页");

    public SlideResult Previous() => SendKey(Win32.VK_LEFT, "上一页");

    public SlideResult Goto(int slideNumber)
    {
        if (slideNumber < 1) return new SlideResult(false, "页码必须大于 0。");

        // Absolute jumps need COM; there is no keystroke equivalent.
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
