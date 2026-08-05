namespace LiveControlPanel.Config;

/// <summary>
/// Resolves the on-disk layout. FR 2: everything lives under %ProgramData%, never under the
/// user profile — the Windows service account's view of the user profile is not reliable.
/// </summary>
public sealed class AppPaths
{
    public const string DirectoryName = "LiveControlPanel";

    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            DirectoryName);
    }

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string TemplatesFile => Path.Combine(Root, "templates.json");
    public string TokenFile => Path.Combine(Root, "token.dat");
    public string ThumbnailsDirectory => Path.Combine(Root, "thumbnails");
    public string LogDirectory => Path.Combine(Root, "logs");

    /// <summary>Resolves a settings-relative path (e.g. "thumbnails/default.jpg") against the data root.</summary>
    public string Resolve(string relativeOrAbsolute) =>
        Path.IsPathRooted(relativeOrAbsolute)
            ? relativeOrAbsolute
            : Path.Combine(Root, relativeOrAbsolute.Replace('/', Path.DirectorySeparatorChar));

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ThumbnailsDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
