using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LiveControlPanel.Slides;

public sealed record SlidePosition(int Current, int Total);

/// <summary>
/// Optional COM enhancement (FR 5.3). Gives the current/total slide number and absolute jumps when
/// WPS (or PowerPoint) exposes an automation object.
///
/// Explicitly *not* on the critical path: every method returns null on any failure, and directed
/// keystrokes must keep working when COM is unavailable.
///
/// <c>Marshal.GetActiveObject</c> was removed in .NET Core, so <c>GetActiveObject</c> is imported
/// from oleaut32 directly.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WpsCom
{
    private static readonly string[] ProgIds = { "KWPP.Application", "WPS.Application", "PowerPoint.Application" };

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object obj);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    public static SlidePosition? TryGetPosition()
    {
        var view = TryGetSlideShowView();
        if (view is null) return null;

        try
        {
            var current = (int?)Get(view, "CurrentShowPosition");
            var presentation = Get(view, "Presentation");
            var slides = presentation is null ? null : Get(presentation, "Slides");
            var total = slides is null ? null : (int?)Get(slides, "Count");

            return current is null || total is null ? null : new SlidePosition(current.Value, total.Value);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Release(view);
        }
    }

    public static bool TryGoto(int slideNumber)
    {
        var view = TryGetSlideShowView();
        if (view is null) return false;

        try
        {
            Invoke(view, "GotoSlide", slideNumber);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            Release(view);
        }
    }

    /// <summary>The active slide-show view, or null when nothing is presenting.</summary>
    private static object? TryGetSlideShowView()
    {
        foreach (var progId in ProgIds)
        {
            object? app = null;
            try
            {
                if (CLSIDFromProgID(progId, out var clsid) != 0) continue;
                if (GetActiveObject(ref clsid, IntPtr.Zero, out app) != 0 || app is null) continue;

                var windows = Get(app, "SlideShowWindows");
                if (windows is null) continue;

                var count = (int?)Get(windows, "Count") ?? 0;
                if (count < 1) continue;

                var window = Invoke(windows, "Item", 1);
                if (window is null) continue;

                return Get(window, "View");
            }
            catch (Exception)
            {
                // Not running, not registered, or a different automation model. Try the next ProgID.
            }
            finally
            {
                if (app is not null) Release(app);
            }
        }

        return null;
    }

    private static object? Get(object target, string name) =>
        target.GetType().InvokeMember(name,
            System.Reflection.BindingFlags.GetProperty, null, target, null);

    private static object? Invoke(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name,
            System.Reflection.BindingFlags.InvokeMethod, null, target, args);

    private static void Release(object com)
    {
        try
        {
            if (Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
        }
        catch (Exception) { /* nothing useful to do while tearing down an optional feature */ }
    }
}
