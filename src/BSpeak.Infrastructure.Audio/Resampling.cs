using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BSpeak.Infrastructure.Audio;

/// <summary>Anti-aliased resampling over NAudio's managed WDL resampler (cross-platform; no NAudio device code).</summary>
public static class Resampling
{
    /// <summary>Resamples a whole buffer at once, so the filter always has its full lookahead.</summary>
    public static float[] Resample(float[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate || input.Length == 0) return input;
        var source = new BufferProvider(input, fromRate);
        var resampler = new WdlResamplingSampleProvider(source, toRate);
        var output = new List<float>((int)((long)input.Length * toRate / fromRate) + 1024);
        var buffer = new float[8192];
        int read;
        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
            for (var i = 0; i < read; i++) output.Add(buffer[i]);
        return output.ToArray();
    }

    private sealed class BufferProvider(float[] data, int rate) : ISampleProvider
    {
        private int _pos;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }
}

/// <summary>
/// Push-driven streaming resampler for the mic: frames go in at the device rate and come out at 16 kHz.
/// Reads leave a small margin of input unread so the filter keeps lookahead (draining it dry at every
/// frame creates edge artifacts).
/// </summary>
public sealed class PushResampler
{
    private const int Lookahead = 128;
    private readonly object _lock = new();
    private readonly Queue<float> _input = new();
    private readonly WdlResamplingSampleProvider _resampler;
    private readonly double _ratio;

    public PushResampler(int inputRate, int outputRate)
    {
        _ratio = (double)outputRate / inputRate;
        _resampler = new WdlResamplingSampleProvider(new QueueProvider(_input, _lock, inputRate), outputRate);
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_lock) foreach (var s in samples) _input.Enqueue(s);
    }

    /// <summary>Reads what the buffered input fully supports into <paramref name="destination"/>.</summary>
    public int Read(float[] destination)
    {
        int available;
        lock (_lock) available = _input.Count - Lookahead;
        if (available <= 0) return 0;
        var want = Math.Min(destination.Length, (int)(available * _ratio));
        return want <= 0 ? 0 : _resampler.Read(destination, 0, want);
    }

    /// <summary>
    /// End of the stream: reads out everything still buffered, including the lookahead margin. Pads a little
    /// silence so the filter can flush its tail. Call until it returns 0.
    /// </summary>
    public int Drain(float[] destination)
    {
        lock (_lock)
        {
            if (!_drained) { foreach (var _ in Enumerable.Range(0, Lookahead * 2)) _input.Enqueue(0f); _drained = true; }
        }
        return _resampler.Read(destination, 0, destination.Length);
    }

    private bool _drained;

    private sealed class QueueProvider(Queue<float> queue, object gate, int rate) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            lock (gate)
            {
                var n = 0;
                while (n < count && queue.Count > 0) buffer[offset + n++] = queue.Dequeue();
                return n;
            }
        }
    }
}
