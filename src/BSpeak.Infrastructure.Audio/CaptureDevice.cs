using System.Runtime.InteropServices;
using System.Threading.Channels;
using PortAudioSharp;

namespace BSpeak.Infrastructure.Audio;

/// <summary>Input-only PortAudio stream. The real-time callback only copies each mic buffer out to <see cref="Frames"/>.</summary>
public sealed class CaptureDevice(AudioOptions options) : IDisposable
{
    private readonly Channel<float[]> _frames = Channel.CreateBounded<float[]>(
        new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });

    private PortAudioSharp.Stream? _stream;
    private PortAudioSharp.Stream.Callback? _callback; // held so the GC does not collect it while native code uses it

    public int SampleRate => options.DeviceSampleRate;
    public string DeviceName { get; private set; } = "";
    public ChannelReader<float[]> Frames => _frames.Reader;

    public void Start()
    {
        var device = PortAudioSystem.Resolve(options.InputDevice, output: false);
        if (device == PortAudio.NoDevice) throw new InvalidOperationException("No audio input device is available.");
        var info = PortAudio.GetDeviceInfo(device);
        DeviceName = info.name;
        var input = new StreamParameters
        {
            device = device, channelCount = 1, sampleFormat = SampleFormat.Float32,
            suggestedLatency = info.defaultLowInputLatency, hostApiSpecificStreamInfo = IntPtr.Zero,
        };
        _callback = OnAudio;
        _stream = new PortAudioSharp.Stream(input, null, SampleRate, PortAudio.FramesPerBufferUnspecified, StreamFlags.ClipOff, _callback, IntPtr.Zero);
        _stream.Start();
    }

    private StreamCallbackResult OnAudio(IntPtr input, IntPtr output, uint frameCount, ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags flags, IntPtr userData)
    {
        var frame = new float[frameCount];
        if (input != IntPtr.Zero) Marshal.Copy(input, frame, 0, (int)frameCount);
        _frames.Writer.TryWrite(frame);
        return StreamCallbackResult.Continue;
    }

    public void Dispose()
    {
        try { _stream?.Stop(); } catch { }
        try { _stream?.Dispose(); } catch { }
        _stream = null;
        _frames.Writer.TryComplete();
    }
}
