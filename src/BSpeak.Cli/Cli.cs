using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using BSpeak.Host;
using BSpeak.Infrastructure.Audio;
using BSpeak.Infrastructure.Speech;
using Microsoft.Extensions.Logging;

namespace BSpeak.Cli;

public static class ExitCodes
{
    public const int Ok = 0, Rule = 1, Usage = 2, TimedOut = 3, NotFound = 4, HostUnavailable = 5;
}

public sealed class UsageException(string message) : Exception(message);
public sealed class HostUnavailableException(string message) : Exception(message);

public static class Json
{
    public static string Pretty(string body)
    {
        try { return JsonNode.Parse(body)?.ToJsonString(Profile.Json) ?? body; }
        catch (JsonException) { return body; }
    }

    public static string Of(object value) => JsonSerializer.Serialize(value, Profile.Json);

    public static string Error(string code, string message) => Of(new { error = new { code, message } });
}

/// <summary>Parsed command line: <c>bspeak &lt;resource&gt; &lt;verb&gt; [positional...] [--option value] [--flag]</c>.</summary>
public sealed class Args
{
    private static readonly HashSet<string> Flags = ["wait", "for-you", "no-audio", "background", "help", "play"];

    public List<string> Positional { get; } = [];
    public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static Args Parse(string[] args)
    {
        var result = new Args();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--"))
            {
                var name = a[2..];
                var eq = name.IndexOf('=');
                if (eq >= 0) result.Options[name[..eq]] = name[(eq + 1)..];
                else if (Flags.Contains(name)) result.Options[name] = "true";
                else if (i + 1 < args.Length) result.Options[name] = args[++i];
                else throw new UsageException($"--{name} needs a value.");
            }
            else result.Positional.Add(a);
        }
        return result;
    }

    public string? Get(string name) => Options.GetValueOrDefault(name);
    public bool Has(string name) => Options.ContainsKey(name);

    public int? Int(string name) => Get(name) is { } v
        ? int.TryParse(v, out var n) ? n : throw new UsageException($"--{name} must be a number.")
        : null;

    public string Require(int index, string what) =>
        Positional.Count > index ? Positional[index] : throw new UsageException($"Missing {what}.");

    /// <summary>The participant this command acts as: --as, or the BSPEAK_AS environment variable.</summary>
    public string As => Get("as") ?? Environment.GetEnvironmentVariable("BSPEAK_AS")
        ?? throw new UsageException("Say who you are with --as <Name> (the name participant post gave you).");
}

public static class Cli
{
    private const string Help = """
        bspeak: talk with your agents by voice.

        Commands print JSON. Exit codes: 0 ok, 1 rejected by a rule, 2 usage, 3 wait timed out, 4 not found, 5 host unavailable.
        Any command starts the host in the background if it is not running.

          brief get [--as <name>]                          Instructions for agents, plus who is in the room (text)
          participant post [--name <name>]                 Join the room; prints your name, voice and "after"
          participant list
          participant delete <name>                        Leave the room
          utterance list --as <name> [--after <id>] [--for-you] [--wait] [--timeout <seconds>]
                                                           What the user said after utterance <id>; --wait blocks until they speak
          utterance post "<text>"                          Type as if you had said it (testing, or no mic)
          speech post "<text>" --as <name> [--reply-to <id>] [--wait] [--timeout <seconds>]
                                                           Say something; --wait blocks until it played or was interrupted
          speech get <id> [--wait]
          speech list [--as <name>]
          speech delete <id> --as <name>                   Withdraw a speech that has not played yet
          listening get
          listening put on|off [--as <name>]               Same as saying "start listening" / "stop listening"
          listening patch --mode always|wake-word [--wake-word "<phrase>"]
          event list [--after <seq>] [--wait]              The raw event log
          host get                                         Status: audio, devices, models, room
          host delete                                      Stop the host
          device list                                      Audio devices (set audio.inputDevice / outputDevice in config.json)
          model list | model pull                          Local model files; pull downloads what is missing
          config get                                       Where config.json is, and what it says
          selftest [--play]                                Speak a sentence and transcribe it back through the real engines
          serve [--no-audio] [--port <n>]                  Run the host in the foreground
        """;

