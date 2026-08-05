using LiveControlPanel.Config;
using LiveControlPanel.Core;
using LiveControlPanel.Slides;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveControlPanel.Tests;

/// <summary>
/// Wires the real <see cref="ConfigStore"/>, <see cref="StateManager"/> and
/// <see cref="Orchestrator"/> against fakes and a throwaway data directory, so tests exercise the
/// production code paths rather than a parallel implementation.
/// </summary>
public sealed class TestHost : IDisposable
{
    public TestHost()
    {
        Root = Path.Combine(Path.GetTempPath(), "lcp-tests", Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(Root);
        Config = new ConfigStore(Paths);
        Config.UpdateSettings(s =>
        {
            s.StreamId = "stream-1";
            // Off by default: most tests do not care, and a missing file would only add noise.
            s.DefaultThumbnail = "";
        });

        Hub = new StateHub(NullLogger<StateHub>.Instance);
        Slides = new StubSlideController();
        State = new StateManager(Config, Hub, Slides, NullLogger<StateManager>.Instance);
        YouTube = new FakeYouTubeClient();
        Obs = new FakeObsClient();
        Telegram = new FakeTelegramClient();

        Orchestrator = new Orchestrator(Config, State, YouTube, Obs, NullLogger<Orchestrator>.Instance);
        Notifications = new NotificationService(Config, State, Telegram);
        Preflight = new Preflight(Config, Obs, YouTube, NullLogger<Preflight>.Instance);
    }

    public string Root { get; }
    public AppPaths Paths { get; }
    public ConfigStore Config { get; }
    public StateHub Hub { get; }
    public StubSlideController Slides { get; }
    public StateManager State { get; }
    public FakeYouTubeClient YouTube { get; }
    public FakeObsClient Obs { get; }
    public FakeTelegramClient Telegram { get; }
    public Orchestrator Orchestrator { get; }
    public NotificationService Notifications { get; }
    public Preflight Preflight { get; }

    /// <summary>
    /// Pins "today" so orchestration tests do not depend on the day they are run. Uses the same
    /// manual-choice path as the "不是这一场？" override, which is what makes it survive refreshes.
    /// </summary>
    public void SetToday(string title = "8/5/2026 Wednesday Service", string templateId = "wednesday-service") =>
        State.Mutate(s => s.Today = new TodayState
        {
            TemplateId = templateId,
            Title = title,
            ScheduledStart = new DateTime(2026, 8, 5, 18, 0, 0),
            Manual = true,
        });

    /// <summary>Writes a real file so the thumbnail step has something to upload.</summary>
    public void WithThumbnail(string relative = "thumbnails/default.jpg")
    {
        var path = Paths.Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
        Config.UpdateSettings(s => s.DefaultThumbnail = relative);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* a stray handle on a temp dir is not worth failing a test over */ }
    }
}

public sealed class StubSlideController : ISlideController
{
    public SlidesState State { get; set; } = new() { Available = true, Current = 7, Total = 24 };
    public List<string> Calls { get; } = new();
    public bool GotoSucceeds { get; set; } = true;

    /// <summary>Null models a presentation program with no way to render a slide — e.g. WPS without export.</summary>
    public SlidePreview? Preview { get; set; }

    public List<int?> PreviewRequests { get; } = new();

    public IReadOnlyList<WindowInfo> EnumerateWindows() => Array.Empty<WindowInfo>();

    public SlidesState GetState() => State;

    public SlidePreview? TryGetPreview(int? slideNumber)
    {
        PreviewRequests.Add(slideNumber);
        return Preview;
    }

    public SlideDiagnostics Diagnose() => new(
        Enabled: true,
        SessionId: 1,
        SessionIsolated: false,
        ComProgId: "PowerPoint.Application",
        SlideShowRunning: true,
        Current: State.Current,
        Total: State.Total,
        PreviewSupported: Preview is not null,
        TargetWindowFound: true,
        Strategy: "PostMessage",
        Message: null,
        Culture: "en-US (LCID 1033)");

    public string ProbeCom() => "stub";

    public SlideResult Next() { Calls.Add("next"); return new SlideResult(true, "已发送下一页。"); }

    public SlideResult Previous() { Calls.Add("prev"); return new SlideResult(true, "已发送上一页。"); }

    public SlideResult Goto(int slideNumber)
    {
        Calls.Add($"goto:{slideNumber}");
        return GotoSucceeds
            ? new SlideResult(true, $"已跳转到第 {slideNumber} 页。")
            : new SlideResult(false, "无法跳页。");
    }
}
