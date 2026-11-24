namespace BSpeak.Infrastructure.Audio;

/// <summary>Audio device and turn-detection settings (the <c>audio</c> section of config.json).</summary>
public sealed class AudioOptions
{
    /// <summary>False runs the host without devices: speech is printed, and you type with <c>bspeak utterance post</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>"default", a device index, or part of a device name (see <c>bspeak device list</c>).</summary>
    public string InputDevice { get; set; } = "default";

    public string OutputDevice { get; set; } = "default";

    /// <summary>Rate both device streams run at. 48 kHz suits nearly every headset.</summary>
    public int DeviceSampleRate { get; set; } = 48_000;

    /// <summary>
    /// Ignore the mic while an agent is speaking. Leave off with a headset (it disables barge-in).
    /// Turn on when using room speakers, so the agent does not hear itself.
    /// </summary>
    public bool MuteMicWhilePlaying { get; set; }

    /// <summary>Voice probability that counts as speech.</summary>
    public float OnsetThreshold { get; set; } = 0.5f;

    /// <summary>Voice probability below which speech counts as paused.</summary>
    public float OffsetThreshold { get; set; } = 0.3f;

    /// <summary>Speech must last this long before it counts as you starting to talk (and interrupts an agent).</summary>
    public int OnsetMs { get; set; } = 160;

    /// <summary>Utterances shorter than this are treated as noise.</summary>
    public int MinSpeechMs { get; set; } = 300;

    /// <summary>Silence that ends your turn. Pauses shorter than this stay in the same utterance.</summary>
    public int EndOfTurnSilenceMs { get; set; } = 750;

    /// <summary>Audio kept from just before speech onset, so the first syllable is not clipped.</summary>
    public int PreRollMs { get; set; } = 250;

    /// <summary>An utterance is cut and transcribed once it reaches this length.</summary>
    public int MaxUtteranceSeconds { get; set; } = 45;

    /// <summary>Playback cushion built before audio starts, to ride out gaps between synthesized sentences.</summary>
    public int RenderPrerollMs { get; set; } = 200;
}
