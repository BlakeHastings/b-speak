using BSpeak.Application;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BSpeak.Infrastructure.Speech;

/// <summary>
/// Silero VAD v5 over ONNX. Each call takes one 512-sample window of 16 kHz audio. The model also needs
/// the last 64 samples of the previous window prepended as context: without it every frame, even loud
/// speech, scores near zero.
/// </summary>
public sealed class SileroVad : IVoiceActivityDetector, IDisposable
{
    private const int Window = IVoiceActivityDetector.WindowSize;
    private const int Context = 64;
    private static readonly int[] StateShape = [2, 1, 128];
    private static readonly int[] InputShape = [1, Context + Window];
    private static readonly string[] Outputs = ["output", "stateN"];

    private readonly InferenceSession _session;
    private float[] _state = new float[256];
    private float[] _context = new float[Context];

    public SileroVad(string modelPath)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Silero VAD model is missing. Run: bspeak model pull", modelPath);
        _session = new InferenceSession(modelPath, new Microsoft.ML.OnnxRuntime.SessionOptions { ExecutionMode = ExecutionMode.ORT_SEQUENTIAL });
    }

    public float Probability(float[] window)
    {
        if (window.Length != Window) throw new ArgumentException($"Expected {Window} samples, got {window.Length}.", nameof(window));
        var input = new float[Context + Window];
        _context.CopyTo(input, 0);
        window.CopyTo(input, Context);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(input, InputShape)),
            NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>((float[])_state.Clone(), StateShape)),
            NamedOnnxValue.CreateFromTensor("sr", new DenseTensor<long>(new long[] { 16_000 }, [])),
        };
        using var results = _session.Run(inputs, Outputs);
        var prob = results.First(r => r.Name == "output").AsTensor<float>().FirstOrDefault();
        _state = results.First(r => r.Name == "stateN").AsTensor<float>().ToArray();
        Array.Copy(window, Window - Context, _context, 0, Context);
        return prob;
    }

    public void Reset()
    {
        _state = new float[256];
        _context = new float[Context];
    }

    public void Dispose() => _session.Dispose();
}
