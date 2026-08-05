using LiveControlPanel.Config;
using LiveControlPanel.Slides;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiveControlPanel.Tests;

/// <summary>
/// Slide control is off until switched on. It is the only feature that reaches into another
/// application — synthesizing keystrokes at a window, attaching to a COM automation object — and
/// which of those work is machine-specific, so the default must be to touch nothing.
///
/// These use the real <see cref="SlideController"/>. With the feature disabled it must not call into
/// Win32 or COM at all, which is exactly what makes it safe to assert on a machine with no
/// presentation program running.
/// </summary>
public class SlideControlTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lcp-slides-tests", Guid.NewGuid().ToString("N"));

    private SlideController Controller(bool enabled)
    {
        var config = new ConfigStore(new AppPaths(_root));
        config.UpdateSettings(s => s.Slides.Enabled = enabled);
        return new SlideController(config, NullLogger<SlideController>.Instance);
    }

    // ---------------------------------------------------------------- default

    [Fact]
    public void Slide_control_is_disabled_by_default()
    {
        var config = new ConfigStore(new AppPaths(_root));

        Assert.False(config.Settings.Slides.Enabled);
    }

    [Fact]
    public void A_freshly_seeded_settings_file_records_slides_as_disabled()
    {
        var config = new ConfigStore(new AppPaths(_root));
        var json = File.ReadAllText(config.Paths.SettingsFile);

        Assert.Contains("\"enabled\": false", json);
    }

    // ---------------------------------------------------------------- disabled behaviour

    [Fact]
    public void Disabled_state_reports_neither_enabled_nor_available()
    {
        var state = Controller(enabled: false).GetState();

        Assert.False(state.Enabled);
        Assert.False(state.Available);
        Assert.Null(state.Current);
        Assert.Null(state.Total);
    }

    [Fact]
    public void Disabled_paging_explains_how_to_switch_it_on()
    {
        var controller = Controller(enabled: false);

        foreach (var result in new[] { controller.Next(), controller.Previous(), controller.Goto(3) })
        {
            Assert.False(result.Ok);
            Assert.Contains("没有启用", result.Message);
            Assert.Contains("设置页", result.Message);
        }
    }

    [Fact]
    public void Disabled_preview_returns_nothing()
    {
        Assert.Null(Controller(enabled: false).TryGetPreview(null));
        Assert.Null(Controller(enabled: false).TryGetPreview(5));
    }

    /// <summary>
    /// The two setup endpoints must keep working while the feature is off — they are how an operator
    /// decides whether to turn it on.
    /// </summary>
    [Fact]
    public void Diagnostics_still_work_while_disabled_and_report_the_disabled_state()
    {
        var diagnostics = Controller(enabled: false).Diagnose();

        Assert.False(diagnostics.Enabled);
        Assert.NotEqual(0, diagnostics.SessionId);          // a test host is never session 0
        Assert.False(diagnostics.SessionIsolated);
        Assert.False(string.IsNullOrWhiteSpace(diagnostics.Strategy));
    }

    [Fact]
    public void Window_enumeration_still_works_while_disabled()
    {
        // Needed to fill in the window class before enabling.
        Assert.NotEmpty(Controller(enabled: false).EnumerateWindows());
    }

    // ---------------------------------------------------------------- enabled, nothing presenting

    [Fact]
    public void Enabled_with_nothing_presenting_reports_unavailable_rather_than_failing()
    {
        var state = Controller(enabled: true).GetState();

        Assert.True(state.Enabled);
        // No slide show is running in a test run, so there is nothing to control.
        Assert.False(state.Available);
    }

    [Fact]
    public void Enabled_paging_with_no_presentation_gives_an_actionable_message()
    {
        var result = Controller(enabled: true).Next();

        Assert.False(result.Ok);
        Assert.Contains("放映", result.Message);
        Assert.DoesNotContain("没有启用", result.Message);
    }

    [Fact]
    public void Enabled_goto_rejects_a_zero_or_negative_page()
    {
        var controller = Controller(enabled: true);

        Assert.False(controller.Goto(0).Ok);
        Assert.Contains("大于 0", controller.Goto(0).Message);
        Assert.False(controller.Goto(-1).Ok);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* temp dir */ }
    }
}

/// <summary>The settings PIN is a fixed, documented default rather than a random number (FR 6.5).</summary>
public class SettingsPinTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lcp-pin-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_default_pin_is_fixed_and_documented()
    {
        Assert.Equal("0000", Seed.DefaultSettingsPin);
        Assert.Equal(Seed.DefaultSettingsPin, Seed.SettingsPin());
    }

    [Fact]
    public void Two_fresh_installations_get_the_same_pin_but_different_access_codes()
    {
        var a = new ConfigStore(new AppPaths(Path.Combine(_root, "a")));
        var b = new ConfigStore(new AppPaths(Path.Combine(_root, "b")));

        // Predictable PIN: nobody should have to read a JSON file to open the settings page.
        Assert.Equal(a.Settings.SettingsPin, b.Settings.SettingsPin);

        // The access code is the LAN gate and stays per-installation.
        Assert.NotEqual(a.Settings.AccessCode, b.Settings.AccessCode);
    }

    [Fact]
    public void A_changed_pin_survives_a_restart_and_is_not_reset_to_the_default()
    {
        var paths = new AppPaths(_root);
        new ConfigStore(paths).UpdateSettings(s => s.SettingsPin = "913245");

        var reopened = new ConfigStore(paths);

        Assert.Equal("913245", reopened.Settings.SettingsPin);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* temp dir */ }
    }
}
