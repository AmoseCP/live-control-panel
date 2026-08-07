using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Util.Store;

namespace LiveControlPanel.Youtube;

/// <summary>
/// Token store backed by a single DPAPI-encrypted file (FR 3.3).
///
/// The scope is <see cref="DataProtectionScope.LocalMachine"/> on purpose: the panel normally runs
/// as a Windows service account, but an administrator may also run the exe interactively to
/// re-authorize. CurrentUser scope would make the file undecryptable across those two identities.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiDataStore : IDataStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LiveControlPanel.v1");

    /// <summary>Stored inside the encrypted payload, never as a type-keyed entry.</summary>
    private const string AuthorizedAtKey = "LiveControlPanel:authorizedAt";

    private readonly string _path;
    private readonly object _gate = new();

    public DpapiDataStore(string path) => _path = path;

    /// <summary>
    /// UTC timestamp of the last explicit authorization, used for the authorization-age warning.
    /// This must NOT be derived from the file's write time: Google's flow stores the refreshed
    /// access token through this same store on every refresh (roughly hourly while the panel
    /// runs), which would reset a file-time-based countdown forever — the FR 8 expiry warning
    /// would then never fire. Only <see cref="MarkAuthorized"/> moves this value.
    /// </summary>
    public DateTime? AuthorizedAtUtc
    {
        get
        {
            lock (_gate)
            {
                var all = ReadAll();
                if (all.TryGetValue(AuthorizedAtKey, out var raw)
                    && DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at))
                    return at.ToUniversalTime();

                // Tokens stored before the explicit stamp existed: the file time is the only
                // approximation available. New authorizations always write the stamp.
                return File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : null;
            }
        }
    }

    /// <summary>Called once per explicit (re-)authorization; never by token refreshes.</summary>
    public void MarkAuthorized(DateTime utcNow)
    {
        lock (_gate)
        {
            var all = ReadAll();
            all[AuthorizedAtKey] = utcNow.ToString("O");
            WriteAll(all);
        }
    }

    public Task StoreAsync<T>(string key, T value)
    {
        lock (_gate)
        {
            var all = ReadAll();
            all[Key<T>(key)] = JsonSerializer.Serialize(value);
            WriteAll(all);
        }
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        lock (_gate)
        {
            var all = ReadAll();
            if (all.TryGetValue(Key<T>(key), out var raw) && !string.IsNullOrEmpty(raw))
            {
                var value = JsonSerializer.Deserialize<T>(raw);
                if (value is not null) return Task.FromResult(value);
            }
        }
        return Task.FromResult<T>(default!);
    }

    public Task DeleteAsync<T>(string key)
    {
        lock (_gate)
        {
            var all = ReadAll();
            if (all.Remove(Key<T>(key))) WriteAll(all);
        }
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        lock (_gate)
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        return Task.CompletedTask;
    }

    public bool HasToken
    {
        get { lock (_gate) return ReadAll().Count > 0; }
    }

    private static string Key<T>(string key) => $"{typeof(T).FullName}:{key}";

    private Dictionary<string, string> ReadAll()
    {
        if (!File.Exists(_path)) return new Dictionary<string, string>();

        try
        {
            var protectedBytes = File.ReadAllBytes(_path);
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain)
                   ?? new Dictionary<string, string>();
        }
        catch (Exception)
        {
            // Unreadable token file (wrong machine, corrupted). Behave as "not authorized" so the
            // pre-flight surfaces a re-authorize prompt instead of the service failing to start.
            return new Dictionary<string, string>();
        }
    }

    private void WriteAll(Dictionary<string, string> all)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var plain = JsonSerializer.SerializeToUtf8Bytes(all);
        var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine);

        // Write-then-rename with a real flush: this file is rewritten on every hourly token
        // refresh, so a power cut mid-write would otherwise corrupt the only copy of the
        // refresh token and silently demote the panel to "not authorized".
        var tmp = _path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(encrypted);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, _path, overwrite: true);
    }
}
