namespace LiveControlPanel.Translate;

/// <summary>
/// A device named in settings is gone — unplugged, powered off, or renamed by Windows.
///
/// Deliberately free of any NAudio type: the translator catches this to build an operator-facing
/// message, and nothing outside <see cref="WasapiAudioEngine"/> should have to know what backs the
/// audio abstraction.
/// </summary>
public sealed class AudioDeviceMissingException : Exception
{
    /// <summary>"capture" or "playback" — which half of the path is broken.</summary>
    public const string CaptureRole = "capture";

    public const string PlaybackRole = "playback";

    public AudioDeviceMissingException(string deviceId, string role)
        : base($"Audio device {deviceId} ({role}) is not available.")
    {
        DeviceId = deviceId;
        Role = role;
    }

    public string DeviceId { get; }
    public string Role { get; }
}

/// <summary>One Windows audio endpoint, as offered on the settings page.</summary>
public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>
/// Pulls the original speech off a recording device and hands it over already in the one format the
/// model accepts: 16 kHz mono little-endian 16-bit, in 100 ms frames.
///
/// The device is opened in shared mode on purpose. OBS is holding the very same mixer endpoint open
/// for the primary broadcast, and taking it exclusively would silence the service.
/// </summary>
public interface IAudioCapture : IDisposable
{
    /// <summary>Exactly <see cref="GeminiProtocol.ChunkBytes"/> per invocation.</summary>
    event Action<byte[]>? FrameReady;

    /// <summary>The device went away — unplugged, or the mixer was switched off.</summary>
    event Action<Exception>? Faulted;

    /// <summary>Peak (0..1) of the most recent frame.</summary>
    double LastPeak { get; }

    void Start();
    void Stop();
}

/// <summary>
/// Plays 24 kHz mono 16-bit PCM into a playback device — in production the virtual cable OBS
/// captures as the translated audio track.
/// </summary>
public interface IAudioSink : IDisposable
{
    double LastPeak { get; }

    /// <summary>How much audio is queued ahead of the device, for drift diagnosis.</summary>
    int BufferedMilliseconds { get; }

    void Start();
    void Write(byte[] pcm24kMono);
    void Stop();
}

/// <summary>
/// Everything Windows-audio-specific behind one seam, so orchestration, health reporting and the
/// pre-flight can all be exercised without a sound card.
/// </summary>
public interface IAudioEngine
{
    IReadOnlyList<AudioDeviceInfo> CaptureDevices();
    IReadOnlyList<AudioDeviceInfo> PlaybackDevices();

    /// <summary>Null or empty selects the system default endpoint.</summary>
    IAudioCapture OpenCapture(string? deviceId);

    IAudioSink OpenSink(string? deviceId);
}

/// <summary>Peak level of a block of 16-bit PCM, normalized to 0..1.</summary>
public static class AudioLevel
{
    public static double Peak(ReadOnlySpan<byte> pcm16)
    {
        var peak = 0;
        for (var i = 0; i + 1 < pcm16.Length; i += 2)
        {
            var sample = (short)(pcm16[i] | (pcm16[i + 1] << 8));
            // -32768 has no positive counterpart; negating it overflows back to itself.
            var magnitude = sample == short.MinValue ? short.MaxValue : Math.Abs((int)sample);
            if (magnitude > peak) peak = magnitude;
        }

        return peak / (double)short.MaxValue;
    }
}

/// <summary>
/// Re-frames a byte stream of unpredictable block sizes into fixed-size frames.
///
/// WASAPI hands over whatever the device's period produced — 10 ms here, 21 ms there — while the API
/// wants 100 ms chunks. Doing this arithmetic inside the capture callback is where clicks and lost
/// tails come from, so it lives here on its own and is tested directly.
/// </summary>
public sealed class PcmFrameSplitter
{
    private readonly byte[] _frame;
    private int _filled;

    public PcmFrameSplitter(int frameBytes)
    {
        if (frameBytes <= 0) throw new ArgumentOutOfRangeException(nameof(frameBytes));
        _frame = new byte[frameBytes];
    }

    public int FrameBytes => _frame.Length;

    /// <summary>Bytes held back because they do not yet complete a frame.</summary>
    public int Pending => _filled;

    /// <summary>Emits <paramref name="onFrame"/> once per completed frame; the remainder is carried.</summary>
    public void Add(ReadOnlySpan<byte> data, Action<byte[]> onFrame)
    {
        while (data.Length > 0)
        {
            var take = Math.Min(_frame.Length - _filled, data.Length);
            data[..take].CopyTo(_frame.AsSpan(_filled));
            _filled += take;
            data = data[take..];

            if (_filled < _frame.Length) return;

            // A copy per frame: the caller keeps it (queued for the socket) well past this call.
            onFrame(_frame.AsSpan().ToArray());
            _filled = 0;
        }
    }

    public void Reset() => _filled = 0;
}
