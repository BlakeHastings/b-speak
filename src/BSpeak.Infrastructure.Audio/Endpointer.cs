namespace BSpeak.Infrastructure.Audio;

public enum EndpointKind { None, Onset, Committed, Abandoned }

/// <param name="Audio">The utterance audio at 16 kHz, for <see cref="EndpointKind.Committed"/>.</param>
public readonly record struct EndpointResult(EndpointKind Kind, float[]? Audio = null)
{
    public static readonly EndpointResult None = new(EndpointKind.None);
}

/// <summary>
/// Turns per-window voice probabilities into turn boundaries. Speech that lasts <c>OnsetMs</c> announces
/// <see cref="EndpointKind.Onset"/> (you started talking: barge-in). Pauses shorter than the end-of-turn
/// silence stay in the same utterance. When the turn ends it is either <see cref="EndpointKind.Committed"/>
/// with its audio, or <see cref="EndpointKind.Abandoned"/> when it was too short to be speech, so the room
/// can close the turn it opened.
/// </summary>
public sealed class Endpointer
{
    public const int WindowSamples = 512;
    private const double WindowMs = WindowSamples / 16.0;

    private enum State { Idle, Speaking, Trailing }

    private readonly float _onset, _offset;
    private readonly int _onsetFrames, _minSpeechFrames, _endFrames, _preRollFrames, _maxFrames;
    private readonly Queue<float[]> _preRoll = new();
    private List<float[]> _segment = [];
    private State _state = State.Idle;
    private int _speechFrames;
    private int _silenceFrames;
    private bool _announced;

    public Endpointer(AudioOptions o)
    {
        _onset = o.OnsetThreshold;
        _offset = o.OffsetThreshold;
        _onsetFrames = Frames(o.OnsetMs);
        _minSpeechFrames = Frames(o.MinSpeechMs);
        _endFrames = Frames(o.EndOfTurnSilenceMs);
        _preRollFrames = Frames(o.PreRollMs);
        _maxFrames = Frames(o.MaxUtteranceSeconds * 1000);
    }

    private static int Frames(int ms) => Math.Max(1, (int)Math.Round(ms / WindowMs));

    public EndpointResult Process(float[] window, float probability)
    {
        switch (_state)
        {
            case State.Idle:
                if (probability >= _onset)
                {
                    _state = State.Speaking;
                    _segment = [.. _preRoll, window];
                    _preRoll.Clear();
                    _speechFrames = 1;
                    _silenceFrames = 0;
                    _announced = false;
                    return MaybeAnnounce();
                }
                _preRoll.Enqueue(window);
                while (_preRoll.Count > _preRollFrames) _preRoll.Dequeue();
                return EndpointResult.None;

            case State.Speaking:
                _segment.Add(window);
                if (probability < _offset) { _state = State.Trailing; _silenceFrames = 1; }
                else _speechFrames++;
                return _segment.Count >= _maxFrames ? Finish() : MaybeAnnounce();

            default: // Trailing
                _segment.Add(window);
                if (probability >= _onset)
                {
                    _state = State.Speaking;
                    _speechFrames++;
                    _silenceFrames = 0;
                    return MaybeAnnounce();
                }
                _silenceFrames++;
                return _silenceFrames >= _endFrames || _segment.Count >= _maxFrames ? Finish() : EndpointResult.None;
        }
    }

    private EndpointResult MaybeAnnounce()
    {
        if (_announced || _speechFrames < _onsetFrames) return EndpointResult.None;
        _announced = true;
        return new EndpointResult(EndpointKind.Onset);
    }

    private EndpointResult Finish()
    {
        var announced = _announced;
        var enough = _speechFrames >= _minSpeechFrames;
        var segment = _segment;
        _state = State.Idle;
        _segment = [];
        _speechFrames = 0;
        _silenceFrames = 0;
        _announced = false;
        if (!announced) return EndpointResult.None;
        if (!enough) return new EndpointResult(EndpointKind.Abandoned);
        var audio = new float[segment.Sum(s => s.Length)];
        var offset = 0;
        foreach (var s in segment) { s.CopyTo(audio, offset); offset += s.Length; }
        return new EndpointResult(EndpointKind.Committed, audio);
    }
}
