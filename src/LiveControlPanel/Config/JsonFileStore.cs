using System.Text.Json;

namespace LiveControlPanel.Config;

/// <summary>
/// A JSON file holding a single value, cached in memory and written atomically. Reads never throw:
/// a missing or corrupt file falls back to the seed, because the service starts before anyone is
/// logged in and there is nobody to answer a prompt.
/// </summary>
public sealed class JsonFileStore<T> where T : class
{
    private readonly string _path;
    private readonly Func<T> _seed;
    private readonly object _gate = new();
    private T _value;

    public JsonFileStore(string path, Func<T> seed)
    {
        _path = path;
        _seed = seed;
        _value = Load();
    }

    public T Value
    {
        get { lock (_gate) return _value; }
    }

    /// <summary>True when the backing file did not exist (or was unreadable) and the seed was written.</summary>
    public bool WasSeeded { get; private set; }

    public void Save(T value)
    {
        lock (_gate)
        {
            _value = value;
            WriteAtomic(value);
        }
    }

    /// <summary>Mutates the cached value under the lock and persists the result.</summary>
    public T Update(Action<T> mutate)
    {
        lock (_gate)
        {
            mutate(_value);
            WriteAtomic(_value);
            return _value;
        }
    }

    private T Load()
    {
        var unreadable = false;
        try
        {
            if (File.Exists(_path))
            {
                var parsed = JsonSerializer.Deserialize<T>(File.ReadAllText(_path), Json.Options);
                if (parsed is not null) return parsed;
                unreadable = true;
            }
        }
        catch (Exception)
        {
            // Fall through to the seed. A corrupt settings file must not stop the service from
            // starting; the operator would have no way to fix it at 04:40.
            unreadable = true;
        }

        // Reseeding must never destroy the only copy: settings.json holds the access code that
        // every iPad bookmark and printed QR embeds, plus every secret. Keep the original for an
        // administrator to recover values from.
        if (unreadable) PreserveUnreadableFile();

        var seeded = _seed();
        WasSeeded = true;
        try { WriteAtomic(seeded); } catch (Exception) { /* read-only disk: keep running in memory */ }
        return seeded;
    }

    private void PreserveUnreadableFile()
    {
        try
        {
            var backup = _path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(_path, backup, overwrite: true);
            Serilog.Log.Warning(
                "Could not read {File}. Defaults were written in its place (a NEW access code included), " +
                "and the unreadable original was kept as {Backup} — recover the old values from there.",
                _path, backup);
        }
        catch (Exception)
        {
            // Best effort: starting the panel still matters more than the backup.
        }
    }

    private void WriteAtomic(T value)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Write-then-rename with a real flush. Without the flush the rename can be committed
        // while the data blocks are not — a power cut (this PC is on mains until the UPS arrives)
        // could then leave a present-but-truncated file, which reads as corrupt and triggers the
        // reseed path above.
        var tmp = _path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(fs))
        {
            writer.Write(JsonSerializer.Serialize(value, Json.Options));
            writer.Flush();
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, _path, overwrite: true);
    }
}