    public static async Task<int> RunAsync(string[] argv)
    {
        var args = Args.Parse(argv);
        if (args.Positional.Count == 0 || args.Positional[0] is "help" or "-h" || args.Has("help"))
        {
            Console.WriteLine(Help);
            return ExitCodes.Ok;
        }

        var resource = args.Positional[0];
        var verb = args.Positional.Count > 1 ? args.Positional[1] : "";
        switch (resource)
        {
            case "serve":
                return await HostApp.RunAsync(args.Has("no-audio"), args.Int("port"), quiet: args.Has("background"));
            case "model":
                return await ModelAsync(verb);
            case "selftest":
                return await SelfTest.RunAsync(args.Has("play"));
            case "device" when verb == "list":
                Console.WriteLine(Json.Of(PortAudioSystem.List()));
                return ExitCodes.Ok;
            case "config" when verb == "get":
                Console.WriteLine(Json.Of(new { path = Profile.ConfigPath, config = Profile.LoadConfig() }));
                return ExitCodes.Ok;
            case "host" when verb == "delete":
                return await StopHostAsync();
        }

        var client = await HostClient.ConnectAsync();
        return (resource, verb) switch
        {
            ("brief", "get") => await client.TextAsync($"/api/brief{Query(("as", args.Get("as") ?? Environment.GetEnvironmentVariable("BSPEAK_AS")))}"),
            ("participant", "post") => await client.SendAsync(HttpMethod.Post, "/api/participants", new { name = args.Get("name") ?? (args.Positional.Count > 2 ? args.Positional[2] : null) }),
            ("participant", "list") => await client.SendAsync(HttpMethod.Get, "/api/participants"),
            ("participant", "delete") => await client.SendAsync(HttpMethod.Delete, $"/api/participants/{Uri.EscapeDataString(args.Require(2, "participant name"))}"),
            ("utterance", "list") => await UtterancesAsync(client, args),
            ("utterance", "post") => await client.SendAsync(HttpMethod.Post, "/api/utterances", new { text = args.Require(2, "what to say, in quotes") }),
            ("speech", "post") => await SpeakAsync(client, args),
            ("speech", "get") => await SpeechGetAsync(client, args.Require(2, "speech id"), args),
            ("speech", "list") => await client.SendAsync(HttpMethod.Get, $"/api/speech{Query(("as", args.Get("as")))}"),
            ("speech", "delete") => await client.SendAsync(HttpMethod.Delete, $"/api/speech/{Uri.EscapeDataString(args.Require(2, "speech id"))}{Query(("as", args.As))}"),
            ("listening", "get") => await client.SendAsync(HttpMethod.Get, "/api/listening"),
            ("listening", "put") => await client.SendAsync(HttpMethod.Put, "/api/listening", new { on = OnOff(args.Require(2, "on or off")), @as = args.Get("as") }),
            ("listening", "patch") => await client.SendAsync(HttpMethod.Patch, "/api/listening", new { mode = args.Get("mode") ?? throw new UsageException("--mode is required."), wakeWord = args.Get("wake-word") }),
            ("event", "list") => await EventsAsync(client, args),
            ("host", "get") => await client.SendAsync(HttpMethod.Get, "/api/host"),
            _ => throw new UsageException($"Unknown command '{resource} {verb}'."),
        };
    }

    private static bool OnOff(string value) => value.ToLowerInvariant() switch
    {
        "on" or "true" or "start" => true,
        "off" or "false" or "stop" => false,
        _ => throw new UsageException("Use 'on' or 'off'."),
    };

