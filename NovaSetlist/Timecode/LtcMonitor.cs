using System.Runtime.InteropServices;
using NAudio.Wave;
using NovaSetlist.Audio;

namespace NovaSetlist.Timecode;

/// <summary>
/// View-only LTC monitor: captures one WDM (waveIn) input and decodes SMPTE LTC
/// from it. A cut-down version of the TimecodeBridge engine — no Art-Net output,
/// no freewheel, just decode-and-display.
///
/// Captures at the endpoint's native mix rate where discoverable (waveIn never
/// changes the device's shared-mode rate — asking for anything else just inserts
/// a resample), with deep MME buffering (8 × 10 ms) because MME scheduling is
/// jittery under load.
/// </summary>
public sealed class LtcMonitor : IDisposable
{
    private readonly WaveInEvent _waveIn;
    private readonly LtcDecoder _decoder;
    private readonly FloatRingBuffer _ring = new(1 << 17);
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _worker;
    private readonly int _channels;
    private float[] _conv = Array.Empty<float>();
    private volatile bool _stop;
    private volatile string? _error;

    // Last confirmed frame, packed for lock-free cross-thread reads:
    // bit 32 = drop-frame, bits 24..31 h, 16..23 m, 8..15 s, 0..7 f. -1 = none yet.
    private long _frameBits = -1;

    // Wall-clock tick of the last audio buffer. The decoder measures "signal" in samples,
    // so if a virtual device (Dante/NDI/VB-Cable) silently stops delivering, sample time
    // freezes and SIGNAL/LOCK would read true forever — the UI checks this instead.
    private long _lastAudioTick = Environment.TickCount64;

    public string Description { get; }

    public LtcMonitor(int deviceNumber)
    {
        var caps = WaveInEvent.GetCapabilities(deviceNumber);
        _channels = Math.Clamp(caps.Channels, 1, 2);
        int sampleRate = WdmInput.GuessEndpointRate(caps.ProductName) ?? 48000;

        _decoder = new LtcDecoder(sampleRate);
        _decoder.FrameDecoded += f =>
        {
            long bits = ((long)(f.DropFrame ? 1 : 0) << 32) |
                        ((long)f.Hours << 24) | ((long)f.Minutes << 16) |
                        ((long)f.Seconds << 8) | f.Frames;
            Interlocked.Exchange(ref _frameBits, bits);
        };

        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(sampleRate, 16, _channels),
            BufferMilliseconds = 10,
            // 120 ms depth: NDI/network audio sources deliver in bursts, and every
            // dropped MME buffer is a hole in the LTC stream that costs the lock.
            NumberOfBuffers = 12,
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += (_, e) =>
        {
            if (!_stop)
                _error = e.Exception is null ? "Input stopped" : "Input error — device lost?";
        };

        Description = $"{caps.ProductName} · {sampleRate / 1000.0:0.#} kHz";

        _worker = new Thread(DecodeLoop)
        {
            IsBackground = true,
            Name = "LTC decode",
            // Keep decoding punctual under UI load — a late decode looks like a stutter.
            Priority = ThreadPriority.AboveNormal,
        };
    }

    public bool SignalPresent => _decoder.SignalPresent;
    public bool Locked => _decoder.Locked;
    public double MeasuredFps => _decoder.MeasuredFps;
    public TimecodeRate DetectedRate => _decoder.DetectedRate;
    public string? Error => _error;

    /// <summary>Milliseconds since the input last delivered audio; large = capture has stalled.</summary>
    public long MsSinceAudio => Environment.TickCount64 - Volatile.Read(ref _lastAudioTick);

    /// <summary>Packed last confirmed frame (see _frameBits layout), or -1 if none yet.
    /// Cheap to poll — compare against the previous value before unpacking.</summary>
    public long CurrentBits => Interlocked.Read(ref _frameBits);

    public static LtcFrame FrameFromBits(long bits) => new(
        (int)(bits >> 24) & 0xFF, (int)(bits >> 16) & 0xFF,
        (int)(bits >> 8) & 0xFF, (int)bits & 0xFF,
        (bits >> 32 & 1) != 0);

    public void Start()
    {
        Volatile.Write(ref _lastAudioTick, Environment.TickCount64);
        _worker.Start();
        _waveIn.StartRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        Volatile.Write(ref _lastAudioTick, Environment.TickCount64);
        var src = MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded));
        int frames = src.Length / _channels;
        if (frames <= 0)
            return;
        if (_conv.Length < frames)
            _conv = new float[frames];

        for (var i = 0; i < frames; i++)
            _conv[i] = src[i * _channels] * (1f / 32768f); // channel 1

        _ring.Write(_conv.AsSpan(0, frames));
        try { _signal.Set(); }
        catch (ObjectDisposedException) { /* torn down while a late callback was in flight */ }
    }

    private void DecodeLoop()
    {
        var buf = new float[4096];
        try
        {
            while (!_stop)
            {
                _signal.WaitOne(100);
                int n;
                while (!_stop && (n = _ring.Read(buf)) > 0)
                    _decoder.Process(buf.AsSpan(0, n));
            }
        }
        catch (Exception)
        {
            // A dead decode thread must be visible, never silent (TimecodeBridge lesson).
            _error = "Decoder stopped unexpectedly — reselect the input";
        }
    }

    public void Dispose()
    {
        _stop = true;
        try { _waveIn.StopRecording(); } catch { /* device already gone */ }
        _waveIn.Dispose();
        try { _signal.Set(); } catch (ObjectDisposedException) { }
        if (_worker.IsAlive)
            _worker.Join(1000);
        _signal.Dispose();
    }
}
