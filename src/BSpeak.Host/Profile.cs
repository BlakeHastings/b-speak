using System.Text.Json;
using System.Text.Json.Serialization;
using BSpeak.Domain;
using BSpeak.Infrastructure.Audio;
using BSpeak.Infrastructure.Speech;

namespace BSpeak.Host;

/// <summary>Room settings (the <c>room</c> section of config.json).</summary>
public sealed class RoomOptions
{
    public int UserGapMs { get; set; } = 700;
    public int IdleTimeoutMinutes { get; set; } = 30;
    public int MaxSpeechLength { get; set; } = 600;
    public List<string> PausePhrases { get; set; } = ["stop listening", "pause listening"];
    public List<string> ResumePhrases { get; set; } = ["start listening", "resume listening"];
}

/// <summary>Everything in ~/.bspeak/config.json.</summary>
public sealed class BSpeakConfig
{
    /// <summary>Port the host listens on, on localhost only. 0 picks a free port each start.</summary>
    public int Port { get; set; }

    public RoomOptions Room { get; set; } = new();
    public AudioOptions Audio { get; set; } = new();
    public SttOptions Stt { get; set; } = new();
    public TtsOptions Tts { get; set; } = new();

    public RoomSettings ToRoomSettings() => new(
        TimeSpan.FromMilliseconds(Room.UserGapMs),
        TimeSpan.FromMinutes(Room.IdleTimeoutMinutes),
        Tts.RoomVoices,
        new ControlPhrases(Room.PausePhrases, Room.ResumePhrases));

    public SpeechRules ToSpeechRules() => new(Room.MaxSpeechLength);
}

/// <summary>Where the host is listening, written while it runs.</summary>
public sealed record HostInfo(int Port, int Pid, DateTimeOffset StartedAt);

/// <summary>The per-user b-speak folder: ~/.bspeak, or BSPEAK_HOME when set.</summary>
public static class Profile
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    public static string Home => Environment.GetEnvironmentVariable("BSPEAK_HOME") is { Length: > 0 } h
        ? h
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bspeak");

    public static string ConfigPath => Path.Combine(Home, "config.json");
    public static string HostInfoPath => Path.Combine(Home, "host.json");
    public static string LogPath => Path.Combine(Home, "host.log");
    public static string ModelsDir => Path.Combine(Home, "models");

    /// <summary>
    /// The file written on first run. It sets nothing, so every setting keeps tracking the built-in default
    /// until you choose to override it; the comments show the settings people usually change.
    /// </summary>
    public const string StarterConfig = """
        // b-speak settings. Anything you leave out uses the built-in default.
        // Restart the host after editing: bspeak host delete (the next command starts it again).
        // All settings: bspeak config get
        {
          // "audio": { "inputDevice": "default", "outputDevice": "default", "endOfTurnSilenceMs": 750 },
          // "stt": { "provider": "whisper", "model": "small.en" },
          // Text to speech providers are tried in order; the first that runs on this machine speaks,
          // and if it fails later the next one takes over.
          // Default: Gradium's streaming server in Docker (started for you), then their CLI, then Kokoro.
          // "tts": { "providers": ["phonon-server", "phonon", "kokoro"], "phonon": { "voices": ["Miles", "Harper"] }, "kokoro": { "speed": 1.1 } },
          // Any other OpenAI-compatible server: "tts": { "providers": ["openai", "kokoro"], "openAi": { "endpoint": "http://host:port/v1", "streaming": true } },
        }
        """;

    private static readonly JsonSerializerOptions ReadOptions = new(Json) { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Loads config.json, writing the starter file there first if it does not exist yet.</summary>
    public static BSpeakConfig LoadConfig()
    {
        Directory.CreateDirectory(Home);
        if (!File.Exists(ConfigPath)) File.WriteAllText(ConfigPath, StarterConfig);
        return JsonSerializer.Deserialize<BSpeakConfig>(File.ReadAllText(ConfigPath), ReadOptions) ?? new BSpeakConfig();
    }

    public static HostInfo? ReadHostInfo()
    {
        try { return File.Exists(HostInfoPath) ? JsonSerializer.Deserialize<HostInfo>(File.ReadAllText(HostInfoPath), Json) : null; }
        catch { return null; }
    }

    public static void WriteHostInfo(HostInfo info)
    {
        Directory.CreateDirectory(Home);
        File.WriteAllText(HostInfoPath, JsonSerializer.Serialize(info, Json));
    }

    public static void ClearHostInfo(int pid)
    {
        if (ReadHostInfo()?.Pid == pid) File.Delete(HostInfoPath);
    }
}
