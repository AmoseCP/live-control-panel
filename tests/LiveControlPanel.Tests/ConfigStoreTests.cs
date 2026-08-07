using System.Text.Json;
using LiveControlPanel.Config;
using Xunit;

namespace LiveControlPanel.Tests;

/// <summary>
/// The plan's M1.2 acceptance criteria: deleting the data directory must rebuild exactly the four
/// scheduled templates, with Morning Service on Mon–Fri and Sunday Service on Sunday.
/// </summary>
public class ConfigStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lcp-config-tests", Guid.NewGuid().ToString("N"));

    private ConfigStore Open() => new(new AppPaths(_root));

    // ---------------------------------------------------------------- seeding

    [Fact]
    public void First_run_creates_the_data_directory_and_both_files()
    {
        var config = Open();

        Assert.True(Directory.Exists(config.Paths.Root));
        Assert.True(File.Exists(config.Paths.SettingsFile));
        Assert.True(File.Exists(config.Paths.TemplatesFile));
        Assert.True(Directory.Exists(config.Paths.ThumbnailsDirectory));
        Assert.True(Directory.Exists(config.Paths.LogDirectory));
    }

    [Fact]
    public void First_run_seeds_the_four_scheduled_services_plus_the_ad_hoc_one()
    {
        var config = Open();

        Assert.Equal(4, config.SchedulableTemplates().Count());
        Assert.Equal(5, config.Templates.Count);
        Assert.Contains(config.Templates, t => t.Id == Seed.CustomTemplateId);
    }

    /// <summary>
    /// The ad-hoc template carries a name and the standard format so an extra stream defaults to
    /// "<date> Service" without the operator typing a date, yet still never matches automatically.
    /// </summary>
    [Fact]
    public void The_ad_hoc_template_defaults_to_date_plus_service_but_never_auto_matches()
    {
        var custom = Open().FindTemplate(Seed.CustomTemplateId)!;

        Assert.Equal("Service", custom.Name);
        Assert.Equal("{M}/{D}/{YYYY} {name}", custom.TitleFormat);
        Assert.Empty(custom.Weekdays);
        Assert.Null(custom.StartTime);

        // Same formatting path as the scheduled services, so the month/day are never zero-padded.
        Assert.Equal("8/3/2026 Service",
            Core.ScheduleMatcher.FormatTitle(custom, new DateTime(2026, 8, 3)));
        Assert.Equal("12/25/2026 Service",
            Core.ScheduleMatcher.FormatTitle(custom, new DateTime(2026, 12, 25)));
    }

    [Theory]
    [InlineData("morning-service", "Morning Service", "04:40", new[] { 1, 2, 3, 4, 5 })]
    [InlineData("wednesday-service", "Wednesday Service", "18:00", new[] { 3 })]
    [InlineData("friday-prayer", "Friday Prayer Meeting", "18:00", new[] { 5 })]
    [InlineData("sunday-service", "Sunday Service", "10:30", new[] { 0 })]
    public void Seeded_templates_match_the_requirements_exactly(
        string id, string name, string startTime, int[] weekdays)
    {
        var template = Open().FindTemplate(id);

        Assert.NotNull(template);
        Assert.Equal(name, template!.Name);
        Assert.Equal(startTime, template.StartTime);
        Assert.Equal(weekdays, template.Weekdays.ToArray());
        Assert.Equal("unlisted", template.PrivacyStatus);
        Assert.False(template.MadeForKids);
        Assert.Equal("ultraLow", template.LatencyPreference);
        Assert.Equal("{M}/{D}/{YYYY} {name}", template.TitleFormat);
    }

    /// <summary>
    /// The mandated weekday table yields eight occurrences a week: five 04:40 mornings (Mon–Fri),
    /// Wednesday and Friday evenings, and Sunday morning. Note the requirements prose says "seven
    /// services a week" while its own table sums to eight; the table is what fixes the seed data, so
    /// that is what is asserted here. Saturday is empty either way.
    /// </summary>
    [Fact]
    public void The_seeded_week_matches_the_mandated_table_and_leaves_saturday_empty()
    {
        var config = Open();

        var occurrences = config.SchedulableTemplates().Sum(t => t.Weekdays.Count);
        Assert.Equal(8, occurrences);

        Assert.DoesNotContain(6, config.SchedulableTemplates().SelectMany(t => t.Weekdays));

        // Wednesday and Friday each carry two services — the case that makes the leftover-broadcast
        // pre-flight check matter.
        foreach (var twoServiceDay in new[] { 3, 5 })
            Assert.Equal(2, config.SchedulableTemplates().Count(t => t.Weekdays.Contains(twoServiceDay)));
    }

    [Fact]
    public void The_sunday_morning_service_is_not_called_morning_service()
    {
        var config = Open();

        var sunday = config.SchedulableTemplates().Single(t => t.Weekdays.Contains(0));
        Assert.Equal("Sunday Service", sunday.Name);
    }

    [Fact]
    public void Deleting_the_data_directory_rebuilds_the_seed()
    {
        var first = Open();
        Assert.Equal(5, first.Templates.Count);

        Directory.Delete(_root, recursive: true);

        var second = Open();
        Assert.True(second.TemplatesWereSeeded);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, second.FindTemplate("morning-service")!.Weekdays.ToArray());
        Assert.Equal(new[] { 0 }, second.FindTemplate("sunday-service")!.Weekdays.ToArray());
    }

    // ---------------------------------------------------------------- access code / pin

    [Fact]
    public void First_run_generates_an_access_code_and_a_settings_pin()
    {
        var config = Open();

        Assert.False(string.IsNullOrWhiteSpace(config.Settings.AccessCode));
        Assert.False(string.IsNullOrWhiteSpace(config.Settings.SettingsPin));
        Assert.InRange(config.Settings.SettingsPin.Length, 4, 6);
        Assert.All(config.Settings.SettingsPin, c => Assert.True(char.IsDigit(c)));
    }

    [Fact]
    public void The_access_code_survives_a_restart()
    {
        var code = Open().Settings.AccessCode;
        Assert.Equal(code, Open().Settings.AccessCode);
    }

    [Fact]
    public void Access_codes_are_random_per_installation()
    {
        var codes = Enumerable.Range(0, 20).Select(_ => Seed.AccessCode()).ToHashSet();
        Assert.True(codes.Count > 15, "access codes should not collide this often");
    }

    [Fact]
    public void Access_codes_exclude_glyphs_that_are_easy_to_mistype()
    {
        for (var i = 0; i < 200; i++)
            Assert.DoesNotContain(Seed.AccessCode(), c => c is 'l' or '1' or 'o' or '0' or 'I');
    }

    [Fact]
    public void A_settings_file_missing_the_access_code_gets_one_generated()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsFile, """{ "port": 5088, "accessCode": "", "settingsPin": "" }""");

        var config = new ConfigStore(paths);

        Assert.False(string.IsNullOrWhiteSpace(config.Settings.AccessCode));
        Assert.False(string.IsNullOrWhiteSpace(config.Settings.SettingsPin));
    }

    // ---------------------------------------------------------------- persistence

    [Fact]
    public void Settings_updates_are_persisted()
    {
        Open().UpdateSettings(s =>
        {
            s.StreamId = "abc-123";
            s.TelegramChatId = "-1001234567890";
        });

        var reopened = Open();
        Assert.Equal("abc-123", reopened.Settings.StreamId);
        Assert.Equal("-1001234567890", reopened.Settings.TelegramChatId);
    }

    [Fact]
    public void Settings_are_written_as_camel_case_json()
    {
        var config = Open();
        config.UpdateSettings(s => s.StreamId = "abc");

        var json = File.ReadAllText(config.Paths.SettingsFile);
        Assert.Contains("\"streamId\"", json);
        Assert.Contains("\"telegramBotToken\"", json);
        Assert.Contains("\"matchWindow\"", json);
        Assert.DoesNotContain("\"StreamId\"", json);
    }

    [Fact]
    public void A_corrupt_settings_file_falls_back_to_defaults_instead_of_failing_to_start()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsFile, "{ this is not json");

        var config = new ConfigStore(paths);

        Assert.Equal(5088, config.Settings.Port);
        Assert.False(string.IsNullOrWhiteSpace(config.Settings.AccessCode));
    }

    /// <summary>
    /// Reseeding after corruption regenerates the access code, which invalidates every iPad
    /// bookmark and the printed QR at once — the unreadable original must be preserved so an
    /// administrator can recover the old code and secrets instead of re-provisioning everything.
    /// </summary>
    [Fact]
    public void A_corrupt_settings_file_is_preserved_as_a_backup_before_reseeding()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsFile, "{ this is not json");

        _ = new ConfigStore(paths);

        var backup = Directory.GetFiles(Path.GetDirectoryName(paths.SettingsFile)!, "settings.json.bad-*").Single();
        Assert.Equal("{ this is not json", File.ReadAllText(backup));
    }

    [Fact]
    public void A_corrupt_templates_file_falls_back_to_the_seed()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        File.WriteAllText(paths.TemplatesFile, "not json at all");

        var config = new ConfigStore(paths);

        Assert.Equal(5, config.Templates.Count);
        Assert.NotNull(config.FindTemplate("morning-service"));
    }

    [Fact]
    public void Templates_can_be_replaced_and_reloaded()
    {
        var config = Open();
        config.SaveTemplates(new List<ServiceTemplate>
        {
            new() { Id = "only", Name = "Only Service", Weekdays = new List<int> { 2 }, StartTime = "09:00" },
        });

        var reopened = Open();
        Assert.Single(reopened.Templates);
        Assert.Equal("Only Service", reopened.Templates[0].Name);
    }

    [Fact]
    public void Template_lookup_is_case_insensitive()
    {
        var config = Open();

        Assert.NotNull(config.FindTemplate("MORNING-SERVICE"));
        Assert.Null(config.FindTemplate("no-such-service"));
    }

    // ---------------------------------------------------------------- paths

    [Fact]
    public void Relative_paths_resolve_against_the_data_root_not_the_working_directory()
    {
        var paths = new AppPaths(_root);

        var resolved = paths.Resolve("thumbnails/default.jpg");

        Assert.StartsWith(_root, resolved);
        Assert.EndsWith("default.jpg", resolved);
    }

    [Fact]
    public void Absolute_paths_are_left_alone()
    {
        var paths = new AppPaths(_root);
        var absolute = Path.Combine(Path.GetTempPath(), "elsewhere.jpg");

        Assert.Equal(absolute, paths.Resolve(absolute));
    }

    [Fact]
    public void The_default_data_root_is_under_program_data_not_the_user_profile()
    {
        // FR 2: the service account's view of the user profile is not reliable.
        var paths = new AppPaths();

        Assert.Contains(AppPaths.DirectoryName, paths.Root);
        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.GetDirectoryName(paths.Root));
    }

    // ---------------------------------------------------------------- round trip

    [Fact]
    public void Settings_round_trip_through_json_without_losing_fields()
    {
        var original = new AppSettings
        {
            Port = 6000,
            AccessCode = "abcd1234",
            SettingsPin = "9876",
            StreamId = "s1",
            Obs = new ObsSettings { Url = "ws://x:1", SceneCamera = "C", VideoSourceNames = new List<string> { "v" } },
            Slides = new SlidesSettings { WindowClass = "cls", Strategy = "SendInput" },
            MatchWindow = new MatchWindowSettings { BeforeMinutes = 30, AfterMinutes = 45 },
            YouTube = new YouTubeSettings { ClientId = "cid", ClientSecret = "sec" },
        };

        var json = JsonSerializer.Serialize(original, Json.Options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, Json.Options)!;

        Assert.Equal(6000, restored.Port);
        Assert.Equal("abcd1234", restored.AccessCode);
        Assert.Equal("ws://x:1", restored.Obs.Url);
        Assert.Equal(new[] { "v" }, restored.Obs.VideoSourceNames.ToArray());
        Assert.Equal("SendInput", restored.Slides.Strategy);
        Assert.Equal(30, restored.MatchWindow.BeforeMinutes);
        Assert.Equal("cid", restored.YouTube.ClientId);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* temp dir */ }
    }
}
