using LiveControlPanel.Config;
using LiveControlPanel.Core;
using Xunit;

namespace LiveControlPanel.Tests;

/// <summary>
/// The acceptance table from the development plan (M1.3). These dates are real: in 2026, 8/3 is a
/// Monday, 8/5 a Wednesday, 8/7 a Friday, 8/8 a Saturday and 8/9 a Sunday.
/// </summary>
public class ScheduleMatcherTests
{
    private static List<ServiceTemplate> Templates() =>
        Seed.Templates().Where(t => t.Weekdays.Count > 0 && t.StartTime is not null).ToList();

    /// <summary>The shipped defaults: an operator may arrive an hour early or run two hours late.</summary>
    private static MatchWindowSettings Window() => new() { BeforeMinutes = 60, AfterMinutes = 120 };

    private static ServiceMatch? Match(DateTime now) =>
        ScheduleMatcher.MatchToday(Templates(), now, Window());

    // ---------------------------------------------------------------- the plan's table

    [Fact]
    public void Wednesday_0420_matches_morning_service()
    {
        var match = Match(new DateTime(2026, 8, 5, 4, 20, 0));

        Assert.NotNull(match);
        Assert.Equal("morning-service", match!.Template.Id);
        Assert.Equal("8/5/2026 Morning Service", match.Title);
    }

    [Fact]
    public void Wednesday_1745_matches_wednesday_service()
    {
        var match = Match(new DateTime(2026, 8, 5, 17, 45, 0));

        Assert.NotNull(match);
        Assert.Equal("wednesday-service", match!.Template.Id);
        Assert.Equal("8/5/2026 Wednesday Service", match.Title);
    }

    [Fact]
    public void Wednesday_1400_matches_nothing_and_reports_the_evening_service_next()
    {
        var now = new DateTime(2026, 8, 5, 14, 0, 0);

        Assert.Null(Match(now));

        var next = ScheduleMatcher.NextService(Templates(), now);
        Assert.NotNull(next);
        Assert.Equal("wednesday-service", next!.Template.Id);
        Assert.Equal(new DateTime(2026, 8, 5, 18, 0, 0), next.ScheduledStart);
    }

