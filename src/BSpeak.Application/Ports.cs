using BSpeak.Domain;

namespace BSpeak.Application;

public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
}

public enum Earcon { ListeningPaused, ListeningResumed }

/// <summary>A speech that has been granted the floor and should be synthesized and played.</summary>
public sealed record PlaybackRequest(string SpeechId, string Voice, IReadOnlyList<string> Sentences);

/// <summary>
/// The render side: text to speech plus audio output. It reports progress back through
/// <see cref="RoomService.PlaybackStarted"/>, <see cref="RoomService.SentencePlayed"/> and
/// <see cref="RoomService.PlaybackDrained"/>. Calls arrive outside the service's lock, so an
/// implementation may report back synchronously.
/// </summary>
public interface IPlayback
{
    void Play(PlaybackRequest request);

    /// <summary>Stops a speech now and discards anything buffered for it (barge-in).</summary>
    void Stop(string speechId);

    void PlayEarcon(Earcon earcon);
}

/// <summary>Speech to text for one finished utterance: 16 kHz mono float PCM in, text out.</summary>
public interface ISpeechToText
{
    Task<string> TranscribeAsync(float[] audio16k, CancellationToken ct = default);
}

/// <summary>Text to speech for one sentence in a given voice: mono float PCM at <see cref="SampleRate"/>.</summary>
public interface ITextToSpeech
{
    int SampleRate { get; }

    Task<float[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default);
}

/// <summary>Voice activity detection over 512-sample windows of 16 kHz audio (32 ms each).</summary>
public interface IVoiceActivityDetector
{
    const int WindowSize = 512;

    float Probability(float[] window);

    void Reset();
}

public sealed record StoredEvent(long Seq, DateTimeOffset At, DomainEvent Event);

/// <summary>Append-only log of accepted events. Every reader keeps its own position.</summary>
public interface IEventLog
{
    long Head { get; }

    StoredEvent Append(DomainEvent e, DateTimeOffset at);

    IReadOnlyList<StoredEvent> After(long seq, int max = 1000);

    /// <summary>Completes the next time an event is appended.</summary>
    Task Changed { get; }
}

/// <summary>A lookup found nothing. Adapters map it to exit code 4 or HTTP 404.</summary>
public sealed class NotFoundException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