    private static string Query(params (string Key, object? Value)[] pairs)
    {
        var parts = pairs.Where(p => p.Value is not null).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value is bool b ? (b ? "true" : "false") : p.Value!.ToString()!)}").ToList();
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    private static async Task<int> UtterancesAsync(HostClient client, Args args)
    {
        var who = args.As;
        var after = args.Int("after") ?? 0;
        var forYou = args.Has("for-you") ? true : (bool?)null;
        if (!args.Has("wait"))
            return await client.SendAsync(HttpMethod.Get, $"/api/utterances{Query(("as", who), ("after", after), ("forYou", forYou))}");

        var deadline = DateTime.UtcNow.AddSeconds(args.Int("timeout") ?? 1800);
        while (true)
        {
            var remaining = Math.Max(1, (deadline - DateTime.UtcNow).TotalSeconds);
            var (status, body) = await client.RawAsync(HttpMethod.Get, $"/api/utterances{Query(("as", who), ("after", after), ("forYou", forYou), ("wait", Math.Min(remaining, HostApp.MaxWait.TotalSeconds)))}");
            if (status != HttpStatusCode.OK) return client.Print(status, body);
            var batch = JsonNode.Parse(body)!;
            if (batch["utterances"]!.AsArray().Count > 0) return client.Print(status, body);
            if (DateTime.UtcNow >= deadline) { client.Print(status, body); return ExitCodes.TimedOut; }
        }
    }

    private static async Task<int> SpeakAsync(HostClient client, Args args)
    {
        var body = new { @as = args.As, text = args.Require(2, "what to say, in quotes"), replyTo = args.Int("reply-to") };
        if (!args.Has("wait")) return await client.SendAsync(HttpMethod.Post, "/api/speech", body);
        var (status, response) = await client.RawAsync(HttpMethod.Post, $"/api/speech?wait={HostApp.MaxWait.TotalSeconds}", body);
        if (status != HttpStatusCode.OK) return client.Print(status, response);
        return await SpeechGetAsync(client, JsonNode.Parse(response)!["id"]!.GetValue<string>(), args, response);
    }

    private static async Task<int> SpeechGetAsync(HostClient client, string id, Args args, string? first = null)
    {
        if (!args.Has("wait") && first is null) return await client.SendAsync(HttpMethod.Get, $"/api/speech/{Uri.EscapeDataString(id)}");
        var deadline = DateTime.UtcNow.AddSeconds(args.Int("timeout") ?? 300);
        var body = first;
        var status = HttpStatusCode.OK;
        while (true)
        {
            if (body is not null && JsonNode.Parse(body)?["state"]?.GetValue<string>() is "finished" or "interrupted" or "withdrawn")
                return client.Print(status, body);
            if (DateTime.UtcNow >= deadline) { client.Print(status, body ?? "{}"); return ExitCodes.TimedOut; }
            (status, body) = await client.RawAsync(HttpMethod.Get, $"/api/speech/{Uri.EscapeDataString(id)}?wait={HostApp.MaxWait.TotalSeconds}");
            if (status != HttpStatusCode.OK) return client.Print(status, body);
        }
    }

    private static async Task<int> EventsAsync(HostClient client, Args args)
    {
        var after = args.Int("after") ?? 0;
        if (!args.Has("wait")) return await client.SendAsync(HttpMethod.Get, $"/api/events{Query(("after", after))}");
        var deadline = DateTime.UtcNow.AddSeconds(args.Int("timeout") ?? 1800);
        while (true)
        {
            var (status, body) = await client.RawAsync(HttpMethod.Get, $"/api/events{Query(("after", after), ("wait", HostApp.MaxWait.TotalSeconds))}");
            if (status != HttpStatusCode.OK || JsonNode.Parse(body)!.AsArray().Count > 0) return client.Print(status, body);
            if (DateTime.UtcNow >= deadline) { client.Print(status, body); return ExitCodes.TimedOut; }
        }
    }

    private static async Task<int> ModelAsync(string verb)
    {
        var config = Profile.LoadConfig();
        using var factory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
        var store = new ModelStore(Profile.ModelsDir, config.Stt, config.Tts, factory.CreateLogger("bspeak"));
        switch (verb)
        {
            case "list":
                Console.WriteLine(Json.Of(store.List()));
                return ExitCodes.Ok;
            case "pull":
                await store.EnsureAsync();
                Console.WriteLine(Json.Of(store.List()));
                return ExitCodes.Ok;
            default:
                throw new UsageException("Use 'model list' or 'model pull'.");
        }
    }

    private static async Task<int> StopHostAsync()
    {
        if (Profile.ReadHostInfo() is not { } info || !await HostApp.IsAliveAsync(info.Port))
        {
            Console.WriteLine(Json.Of(new { stopping = false, reason = "not running" }));
            return ExitCodes.Ok;
        }
        using var http = new HttpClient();
        await http.DeleteAsync($"http://127.0.0.1:{info.Port}/api/host");
        for (var i = 0; i < 50 && await HostApp.IsAliveAsync(info.Port); i++) await Task.Delay(100);
        Console.WriteLine(Json.Of(new { stopping = true, pid = info.Pid }));
        return ExitCodes.Ok;
    }
}

/// <summary>Talks to the running host, starting it in the background first if needed.</summary>
public sealed class HostClient
{
    private readonly HttpClient _http;

    private HostClient(int port) =>
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = HostApp.MaxWait + TimeSpan.FromSeconds(15) };

    public static async Task<HostClient> ConnectAsync()
    {
        if (Profile.ReadHostInfo() is { } info && await HostApp.IsAliveAsync(info.Port)) return new HostClient(info.Port);
        StartHost();
        for (var i = 0; i < 300; i++)
        {
            await Task.Delay(100);
            if (Profile.ReadHostInfo() is { } started && await HostApp.IsAliveAsync(started.Port)) return new HostClient(started.Port);
        }
        throw new HostUnavailableException($"The b-speak host did not start. See {Profile.LogPath}.");
    }

    private static void StartHost()
    {
        var exe = Environment.ProcessPath ?? throw new HostUnavailableException("Cannot find the bspeak executable to start the host.");
        var viaDotnet = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var arguments = viaDotnet ? $"\"{Assembly.GetEntryAssembly()!.Location}\" serve --background" : "serve --background";
        Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Profile.Home });
    }

    public async Task<(HttpStatusCode Status, string Body)> RawAsync(HttpMethod method, string path, object? body = null)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body, options: Profile.Json);
            using var response = await _http.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new HostUnavailableException($"Lost the b-speak host ({ex.Message}). If it restarted, join again.");
        }
    }

    public async Task<int> SendAsync(HttpMethod method, string path, object? body = null)
    {
        var (status, response) = await RawAsync(method, path, body);
        return Print(status, response);
    }

    public async Task<int> TextAsync(string path)
    {
        var (status, response) = await RawAsync(HttpMethod.Get, path);
        if (status != HttpStatusCode.OK) return Print(status, response);
        Console.WriteLine(response);
        return ExitCodes.Ok;
    }

    public int Print(HttpStatusCode status, string body)
    {
        Console.WriteLine(Json.Pretty(body));
        return status switch
        {
            HttpStatusCode.OK => ExitCodes.Ok,
            HttpStatusCode.UnprocessableEntity => ExitCodes.Rule,
            HttpStatusCode.NotFound => ExitCodes.NotFound,
            HttpStatusCode.BadRequest => ExitCodes.Usage,
            _ => ExitCodes.HostUnavailable,
        };
    }
}
