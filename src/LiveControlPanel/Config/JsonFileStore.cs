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
        try
        {
            if (File.Exists(_path))
            {
                var parsed = JsonSerializer.Deserialize<T>(File.ReadAllText(_path), Json.Options);
                if (parsed is not null) return parsed;
            }
        }
        catch (Exception)
        {
            // Fall through to the seed. A corrupt settings file must not stop the service from
            // starting; the operator would have no way to fix it at 04:40.
        }

        var seeded = _seed();
        WasSeeded = true;
        try { WriteAtomic(seeded); } catch (Exception) { /* read-only disk: keep running in memory */ }
        return seeded;
    }

    private void WriteAtomic(T value)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json.Options));
        File.Move(tmp, _path, overwrite: true);
    }
}
