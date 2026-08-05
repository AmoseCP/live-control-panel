using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LiveControlPanel.Slides;

public sealed record SlidePosition(int Current, int Total);

/// <summary>Everything the COM probe could learn, for the deployment-time diagnostic.</summary>
public sealed record ComReport(
    string? ProgId,
    bool SlideShowRunning,
    int? Current,
    int? Total,
    bool ExportSupported,
    string? Error,
    string? Culture);

/// <summary>
/// Optional COM enhancement (FR 5.3). Gives the current/total slide number, absolute jumps, and the
/// rendered image a next-slide preview needs.
///
/// Explicitly *not* on the critical path: every method returns null/false on any failure, and
/// directed keystrokes must keep working when COM is unavailable. WPS only claims PowerPoint
/// compatibility, so which of these members actually exist there is a deployment-time question —
/// hence <see cref="Diagnose"/>.
///
/// <c>Marshal.GetActiveObject</c> was removed in .NET Core, so <c>GetActiveObject</c> is imported
/// from oleaut32 directly.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WpsCom
{
    private static readonly string[] ProgIds = { "KWPP.Application", "WPS.Application", "PowerPoint.Application" };

    /// <summary>
    /// The out-parameter must be marshalled as <see cref="UnmanagedType.IDispatch"/>, not IUnknown.
    /// With IUnknown, .NET 8 produces an RCW whose IDispatch is not wired up for late binding, and
    /// every member access fails with DISP_E_UNKNOWNNAME even though the object is the right one —
    /// .NET Framework was more forgiving here, so the same code pattern works there. Verified
    /// against a live PowerPoint slide show: IUnknown fails, IDispatch returns the real values.
    /// </summary>
    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved,
        [MarshalAs(UnmanagedType.IDispatch)] out object obj);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    public static SlidePosition? TryGetPosition()
    {
        var session = TryAttach();
        if (session is null) return null;

        try { return ReadPosition(session); }
        catch (Exception) { return null; }
        finally { session.Dispose(); }
    }

    public static bool TryGoto(int slideNumber) => TryNavigate("GotoSlide", slideNumber);

    /// <summary>
    /// Advances the show through the automation interface. Preferred over synthesized keystrokes:
    /// measured on PowerPoint 16, posted key messages are ignored outright and SendInput needs a
    /// foreground window a background process is not allowed to steal — while View.Next moves the
    /// show every time and needs no focus at all.
    /// </summary>
    public static bool TryNext() => TryNavigate("Next");

    public static bool TryPrevious() => TryNavigate("Previous");

    private static bool TryNavigate(string member, params object[] args)
    {
        var session = TryAttach();
        if (session is null) return false;

        try
        {
            Invoke(session.View, member, args);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>
    /// Renders one slide to <paramref name="path"/>. Returns the rendered size, or null when the
    /// presentation program does not support export — WPS may not.
    /// </summary>
    public static (int Width, int Height)? TryExportSlide(int slideNumber, string path, int width)
    {
        var session = TryAttach();
        if (session is null) return null;

        try
        {
            var slides = Get(session.Presentation, "Slides");
            if (slides is null) return null;

            var count = (int?)Get(slides, "Count") ?? 0;
            if (slideNumber < 1 || slideNumber > count) return null;

            // Preserve the deck's aspect ratio; 4:3 decks must not be squashed into 16:9.
            var height = (int)Math.Round(width * AspectRatio(session.Presentation));

            var slide = Invoke(slides, "Item", slideNumber);
            if (slide is null) return null;

            Invoke(slide, "Export", path, "PNG", width, height);
            return File.Exists(path) ? (width, height) : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>
    /// Tries each late-binding strategy against the running program and reports which ones work.
    /// Kept because the answer differs between .NET Framework and .NET 8, and between PowerPoint and
    /// WPS — so it has to be measured on the target machine rather than assumed.
    /// </summary>
    public static string ProbeStrategies()
    {
        var results = new List<string>();

        if (CLSIDFromProgID("PowerPoint.Application", out var clsid) != 0
            && CLSIDFromProgID("KWPP.Application", out clsid) != 0
            && CLSIDFromProgID("WPS.Application", out clsid) != 0)
        {
            return "没有任何演示程序的 ProgID 已注册。";
        }

        results.Add("apartment=" + Thread.CurrentThread.GetApartmentState());

        // Walk the exact chain TryAttach depends on, naming the step that breaks. A probe that stops
        // at SlideShowWindows.Count proves nothing about Item(1) or Export.
        object? app = null;
        try
        {
            var hr = GetActiveObject(ref clsid, IntPtr.Zero, out app);
            results.Add($"GetActiveObject -> 0x{hr:X8}");
            if (hr != 0 || app is null) return string.Join(" | ", results);

            var windows = Step(results, "Application.SlideShowWindows",
                () => Get(app, "SlideShowWindows"));
            if (windows is null) return string.Join(" | ", results);

            var count = Step(results, "SlideShowWindows.Count", () => Get(windows, "Count"));
            if (count is null) return string.Join(" | ", results);

            var window = Step(results, "SlideShowWindows.Item(1)",
                () => Invoke(windows, "Item", 1));
            if (window is null) return string.Join(" | ", results);

            var view = Step(results, "SlideShowWindow.View", () => Get(window, "View"));
            if (view is null) return string.Join(" | ", results);

            var pos = Step(results, "View.CurrentShowPosition", () => Get(view, "CurrentShowPosition"));

            var presentation = Step(results, "SlideShowWindow.Presentation",
                () => Get(window, "Presentation"));
            if (presentation is null) return string.Join(" | ", results);

            var slides = Step(results, "Presentation.Slides", () => Get(presentation, "Slides"));
            if (slides is null) return string.Join(" | ", results);

            var total = Step(results, "Slides.Count", () => Get(slides, "Count"));

            var target = pos is null ? 1 : Convert.ToInt32(pos);
            var slide = Step(results, $"Slides.Item({target})", () => Invoke(slides, "Item", target));
            if (slide is null) return string.Join(" | ", results);

            Step(results, "Slide.Export", () =>
            {
                var path = Path.Combine(Path.GetTempPath(), "lcp-com-probe.png");
                Invoke(slide, "Export", path, "PNG", 320, 180);
                return File.Exists(path) ? "wrote " + new FileInfo(path).Length + " bytes" : null;
            });
        }
        catch (Exception ex)
        {
            results.Add("unexpected: " + ex.GetBaseException().Message);
        }
        finally
        {
            if (app is not null) Release(app);
        }

        return string.Join(" | ", results);
    }

    private static object? Step(List<string> into, string label, Func<object?> action)
    {
        try
        {
            var value = action();
            into.Add($"{label} -> {(value is null ? "null" : "OK")}");
            return value;
        }
        catch (Exception ex)
        {
            into.Add($"{label} -> FAIL {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>Probes every optional capability and reports what is actually available.</summary>
    public static ComReport Diagnose(string probeFilePath)
    {
        ComSession? session = null;
        try
        {
            session = TryAttach();
            if (session is null)
                return new ComReport(null, false, null, null, false,
                    "没有找到正在放映的演示程序。逐个尝试的结果：" + AttachErrorSummary(), CultureTag());

            var position = ReadPosition(session);

            var exported = false;
            string? error = null;
            try
            {
                var target = position?.Current ?? 1;
                exported = TryExportViaSession(session, target, probeFilePath, 320) is not null;
                if (!exported) error = "已连上 COM，但导出幻灯片图像失败（该程序可能不支持 Slides.Export）。";
            }
            catch (Exception ex)
            {
                error = "导出幻灯片图像时出错：" + ex.Message;
            }

            return new ComReport(session.ProgId, true, position?.Current, position?.Total, exported, error, CultureTag());
        }
        catch (Exception ex)
        {
            return new ComReport(null, false, null, null, false, ex.Message, CultureTag());
        }
        finally
        {
            session?.Dispose();
        }
    }

    // ---------------------------------------------------------------- internals

    /// <summary>
    /// Why attaching failed, per ProgID. Kept because a silent null here is undiagnosable: the COM
    /// layer has many independent ways to be unavailable and they need different fixes.
    /// </summary>
    private static readonly List<string> LastAttachErrors = new();

    private static SlidePosition? ReadPosition(ComSession session)
    {
        var current = (int?)Get(session.View, "CurrentShowPosition");
        var slides = Get(session.Presentation, "Slides");
        var total = slides is null ? null : (int?)Get(slides, "Count");

        return current is null || total is null ? null : new SlidePosition(current.Value, total.Value);
    }

    private static (int Width, int Height)? TryExportViaSession(
        ComSession session, int slideNumber, string path, int width)
    {
        var slides = Get(session.Presentation, "Slides");
        if (slides is null) return null;

        var slide = Invoke(slides, "Item", slideNumber);
        if (slide is null) return null;

        var height = (int)Math.Round(width * AspectRatio(session.Presentation));
        Invoke(slide, "Export", path, "PNG", width, height);
        return File.Exists(path) ? (width, height) : null;
    }

    private static double AspectRatio(object presentation)
    {
        try
        {
            var setup = Get(presentation, "PageSetup");
            var w = Convert.ToDouble(Get(setup!, "SlideWidth"));
            var h = Convert.ToDouble(Get(setup!, "SlideHeight"));
            if (w > 0 && h > 0) return h / w;
        }
        catch (Exception)
        {
            // Fall through to 16:9, the common case for a projected sermon deck.
        }
        return 9.0 / 16.0;
    }

    /// <summary>
    /// The live slide-show view plus its presentation, or null when nothing is presenting.
    ///
    /// Note this reaches the *running* instance through the ROT, which is per-session: a process in
    /// session 0 (a Windows service) cannot see an application on the interactive desktop.
    /// </summary>
    private static ComSession? TryAttach()
    {
        lock (LastAttachErrors) LastAttachErrors.Clear();

        foreach (var progId in ProgIds)
        {
            object? app = null;
            try
            {
                var hr = CLSIDFromProgID(progId, out var clsid);
                if (hr != 0) { Note(progId, $"未注册 (CLSIDFromProgID 0x{hr:X8})"); continue; }

                hr = GetActiveObject(ref clsid, IntPtr.Zero, out app);
                if (hr != 0 || app is null) { Note(progId, $"未在运行 (GetActiveObject 0x{hr:X8})"); continue; }

                var windows = Get(app, "SlideShowWindows");
                if (windows is null) { Note(progId, "读不到 SlideShowWindows"); continue; }

                var count = Convert.ToInt32(Get(windows, "Count") ?? 0);
                if (count < 1) { Note(progId, "已连上，但当前没有在放映"); continue; }

                var window = Invoke(windows, "Item", 1);
                if (window is null) { Note(progId, "读不到 SlideShowWindows.Item(1)"); continue; }

                var view = Get(window, "View");
                if (view is null) { Note(progId, "读不到 SlideShowWindow.View"); continue; }

                // Presentation hangs off the *window*, not the view. Reading it from the view fails
                // with DISP_E_UNKNOWNNAME on PowerPoint 16 — measured, whatever the docs imply.
                var presentation = Get(window, "Presentation");
                if (presentation is null) { Note(progId, "读不到 SlideShowWindow.Presentation"); continue; }

                var session = new ComSession(progId, app, window, view, presentation);
                app = null;   // ownership moved to the session
                return session;
            }
            catch (Exception ex)
            {
                Note(progId, ex.GetBaseException().Message);
            }
            finally
            {
                if (app is not null) Release(app);
            }
        }

        return null;
    }

    /// <summary>What LCID late-bound COM calls are actually using in this process.</summary>
    private static string CultureTag()
    {
        var c = System.Globalization.CultureInfo.CurrentCulture;
        return $"{c.Name} (LCID {c.LCID}), invariant={System.Globalization.CultureInfo.InvariantCulture.Equals(c)}";
    }

    private static void Note(string progId, string reason)
    {
        lock (LastAttachErrors) LastAttachErrors.Add($"{progId}: {reason}");
    }

    private static string AttachErrorSummary()
    {
        lock (LastAttachErrors)
            return LastAttachErrors.Count == 0 ? "" : string.Join("；", LastAttachErrors);
    }

    private sealed class ComSession : IDisposable
    {
        public ComSession(string progId, object app, object window, object view, object presentation)
        {
            ProgId = progId;
            App = app;
            Window = window;
            View = view;
            Presentation = presentation;
        }

        public string ProgId { get; }
        public object App { get; }
        public object Window { get; }
        public object View { get; }
        public object Presentation { get; }

        public void Dispose()
        {
            Release(Presentation);
            Release(View);
            Release(Window);
            Release(App);
        }
    }

    private static object? Get(object target, string name) =>
        InvokeCore(target, name, System.Reflection.BindingFlags.GetProperty, null);

    private static object? Invoke(object target, string name, params object[] args) =>
        InvokeCore(target, name, System.Reflection.BindingFlags.InvokeMethod, args);

    /// <summary>
    /// Late-bound access. The default culture is fine — the earlier DISP_E_UNKNOWNNAME failures came
    /// from the IUnknown marshalling above, not from the LCID.
    /// </summary>
    private static object? InvokeCore(
        object target, string name, System.Reflection.BindingFlags flags, object?[]? args)
    {
        try
        {
            return target.GetType().InvokeMember(name, flags, null, target, args);
        }
        catch (MissingMemberException)
        {
            // WPS is only PowerPoint-compatible, not identical: a member may be exposed as a method
            // where PowerPoint has a property, or vice versa. Try the other shape before giving up.
            var alternate = flags == System.Reflection.BindingFlags.GetProperty
                ? System.Reflection.BindingFlags.InvokeMethod
                : System.Reflection.BindingFlags.GetProperty;

            return target.GetType().InvokeMember(name, alternate, null, target, args);
        }
    }

    private static void Release(object com)
    {
        try
        {
            if (Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
        }
        catch (Exception) { /* nothing useful to do while tearing down an optional feature */ }
    }
}
