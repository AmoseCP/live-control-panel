using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LiveControlPanel.Translate;

/// <summary>
/// WASAPI capture and playback.
///
/// Two deliberate choices:
/// <list type="bullet">
/// <item>Shared mode everywhere. OBS holds the mixer's endpoint open for the primary broadcast, and
/// an exclusive-mode grab here would silence the service it exists to translate.</item>
/// <item>A device explicitly named in settings is never silently replaced by the system default. The
/// translated voice going to the wrong endpoint means it reaches the house PA and folds back into
/// the main mix, so a missing device is an error, not a fallback.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioEngine : IAudioEngine
{
    private readonly ILogger<WasapiAudioEngine> _log;

    public WasapiAudioEngine(ILogger<WasapiAudioEngine> log) => _log = log;

    public IReadOnlyList<AudioDeviceInfo> CaptureDevices() => List(DataFlow.Capture);

    public IReadOnlyList<AudioDeviceInfo> PlaybackDevices() => List(DataFlow.Render);

    public IAudioCapture OpenCapture(string? deviceId) => new WasapiCaptureSource(Resolve(DataFlow.Capture, deviceId));

    public IAudioSink OpenSink(string? deviceId) => new WasapiSink(Resolve(DataFlow.Render, deviceId));

    private IReadOnlyList<AudioDeviceInfo> List(DataFlow flow)
    {
        var result = new List<AudioDeviceInfo>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            string? defaultId = null;
            try
            {
                using var preferred = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                defaultId = preferred.ID;
            }
            catch (Exception ex)
            {
                // A PC with no default endpoint at all is unusual but not fatal here.
                _log.LogDebug(ex, "No default {Flow} endpoint", flow);
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName,
                        string.Equals(device.ID, defaultId, StringComparison.Ordinal)));
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Enumerating {Flow} endpoints failed", flow);
        }

        return result;
    }

    internal static MMDevice Resolve(DataFlow flow, string? deviceId)
    {
        var enumerator = new MMDeviceEnumerator();
        try
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);

            try
            {
                return enumerator.GetDevice(deviceId);
            }
            catch (Exception)
            {
                throw new AudioDeviceMissingException(deviceId,
                    flow == DataFlow.Capture
                        ? AudioDeviceMissingException.CaptureRole
                        : AudioDeviceMissingException.PlaybackRole);
            }
        }
        finally
        {
            enumerator.Dispose();
        }
    }

    /// <summary>
    /// Reads a recording endpoint and converts on the fly to the single format the model accepts.
    ///
    /// The conversion is a pull chain rather than arithmetic in the callback: WASAPI delivers
    /// whatever its period produced, and re-framing 21 ms blocks into 100 ms chunks by hand inside a
    /// device callback is where dropped tails and clicks come from.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class WasapiCaptureSource : IAudioCapture
    {
        private readonly MMDevice _device;
        private readonly WasapiCapture _capture;
        private readonly BufferedWaveProvider _raw;
        private readonly IWaveProvider _mono16k;
        private readonly PcmFrameSplitter _splitter = new(GeminiProtocol.ChunkBytes);
        private readonly byte[] _scratch = new byte[GeminiProtocol.ChunkBytes];
        private readonly object _gate = new();

        private volatile bool _running;
        private double _peak;

        public WasapiCaptureSource(MMDevice device)
        {
            _device = device;
            _capture = new WasapiCapture(device);

            _raw = new BufferedWaveProvider(_capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(5),
                // Better to lose the oldest audio than to have the callback throw and stop the
                // device: a stalled reader must not take the capture down with it.
                DiscardOnBufferOverflow = true,
                // Without this, Read() pads with silence and the drain loop below never terminates.
                ReadFully = false,
            };

            var samples = ToMono(_raw.ToSampleProvider());
            if (samples.WaveFormat.SampleRate != GeminiProtocol.InputSampleRate)
                samples = new WdlResamplingSampleProvider(samples, GeminiProtocol.InputSampleRate);

            _mono16k = new SampleToWaveProvider16(samples);

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
        }

        public event Action<byte[]>? FrameReady;
        public event Action<Exception>? Faulted;

        public double LastPeak => _peak;

        public void Start()
        {
            _running = true;
            _capture.StartRecording();
        }

        public void Stop()
        {
            _running = false;
            try { _capture.StopRecording(); }
            catch (Exception) { /* already stopped, or the device vanished */ }
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (!_running || e.BytesRecorded <= 0) return;

            lock (_gate)
            {
                _raw.AddSamples(e.Buffer, 0, e.BytesRecorded);
                Drain();
            }
        }

        private void Drain()
        {
            while (true)
            {
                var read = _mono16k.Read(_scratch, 0, _scratch.Length);
                if (read <= 0) return;

                _peak = AudioLevel.Peak(_scratch.AsSpan(0, read));
                _splitter.Add(_scratch.AsSpan(0, read), frame => FrameReady?.Invoke(frame));

                if (read < _scratch.Length) return;
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception is not null && _running) Faulted?.Invoke(e.Exception);
        }

        private static ISampleProvider ToMono(ISampleProvider source)
        {
            if (source.WaveFormat.Channels == 1) return source;

            if (source.WaveFormat.Channels == 2)
                return new StereoToMonoSampleProvider(source) { LeftVolume = 0.5f, RightVolume = 0.5f };

            // More than two channels: take the first rather than summing, because an unknown
            // multi-channel layout may put unrelated material on the later channels.
            var multiplexer = new MultiplexingSampleProvider(new[] { source }, 1);
            multiplexer.ConnectInputToOutput(0, 0);
            return multiplexer;
        }

        public void Dispose()
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            Stop();
            try { _capture.Dispose(); } catch (Exception) { /* ignore */ }
            try { _device.Dispose(); } catch (Exception) { /* ignore */ }
        }
    }

    /// <summary>
    /// Plays the translated voice into a playback endpoint — the virtual cable in production.
    ///
    /// The buffer pads with silence when the model is between utterances. That is deliberate: OBS is
    /// capturing this endpoint continuously, and a stream that stops and restarts between sentences
    /// makes an audio input capture click and re-lock.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class WasapiSink : IAudioSink
    {
        private readonly MMDevice _device;
        private readonly WasapiOut _output;
        private readonly BufferedWaveProvider _buffer;

        private volatile bool _started;
        private double _peak;

        public WasapiSink(MMDevice device)
        {
            _device = device;

            _buffer = new BufferedWaveProvider(new WaveFormat(GeminiProtocol.OutputSampleRate, 16, 1))
            {
                // Ten seconds is deep enough to ride out a network stall and shallow enough that the
                // translation never drifts minutes behind the picture.
                BufferDuration = TimeSpan.FromSeconds(10),
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };

            _output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
            _output.Init(Adapt(_buffer, device));
        }

        public double LastPeak => _peak;

        public int BufferedMilliseconds => (int)_buffer.BufferedDuration.TotalMilliseconds;

        public void Start()
        {
            if (_started) return;
            _started = true;
            _output.Play();
        }

        public void Write(byte[] pcm24kMono)
        {
            if (pcm24kMono.Length == 0) return;

            _peak = AudioLevel.Peak(pcm24kMono);
            _buffer.AddSamples(pcm24kMono, 0, pcm24kMono.Length);
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;
            try { _output.Stop(); } catch (Exception) { /* device gone */ }
            _buffer.ClearBuffer();
        }

        /// <summary>
        /// Converts to the endpoint's own mix format where that is straightforward. NAudio will
        /// resample anything it is handed, but doing the common case here keeps an extra resampler —
        /// and its latency — out of the path.
        /// </summary>
        private static IWaveProvider Adapt(BufferedWaveProvider source, MMDevice device)
        {
            WaveFormat mix;
            try { mix = device.AudioClient.MixFormat; }
            catch (Exception) { return source; }

            ISampleProvider samples = source.ToSampleProvider();

            if (mix.SampleRate != samples.WaveFormat.SampleRate)
                samples = new WdlResamplingSampleProvider(samples, mix.SampleRate);

            if (mix.Channels == 2) samples = new MonoToStereoSampleProvider(samples);
            else if (mix.Channels > 2) return source;   // unusual layout: let NAudio work it out

            return mix.Encoding == WaveFormatEncoding.IeeeFloat
                ? new SampleToWaveProvider(samples)
                : new SampleToWaveProvider16(samples);
        }

        public void Dispose()
        {
            Stop();
            try { _output.Dispose(); } catch (Exception) { /* ignore */ }
            try { _device.Dispose(); } catch (Exception) { /* ignore */ }
        }
    }
}
