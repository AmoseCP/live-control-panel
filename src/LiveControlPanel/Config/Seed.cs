namespace LiveControlPanel.Config;

/// <summary>
/// First-run seed data. FR 3.1 fixes these four templates exactly: five 04:40 morning services
/// Mon–Fri, Wednesday and Friday evening meetings, and the Sunday 10:30 service — which is named
/// "Sunday Service", *not* "Morning Service". Saturday has no schedule.
/// </summary>
public static class Seed
{
    public const string CustomTemplateId = "custom";

    public static List<ServiceTemplate> Templates() => new()
    {
        new ServiceTemplate
        {
            Id = "morning-service",
            Name = "Morning Service",
            Weekdays = new List<int> { 1, 2, 3, 4, 5 },
            StartTime = "04:40",
        },
        new ServiceTemplate
        {
            Id = "wednesday-service",
            Name = "Wednesday Service",
            Weekdays = new List<int> { 3 },
            StartTime = "18:00",
        },
        new ServiceTemplate
        {
            Id = "friday-prayer",
            Name = "Friday Prayer Meeting",
            Weekdays = new List<int> { 5 },
            StartTime = "18:00",
        },
        new ServiceTemplate
        {
            Id = "sunday-service",
            Name = "Sunday Service",
            Weekdays = new List<int> { 0 },
            StartTime = "10:30",
        },
        // FR 3.1: a built-in blank template for ad-hoc streams. No weekdays, no start time, so it
        // never matches automatically — it is only reachable through manual creation.
        new ServiceTemplate
        {
            Id = CustomTemplateId,
            Name = "",
            TitleFormat = "",
            Weekdays = new List<int>(),
            StartTime = null,
        },
    };

    private const string CodeAlphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>
    /// Short access code (FR 6.4). Ambiguous glyphs (l/1/o/0) are excluded because operators
    /// occasionally retype the URL by hand instead of scanning the QR code.
    /// </summary>
    public static string AccessCode(int length = 8)
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(length);
        return string.Concat(bytes.Select(b => CodeAlphabet[b % CodeAlphabet.Length]));
    }

    /// <summary>Numeric PIN guarding the settings page (FR 6.5 — anti-fat-finger, not security).</summary>
    public static string SettingsPin() =>
        System.Security.Cryptography.RandomNumberGenerator.GetInt32(1000, 10000)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
}
