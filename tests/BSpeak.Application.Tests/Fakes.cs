using BSpeak.Application;
using BSpeak.Domain;

namespace BSpeak.Application.Tests;

internal sealed class FakeClock : IClock
{
    public DateTimeOffset Now { get; private set; } = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
    public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
}

/// <summary>Records what the service asked the audio side to do.</summary>
internal sealed class FakePlayback : IPlayback
{
    public List<string> Calls { get; } = [];
    public List<PlaybackRequest> Played { get; } = [];

    public void Play(PlaybackRequest request) { Played.Add(request); Calls.Add($"play {request.SpeechId}"); }
    public void Stop(string speechId) => Calls.Add($"stop {speechId}");
    public void PlayEarcon(Earcon earcon) => Calls.Add($"earcon {earcon}");
}

internal sealed class Harness
{
    public FakeClock Clock { get; } = new();
    public FakePlayback Playback { get; } = new();
    public InMemoryEventLog Log { get; } = new();
    public RoomService Room { get; }

    public Harness(RoomSettings? settings = null)
    {
        Room = new RoomService(settings ?? RoomSettings.Default, SpeechRules.Default, Clock, Log, Playback);
    }

    /// <summary>You say something: voice detection fires, then the transcript arrives.</summary>
    public void Say(string text)
    {
        Room.SpeechOnset();
        Clock.Advance(1);
        Room.Utterance(text);
    }

    /// <summary>The audio side plays a granted speech from start to finish.</summary>
    public void PlayThrough(string speechId)
    {
        Room.PlaybackStarted(speechId);
        var sentences = Playback.Played.Last(p => p.SpeechId == speechId).Sentences;
        for (var i = 0; i < sentences.Count; i++) Room.SentencePlayed(speechId, i);
        Room.PlaybackDrained(speechId);
    }

    public IReadOnlyList<string> EventTypesSince(long seq) => Log.After(seq, int.MaxValue).Select(e => e.Event.Type).ToList();
}
