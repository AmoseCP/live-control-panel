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
        // FR 3.1: a built-in template for ad-hoc streams outside the fixed schedule. No weekdays and
        // no start time, so it never matches automatically — it is only reachable through manual
        // creation.
        //
        // It carries a name and the standard title format so an ad-hoc stream defaults to
        // "8/5/2026 Service" from the server's date. The operator never types a date by hand:
        // hand-typing invites "08/05/2026" (zero-padded, against FR 4.1) or the wrong day after
        // midnight, and a title cannot be corrected once the broadcast exists. The title stays
        // editable for anyone who wants something more specific.
        new ServiceTemplate
        {
            Id = CustomTemplateId,
            Name = "Service",
            TitleFormat = "{M}/{D}/{YYYY} {name}",
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

    /// <summary>
    /// Default PIN for the settings page. Fixed, not random: FR 6.5 states the purpose is to stop
    /// seven people mis-editing settings, not to keep anyone out. A random PIN meant nobody could
    /// open the settings page without first going to read a JSON file on the server, which is worse
    /// than the problem it solved. Change it on the settings page, or edit settings.json.
    /// </summary>
    public const string DefaultSettingsPin = "0000";

    public static string SettingsPin() => DefaultSettingsPin;
}