    [Fact]
    public void Friday_1750_matches_friday_prayer_meeting()
    {
        var match = Match(new DateTime(2026, 8, 7, 17, 50, 0));

        Assert.NotNull(match);
        Assert.Equal("friday-prayer", match!.Template.Id);
        Assert.Equal("8/7/2026 Friday Prayer Meeting", match.Title);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 40)]
    [InlineData(10, 30)]
    [InlineData(18, 0)]
    [InlineData(23, 59)]
    public void Saturday_never_matches(int hour, int minute)
    {
        Assert.Null(Match(new DateTime(2026, 8, 8, hour, minute, 0)));
    }

    [Fact]
    public void Sunday_1015_matches_sunday_service_not_morning_service()
    {
        var match = Match(new DateTime(2026, 8, 9, 10, 15, 0));

        Assert.NotNull(match);
        Assert.Equal("sunday-service", match!.Template.Id);
        Assert.Equal("8/9/2026 Sunday Service", match.Title);
        Assert.DoesNotContain("Morning", match.Title);
    }

    [Fact]
    public void Monday_0400_matches_morning_service_with_unpadded_month_and_day()
    {
        var match = Match(new DateTime(2026, 8, 3, 4, 0, 0));

        Assert.NotNull(match);
        Assert.Equal("morning-service", match!.Template.Id);
        Assert.Equal("8/3/2026 Morning Service", match.Title);
    }

    // ---------------------------------------------------------------- window edges

    [Fact]
    public void Window_opens_exactly_60_minutes_before_the_start()
    {
        Assert.NotNull(Match(new DateTime(2026, 8, 5, 3, 40, 0)));
        Assert.Null(Match(new DateTime(2026, 8, 5, 3, 39, 0)));
    }

    [Fact]
    public void Window_closes_exactly_120_minutes_after_the_start()
    {
        Assert.NotNull(Match(new DateTime(2026, 8, 5, 6, 40, 0)));
        Assert.Null(Match(new DateTime(2026, 8, 5, 6, 41, 0)));
    }

    // ---------------------------------------------------------------- early / late operators
    //
    // Template start times are announced times, not what happens. These assert the tolerance an
    // operator actually gets.

    [Theory]
    [InlineData(3, 45, "morning-service")]   // 55 min early
    [InlineData(4, 30, "morning-service")]   // 10 min early
    [InlineData(4, 40, "morning-service")]   // on time
    [InlineData(5, 30, "morning-service")]   // 50 min late
    [InlineData(6, 30, "morning-service")]   // 110 min late — still recognised
    public void An_early_or_late_morning_operator_still_lands_on_the_right_service(
        int hour, int minute, string expectedId)
    {
        var match = Match(new DateTime(2026, 8, 5, hour, minute, 0));

        Assert.NotNull(match);
        Assert.Equal(expectedId, match!.Template.Id);
    }

    [Theory]
    [InlineData(17, 5, "wednesday-service")]    // 55 min early
    [InlineData(18, 0, "wednesday-service")]    // on time
    [InlineData(19, 30, "wednesday-service")]   // 90 min late
    [InlineData(19, 59, "wednesday-service")]   // 119 min late
    public void An_early_or_late_evening_operator_still_lands_on_the_right_service(
        int hour, int minute, string expectedId)
    {
        var match = Match(new DateTime(2026, 8, 5, hour, minute, 0));

        Assert.NotNull(match);
        Assert.Equal(expectedId, match!.Template.Id);
    }

    /// <summary>
    /// The property that makes the wider window safe: on the two-service days the morning window
    /// closes long before the evening window opens, so lateness never resolves to the wrong service.
    /// </summary>
    [Theory]
    [InlineData(3)]   // Wednesday
    [InlineData(5)]   // Friday
    public void Morning_and_evening_windows_never_overlap_on_a_two_service_day(int weekday)
    {
        var date = new DateTime(2026, 8, 3).AddDays(weekday - 1);   // 8/3/2026 is a Monday
        Assert.Equal(weekday, (int)date.DayOfWeek);

        var window = Window();

        // Walk the whole day a minute at a time; every minute matches at most one service, and the
        // gap between them resolves to nothing rather than to a guess.
        var morningLatest = date.AddHours(4).AddMinutes(40).AddMinutes(window.AfterMinutes);
        var eveningEarliest = date.AddHours(18).AddMinutes(-window.BeforeMinutes);

        Assert.True(morningLatest < eveningEarliest,
            $"morning window closes {morningLatest:HH:mm} but evening opens {eveningEarliest:HH:mm}");

        for (var minute = 0; minute < 24 * 60; minute++)
        {
            var now = date.AddMinutes(minute);
            var match = ScheduleMatcher.MatchToday(Templates(), now, window);
            if (match is null) continue;

            var expected = now < date.AddHours(12) ? "morning-service" : null;
            if (expected is not null) Assert.Equal(expected, match.Template.Id);
            else Assert.Contains(match.Template.Id, new[] { "wednesday-service", "friday-prayer" });
        }
    }

    [Fact]
    public void Beyond_the_window_the_panel_reports_no_schedule_rather_than_guessing()
    {
        // Three hours late is outside the window by design; the operator uses the manual picker.
        Assert.Null(Match(new DateTime(2026, 8, 5, 7, 40, 0)));
    }

    [Fact]
    public void Overlapping_windows_resolve_to_the_nearest_start()
    {
        // The shipped schedule never overlaps; the tie-break is still required behaviour, so it is
        // exercised with deliberately overlapping templates.
        var templates = new List<ServiceTemplate>
        {
            new() { Id = "early", Name = "Early", Weekdays = new List<int> { 3 }, StartTime = "10:00" },
            new() { Id = "late", Name = "Late", Weekdays = new List<int> { 3 }, StartTime = "11:00" },
        };

        var match = ScheduleMatcher.MatchToday(templates, new DateTime(2026, 8, 5, 10, 50, 0), Window());

        Assert.NotNull(match);
        Assert.Equal("late", match!.Template.Id);
    }

    [Fact]
    public void Custom_template_never_matches_automatically()
    {
        var all = Seed.Templates();
        var custom = all.Single(t => t.Id == Seed.CustomTemplateId);

        Assert.Empty(custom.Weekdays);
        Assert.Null(custom.StartTime);

        // Every minute of a whole week resolves to something other than "custom".
        var start = new DateTime(2026, 8, 3, 0, 0, 0);
        for (var minutes = 0; minutes < 7 * 24 * 60; minutes += 5)
        {
            var match = ScheduleMatcher.MatchToday(all, start.AddMinutes(minutes), Window());
            Assert.NotEqual(Seed.CustomTemplateId, match?.Template.Id);
        }
    }

    // ---------------------------------------------------------------- next service

    [Fact]
    public void Next_service_crosses_midnight_into_the_following_day()
    {
        // Saturday evening: the next thing on the calendar is Sunday morning.
        var next = ScheduleMatcher.NextService(Templates(), new DateTime(2026, 8, 8, 20, 0, 0));

        Assert.NotNull(next);
        Assert.Equal("sunday-service", next!.Template.Id);
        Assert.Equal(new DateTime(2026, 8, 9, 10, 30, 0), next.ScheduledStart);
    }

    [Fact]
    public void Next_service_after_sunday_service_is_monday_morning()
    {
        var next = ScheduleMatcher.NextService(Templates(), new DateTime(2026, 8, 9, 12, 0, 0));

        Assert.NotNull(next);
        Assert.Equal("morning-service", next!.Template.Id);
        Assert.Equal(new DateTime(2026, 8, 10, 4, 40, 0), next.ScheduledStart);
    }

    [Fact]
    public void Next_service_ignores_an_occurrence_that_has_already_started()
    {
        // 04:41 on a Monday: today's 04:40 has begun, so "next" is Tuesday.
        var next = ScheduleMatcher.NextService(Templates(), new DateTime(2026, 8, 3, 4, 41, 0));

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 8, 4, 4, 40, 0), next!.ScheduledStart);
    }

    [Fact]
    public void Next_service_returns_null_when_no_template_is_schedulable()
    {
        Assert.Null(ScheduleMatcher.NextService(new List<ServiceTemplate>(), new DateTime(2026, 8, 5, 9, 0, 0)));
    }

    // ---------------------------------------------------------------- title format

    [Theory]
    [InlineData(2026, 8, 3, "8/3/2026 Morning Service")]
    [InlineData(2026, 8, 5, "8/5/2026 Morning Service")]
    [InlineData(2026, 12, 25, "12/25/2026 Morning Service")]
    [InlineData(2026, 1, 1, "1/1/2026 Morning Service")]
    public void Title_never_zero_pads_month_or_day(int year, int month, int day, string expected)
    {
        var actual = ScheduleMatcher.FormatTitle(
            "{M}/{D}/{YYYY} {name}", "Morning Service", new DateTime(year, month, day));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Title_falls_back_to_the_name_when_no_format_is_configured()
    {
        Assert.Equal("Morning Service",
            ScheduleMatcher.FormatTitle("", "Morning Service", new DateTime(2026, 8, 5)));
        Assert.Equal("Morning Service",
            ScheduleMatcher.FormatTitle(null, "Morning Service", new DateTime(2026, 8, 5)));
    }

    // ---------------------------------------------------------------- start time parsing

    [Theory]
    [InlineData("04:40", 4, 40)]
    [InlineData("4:40", 4, 40)]
    [InlineData("18:00", 18, 0)]
    [InlineData("10:30", 10, 30)]
    [InlineData(" 18:00 ", 18, 0)]
    public void Start_times_parse(string value, int hour, int minute)
    {
        Assert.True(ScheduleMatcher.TryParseStartTime(value, out var parsed));
        Assert.Equal(new TimeSpan(hour, minute, 0), parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("25:00")]
    [InlineData("18")]
    [InlineData("六点")]
    public void Invalid_start_times_are_rejected(string? value)
    {
        Assert.False(ScheduleMatcher.TryParseStartTime(value, out _));
    }
}
