using System.Globalization;
using LiveControlPanel.Config;

namespace LiveControlPanel.Core;

public sealed record ServiceMatch(ServiceTemplate Template, DateTime ScheduledStart, string Title);

/// <summary>
/// FR 4.1. Pure logic — no clock, no I/O — so the acceptance table in the development plan
/// (M1.3) can be asserted directly.
/// </summary>
public static class ScheduleMatcher
{
    /// <summary>
    /// The service whose window contains <paramref name="now"/>, or null. When several windows
    /// overlap the nearest start wins; with the shipped 04:40/18:00 schedule and the default
    /// -60/+120 window they never do, but the rule is required behaviour, not an accident of data.
    /// </summary>
    public static ServiceMatch? MatchToday(
        IEnumerable<ServiceTemplate> templates, DateTime now, MatchWindowSettings window)
    {
        ServiceMatch? best = null;
        var bestDistance = TimeSpan.MaxValue;

        foreach (var candidate in Occurrences(templates, now.Date))
        {
            var from = candidate.ScheduledStart.AddMinutes(-window.BeforeMinutes);
            var to = candidate.ScheduledStart.AddMinutes(window.AfterMinutes);
            if (now < from || now > to) continue;

            var distance = (now - candidate.ScheduledStart).Duration();
            if (distance >= bestDistance) continue;

            best = candidate;
            bestDistance = distance;
        }

        return best;
    }

    /// <summary>
    /// The next service starting strictly after <paramref name="now"/>, searching forward across
    /// days. FR 4.1 step 5: shown when nothing matches, so the operator learns they came early.
    /// </summary>
    public static ServiceMatch? NextService(
        IEnumerable<ServiceTemplate> templates, DateTime now, int searchDays = 14)
    {
        var list = templates as ICollection<ServiceTemplate> ?? templates.ToList();

        for (var offset = 0; offset <= searchDays; offset++)
        {
            var day = now.Date.AddDays(offset);
            var soonest = Occurrences(list, day)
                .Where(o => o.ScheduledStart > now)
                .OrderBy(o => o.ScheduledStart)
                .FirstOrDefault();
            if (soonest is not null) return soonest;
        }

        return null;
    }

    /// <summary>All occurrences falling on <paramref name="date"/>, one per matching weekday entry.</summary>
    private static IEnumerable<ServiceMatch> Occurrences(IEnumerable<ServiceTemplate> templates, DateTime date)
    {
        var weekday = (int)date.DayOfWeek;

        foreach (var template in templates)
        {
            if (!template.Weekdays.Contains(weekday)) continue;
            if (!TryParseStartTime(template.StartTime, out var startTime)) continue;

            var start = date.Date.Add(startTime);
            yield return new ServiceMatch(template, start, FormatTitle(template, start));
        }
    }

    public static bool TryParseStartTime(string? value, out TimeSpan result)
    {
        result = default;
        return !string.IsNullOrWhiteSpace(value)
            && TimeSpan.TryParseExact(value.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out result);
    }

    public static string FormatTitle(ServiceTemplate template, DateTime date) =>
        FormatTitle(template.TitleFormat, template.Name, date);

    /// <summary>
    /// FR 4.1. US-style date with no zero padding: "8/3/2026 Morning Service", never "08/03/2026".
    /// </summary>
    public static string FormatTitle(string? titleFormat, string name, DateTime date)
    {
        if (string.IsNullOrWhiteSpace(titleFormat)) return name ?? "";

        return titleFormat
            .Replace("{YYYY}", date.Year.ToString("D4", CultureInfo.InvariantCulture))
            .Replace("{M}", date.Month.ToString(CultureInfo.InvariantCulture))
            .Replace("{D}", date.Day.ToString(CultureInfo.InvariantCulture))
            .Replace("{name}", name ?? "");
    }
}
