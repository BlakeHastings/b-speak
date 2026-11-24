using System.Runtime.InteropServices;
using System.Threading.Channels;
using PortAudioSharp;

namespace BSpeak.Infrastructure.Audio;

/// <summary>
/// Output-only PortAudio stream fed from a jitter ring. Writers can place markers at sample positions;
/// a marker's action runs once the speaker has actually played up to that position (tapped at playout,
/// not at write time), which is how the room learns that a sentence was heard. Playout waits for a
/// pre-roll cushion unless the writer has said no more audio is coming.
/// </summary>
public sealed class RenderDevice : IDisposable
{
    private readonly AudioOptions _options;
    private readonly object _gate = new();
    private readonly Queue<float> _ring = new();
    private readonly Queue<(long Position, Action Action)> _markers = new();
    private readonly Channel<Action> _due = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly int _preroll;
    private readonly float _keepAlive;
    private uint _noise = 2463534242;
    private long _written;
    private long _played;
    private bool _primed;
    private bool _endOfData;
    private long _lastAudibleTicks;
    private float[] _scratch = [];
    private PortAudioSharp.Stream? _stream;
    private PortAudioSharp.Stream.Callback? _callback;
    private Task? _dispatcher;

    public RenderDevice(AudioOptions options)
    {
        _options = options;
        _preroll = Math.Max(1, options.DeviceSampleRate * options.RenderPrerollMs / 1000);
        _keepAlive = options.KeepAliveDb is { } db && db < 0 ? (float)Math.Pow(10, db / 20.0) : 0f;
    }

    /// <summary>Playback level for speech and tones, 0 to 1. The keep-alive floor is not scaled.</summary>
    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    private volatile float _volume = 1f;

    public int SampleRate => _options.DeviceSampleRate;
    public string DeviceName { get; private set; } = "";

    /// <summary>True while audio is queued or was audible within the last 300 ms.</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate) if (_ring.Count > 0) return true;
            return DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastAudibleTicks) < TimeSpan.FromMilliseconds(300).Ticks;
        }
    }

    public void Start()
    {
        var device = PortAudioSystem.Resolve(_options.OutputDevice, output: true);
        if (device == PortAudio.NoDevice) throw new InvalidOperationException("No audio output device is available.");
        var info = PortAudio.GetDeviceInfo(device);
        DeviceName = info.name;
        var output = new StreamParameters
        {
            device = device, channelCount = 1, sampleFormat = SampleFormat.Float32,
            suggestedLatency = info.defaultLowOutputLatency, hostApiSpecificStreamInfo = IntPtr.Zero,
        };
        _callback = OnAudio;
        _stream = new PortAudioSharp.Stream(null, output, SampleRate, PortAudio.FramesPerBufferUnspecified, StreamFlags.ClipOff, _callback, IntPtr.Zero);
        _stream.Start();
        _dispatcher = Task.Run(async () =>
        {
            await foreach (var action in _due.Reader.ReadAllAsync())
                try { action(); } catch { /* a marker action must never stop the dispatcher */ }
        });
    }

    /// <summary>The sample position the next written sample will occupy.</summary>
    public long WritePosition { get { lock (_gate) return _written; } }

    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            foreach (var s in samples) _ring.Enqueue(s);
            _written += samples.Length;
            _endOfData = false;
        }
    }

    /// <summary>Runs <paramref name="action"/> once playout passes <paramref name="position"/>.</summary>
    public void MarkAt(long position, Action action)
    {
        lock (_gate)
        {
            if (position <= _played) _due.Writer.TryWrite(action);
            else _markers.Enqueue((position, action));
        }
    }

    /// <summary>No more audio is coming for now: play out what is buffered even if it is shorter than the pre-roll.</summary>
    public void EndOfData()
    {
        lock (_gate) _endOfData = true;
    }

    /// <summary>Drops everything queued and every pending marker (barge-in).</summary>
    public void Flush()
    {
        lock (_gate)
        {
            _played += _ring.Count; // keep positions monotonic: the dropped audio counts as gone
            _ring.Clear();
            _markers.Clear();
            _written = _played;
            _primed = false;
        }
    }

    private StreamCallbackResult OnAudio(IntPtr input, IntPtr output, uint frameCount, ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags flags, IntPtr userData)
    {
        var n = (int)frameCount;
        if (_scratch.Length < n) _scratch = new float[n];
        var audible = false;
        lock (_gate)
        {
            if (!_primed && _ring.Count > 0 && (_ring.Count >= _preroll || _endOfData)) _primed = true;
            var take = _primed ? Math.Min(n, _ring.Count) : 0;
            var gain = Volume;
            for (var i = 0; i < take; i++) _scratch[i] = _ring.Dequeue() * gain;
            // Fill the rest with a faint noise floor rather than digital silence, so devices that power down
            // on silence (many wireless headsets) stay awake and do not swallow the start of the next speech.
            for (var i = take; i < n; i++) _scratch[i] = NextNoise() * _keepAlive;
            _played += take;
            audible = take > 0;
            if (_primed && _ring.Count == 0) _primed = false;
            while (_markers.Count > 0 && _markers.Peek().Position <= _played) _due.Writer.TryWrite(_markers.Dequeue().Action);
        }
        if (audible) Interlocked.Exchange(ref _lastAudibleTicks, DateTime.UtcNow.Ticks);
        Marshal.Copy(_scratch, 0, output, n);
        return StreamCallbackResult.Continue;
    }

    /// <summary>White noise in [-1, 1] from a xorshift generator: cheap and allocation-free for the audio thread.</summary>
    private float NextNoise()
    {
        _noise ^= _noise << 13;
        _noise ^= _noise >> 17;
        _noise ^= _noise << 5;
        return _noise / (float)uint.MaxValue * 2f - 1f;
    }

    public void Dispose()
    {
        try { _stream?.Stop(); } catch { }
        try { _stream?.Dispose(); } catch { }
        _stream = null;
        _due.Writer.TryComplete();
    }
}
