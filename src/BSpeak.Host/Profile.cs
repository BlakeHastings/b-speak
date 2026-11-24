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
        Tts.EffectiveVoices,
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

    /// <summary>Loads config.json, writing the defaults there first if it does not exist yet.</summary>
    public static BSpeakConfig LoadConfig()
    {
        Directory.CreateDirectory(Home);
        if (!File.Exists(ConfigPath))
        {
            var defaults = new BSpeakConfig();
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(defaults, Json));
            return defaults;
        }
        return JsonSerializer.Deserialize<BSpeakConfig>(File.ReadAllText(ConfigPath), Json) ?? new BSpeakConfig();
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
