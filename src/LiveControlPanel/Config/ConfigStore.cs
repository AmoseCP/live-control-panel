namespace LiveControlPanel.Config;

/// <summary>Settings and templates, seeded on first run.</summary>
public sealed class ConfigStore
{
    private readonly JsonFileStore<AppSettings> _settings;
    private readonly JsonFileStore<List<ServiceTemplate>> _templates;

    public ConfigStore(AppPaths paths)
    {
        Paths = paths;
        paths.EnsureCreated();

        _settings = new JsonFileStore<AppSettings>(paths.SettingsFile, () => new AppSettings
        {
            AccessCode = Seed.AccessCode(),
            SettingsPin = Seed.SettingsPin(),
        });
        _templates = new JsonFileStore<List<ServiceTemplate>>(paths.TemplatesFile, Seed.Templates);

        // A settings file that predates these fields, or was hand-edited, must still yield a
        // usable access code and PIN — otherwise nobody can open the panel.
        if (string.IsNullOrWhiteSpace(_settings.Value.AccessCode) ||
            string.IsNullOrWhiteSpace(_settings.Value.SettingsPin))
        {
            _settings.Update(s =>
            {
                if (string.IsNullOrWhiteSpace(s.AccessCode)) s.AccessCode = Seed.AccessCode();
                if (string.IsNullOrWhiteSpace(s.SettingsPin)) s.SettingsPin = Seed.SettingsPin();
            });
        }
    }

    public AppPaths Paths { get; }
    public AppSettings Settings => _settings.Value;
    public IReadOnlyList<ServiceTemplate> Templates => _templates.Value;

    public bool SettingsWereSeeded => _settings.WasSeeded;
    public bool TemplatesWereSeeded => _templates.WasSeeded;

    public AppSettings UpdateSettings(Action<AppSettings> mutate) => _settings.Update(mutate);

    public void SaveTemplates(List<ServiceTemplate> templates) => _templates.Save(templates);

    public ServiceTemplate? FindTemplate(string id) =>
        _templates.Value.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Templates that participate in automatic matching (FR 4.1) — i.e. not "custom".</summary>
    public IEnumerable<ServiceTemplate> SchedulableTemplates() =>
        _templates.Value.Where(t => t.Weekdays.Count > 0 && !string.IsNullOrWhiteSpace(t.StartTime));
}
