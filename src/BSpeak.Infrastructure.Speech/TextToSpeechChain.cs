using BSpeak.Application;
using Microsoft.Extensions.Logging;

namespace BSpeak.Infrastructure.Speech;

/// <summary>One entry in the step-down list: how to bring a provider up, and the voices it uses.</summary>
/// <param name="Start">Creates the engine; throws (with a reason) when it cannot run on this machine.</param>
public sealed record TtsCandidate(string Name, IReadOnlyList<string> Voices, Func<CancellationToken, Task<ITextToSpeech>> Start);

/// <summary>
/// Text to speech that steps down through an ordered list of providers. At start it uses the first one
/// that comes up and passes a warm-up; if that one fails mid-conversation, it moves to the next for good.
/// Room voices come from the first candidate's list and map to other providers by position, so each
/// participant keeps a distinct, consistent voice whichever provider is speaking.
/// </summary>
public sealed class TextToSpeechChain : IStreamingTextToSpeech
{
    private readonly IReadOnlyList<TtsCandidate> _candidates;
    private readonly IReadOnlyList<string> _roomVoices;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _switch = new(1, 1);
    private readonly List<string> _skipped = [];
    private ITextToSpeech? _engine;
    private int _index = -1;

    private TextToSpeechChain(IReadOnlyList<TtsCandidate> candidates, ILogger logger)
    {
        _candidates = candidates;
        _roomVoices = candidates.Count > 0 ? candidates[0].Voices : [];
        _logger = logger;
    }

    /// <summary>The provider currently speaking.</summary>
    public string Active => _index >= 0 ? _candidates[_index].Name : "none";

    /// <summary>Providers passed over, with why.</summary>
    public IReadOnlyList<string> Skipped { get { lock (_skipped) return [.. _skipped]; } }

    public int SampleRate => _engine?.SampleRate ?? 24_000;

    public static async Task<TextToSpeechChain> StartAsync(IReadOnlyList<TtsCandidate> candidates, ILogger logger, CancellationToken ct = default)
    {
        var chain = new TextToSpeechChain(candidates, logger);
        if (!await chain.StepDownAsync(from: -1, ct))
            throw new InvalidOperationException($"No text to speech provider could start: {string.Join("; ", chain.Skipped)}");
        return chain;
    }

    public async Task<float[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default)
    {
        while (true)
        {
            var index = _index;
            var engine = _engine!;
            try
            {
                return await engine.SynthesizeAsync(text, VoiceFor(index, voice), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Skip(_candidates[index].Name, $"failed while speaking: {ex.Message}");
                if (!await StepDownAsync(index, ct)) throw;
            }
        }
    }

    /// <summary>
    /// Streams from the active provider when it can stream, otherwise yields its whole result as one chunk.
    /// A failure before the first chunk steps down and retries; one after audio started is rethrown.
    /// </summary>
    public async IAsyncEnumerable<float[]> StreamAsync(string text, string voice, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            var index = _index;
            var engine = _engine!;
            if (engine is not IStreamingTextToSpeech streaming)
            {
                yield return await SynthesizeAsync(text, voice, ct);
                yield break;
            }

            var chunks = streaming.StreamAsync(text, VoiceFor(index, voice), ct).GetAsyncEnumerator(ct);
            var any = false;
            Exception? failure = null;
            try
            {
                while (true)
                {
                    bool more;
                    try { more = await chunks.MoveNextAsync(); }
                    catch (Exception ex) when (ex is not OperationCanceledException && !any) { failure = ex; break; }
                    if (!more) yield break;
                    any = true;
                    yield return chunks.Current;
                }
            }
            finally { await chunks.DisposeAsync(); }

            Skip(_candidates[index].Name, $"failed while speaking: {failure!.Message}");
            if (!await StepDownAsync(index, ct)) throw failure;
        }
    }

    /// <summary>Moves to the first working provider after <paramref name="from"/>. Safe to call from several failing calls at once.</summary>
    private async Task<bool> StepDownAsync(int from, CancellationToken ct)
    {
        await _switch.WaitAsync(ct);
        try
        {
            if (_index > from) return true; // another call already moved on
            for (var i = from + 1; i < _candidates.Count; i++)
            {
                var candidate = _candidates[i];
                try
                {
                    var engine = await candidate.Start(ct);
                    await engine.SynthesizeAsync("Ready.", VoiceFor(i, _roomVoices.FirstOrDefault() ?? candidate.Voices[0]), ct);
                    (_engine as IDisposable)?.Dispose();
                    _engine = engine;
                    _index = i;
                    _logger.LogInformation("[tts] speaking with {Provider}", candidate.Name);
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Skip(candidate.Name, ex.Message);
                }
            }
            return false;
        }
        finally { _switch.Release(); }
    }

    private void Skip(string name, string reason)
    {
        _logger.LogWarning("[tts] {Provider} unavailable: {Reason}", name, reason);
        lock (_skipped) _skipped.Add($"{name}: {reason}");
    }

    /// <summary>Maps a room voice to the same position in the given provider's list.</summary>
    private string VoiceFor(int index, string roomVoice)
    {
        var voices = _candidates[index].Voices;
        var position = -1;
        for (var i = 0; i < _roomVoices.Count; i++)
            if (string.Equals(_roomVoices[i], roomVoice, StringComparison.OrdinalIgnoreCase)) { position = i; break; }
        return position < 0 || voices.Count == 0 ? roomVoice : voices[position % voices.Count];
    }
}
