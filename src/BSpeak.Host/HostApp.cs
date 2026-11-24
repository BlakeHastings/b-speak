using System.Text.Json;
using BSpeak.Application;
using BSpeak.Domain;
using BSpeak.Infrastructure.Audio;
using BSpeak.Infrastructure.Speech;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BSpeak.Host;

public sealed record JoinRequest(string? Name);
public sealed record UtteranceRequest(string Text);
public sealed record SpeechRequest(string As, string Text, int? ReplyTo);
public sealed record ListeningRequest(bool On, string? As);
public sealed record HandoffRequest(string As, string Text, int? UtteranceId);
public sealed record HandoffPatch(HandoffStatus Status, string? Note, string? By);
public sealed record ModeRequest(ListeningMode Mode, string? WakeWord);
public sealed record ParticipantPatch(string? Voice);
public sealed record VolumeRequest(int? Volume, string? Step);
public sealed record ErrorBody(string Code, string Message);
public sealed record EventView(long Seq, DateTimeOffset At, string Type, string Stream, JsonElement Data);
public sealed record HostStatus(string Version, int Pid, DateTimeOffset StartedAt, string Profile, UpdateStatus? Update, AudioStatus Audio, RoomStatus Room);

/// <summary>
/// The resident host: one per machine. Owns the room, the audio runtime and a localhost HTTP API that
/// the CLI (and later an MCP server) talks to. Mirrors the CLI's resources: <c>bspeak speech post</c>
/// is <c>POST /api/speech</c>.
/// </summary>
public static class HostApp
{
    /// <summary>Longest a single HTTP wait is held open. The CLI repeats waits to cover longer timeouts.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(50);

    private static UpdateStatus? _update;
    private static Task _updateCheck = Task.CompletedTask;

    public static async Task<int> RunAsync(bool forceNoAudio, int? port, bool quiet, CancellationToken ct = default)
    {
        var config = Profile.LoadConfig();
        if (forceNoAudio) config.Audio.Enabled = false;

        if (Profile.ReadHostInfo() is { } existing && await IsAliveAsync(existing.Port))
        {
            Console.Error.WriteLine($"b-speak is already running (pid {existing.Pid}, port {existing.Port}).");
            return 1;
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        if (!quiet) builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
        builder.Logging.AddProvider(new FileLoggerProvider(Profile.LogPath));
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port ?? config.Port}");
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = Profile.Json.PropertyNamingPolicy;
            o.SerializerOptions.Encoder = Profile.Json.Encoder;
            foreach (var c in Profile.Json.Converters) o.SerializerOptions.Converters.Add(c);
        });

        var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("bspeak");
        var router = new PlaybackRouter();
        var fallback = new SimulatedPlayback(logger);
        var log = new InMemoryEventLog();
        var room = new RoomService(config.ToRoomSettings(), config.ToSpeechRules(), new SystemClock(), log, router);
        fallback.Attach(room);
        var audio = new AudioRuntime(config, room, router, fallback, logger);
        var startedAt = DateTimeOffset.Now;

        app.Use(async (ctx, next) =>
        {
            try { await next(ctx); }
            catch (DomainViolation v) { await Error(ctx, 422, v.Code, v.Message); }
            catch (NotFoundException n) { await Error(ctx, 404, n.Code, n.Message); }
            catch (BadHttpRequestException b) { await Error(ctx, 400, "bad-request", b.Message); }
            catch (JsonException j) { await Error(ctx, 400, "bad-request", j.Message); }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
        });

        MapApi(app, room, log, audio, config, startedAt);

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, app.Lifetime.ApplicationStopping);
        await app.StartAsync(ct);
        var actualPort = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
        Profile.WriteHostInfo(new HostInfo(actualPort, Environment.ProcessId, startedAt));
        logger.LogInformation("[host] b-speak listening on http://127.0.0.1:{Port} (pid {Pid}); log at {Log}", actualPort, Environment.ProcessId, Profile.LogPath);

        var audioTask = audio.RunAsync(stopping.Token);
        var tickTask = TickAsync(room, stopping.Token);
        if (config.Updates.Check) StartUpdateCheck(config, logger, stopping.Token);
        try { await app.WaitForShutdownAsync(ct); }
        finally
        {
            stopping.Cancel();
            await Task.WhenAll(audioTask, tickTask).WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
            Profile.ClearHostInfo(Environment.ProcessId);
        }
        return 0;
    }

    private static void StartUpdateCheck(BSpeakConfig config, ILogger logger, CancellationToken ct) =>
        _updateCheck = Task.Run(async () =>
        {
            _update = await Updates.CheckAsync(config.Updates, ct: ct);
            if (_update.Available) logger.LogInformation("[host] b-speak {Latest} is available (this is {Current})", _update.Latest, _update.Current);
        });

    private static async Task TickAsync(RoomService room, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try { while (await timer.WaitForNextTickAsync(ct)) room.Tick(); }
        catch (OperationCanceledException) { }
    }

    private static void MapApi(WebApplication app, RoomService room, IEventLog log, AudioRuntime audio, BSpeakConfig config, DateTimeOffset startedAt)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", () => Results.Ok(new { ok = true, pid = Environment.ProcessId }));

        api.MapGet("/host", () => new HostStatus(Updates.CurrentVersion, Environment.ProcessId, startedAt, Profile.Home, _update, audio.Status, room.Status()));
        api.MapDelete("/host", (IHostApplicationLifetime life) =>
        {
            _ = Task.Run(async () => { await Task.Delay(200); life.StopApplication(); });
            return Results.Ok(new { stopping = true });
        });

        api.MapGet("/brief", async (string? @as) =>
        {
            if (config.Updates.Check && _updateCheck.IsCompleted && (_update?.CheckedAt is not { } at || DateTimeOffset.UtcNow - at > TimeSpan.FromDays(1)))
                StartUpdateCheck(config, app.Logger, CancellationToken.None); // long-running hosts recheck daily
            await Task.WhenAny(_updateCheck, Task.Delay(3000)); // the first brief after a start waits briefly for the check
            return Results.Text(Brief.Render(room.Status(), @as, _update is { } u ? Updates.Notice(u) : null, Preferences.Load().Volume), "text/markdown; charset=utf-8");
        });

        api.MapPost("/participants", (JoinRequest? body) => room.Join(body?.Name, name => Preferences.Load().Voices.GetValueOrDefault(name)));
        api.MapGet("/participants", () => room.Status().Participants);
        api.MapDelete("/participants/{name}", (string name) => { room.Leave(name); return Results.Ok(new { left = name }); });
        api.MapPatch("/participants/{name}", (string name, ParticipantPatch body) =>
        {
            if (body.Voice is null) throw new BadHttpRequestException("Nothing to change: pass voice.");
            var changed = room.ChangeVoice(name, body.Voice, audio.Voices);
            var prefs = Preferences.Load();
            prefs.Voices[changed.Name] = changed.Voice;
            prefs.Save();
            return changed;
        });

        api.MapGet("/voices", async () =>
        {
            for (var i = 0; i < 50 && audio.TtsProvider is null && audio.Status.State is not ("off" or "failed"); i++) await Task.Delay(100); // a just-started host may still be loading its voice
            var used = room.Status().Participants.ToDictionary(p => p.Voice, p => p.Name, StringComparer.OrdinalIgnoreCase);
            var voices = audio.Voices ?? config.Tts.RoomVoices;
            var provider = audio.TtsProvider ?? (audio.Status.State == "off" ? "none (audio is off)" : "starting");
            return new { provider, voices = voices.Select(v => new { name = v, usedBy = used.GetValueOrDefault(v) }) };
        });

        api.MapGet("/volume", () => new { volume = Preferences.Load().Volume });
        api.MapPut("/volume", (VolumeRequest body) =>
        {
            var prefs = Preferences.Load();
            prefs.Volume = Math.Clamp(body.Step?.ToLowerInvariant() switch
            {
                "up" => prefs.Volume + 15,
                "down" => prefs.Volume - 15,
                null => body.Volume ?? throw new BadHttpRequestException("Pass volume (0 to 100) or step (up or down)."),
                _ => throw new BadHttpRequestException("step must be up or down."),
            }, 0, 100);
            prefs.Save();
            if (audio.Render is { } render) render.Volume = prefs.Volume / 100f;
            return new { volume = prefs.Volume };
        });

        api.MapGet("/utterances", async (string @as, int? after, bool? forYou, double? wait, HttpContext ctx) =>
        {
            if (wait is not > 0) return new UtteranceBatch(room.ListUtterances(@as, after ?? 0, forYou ?? false), TimedOut: false);
            return await room.WaitForUtterancesAsync(@as, after ?? 0, forYou ?? false, Clamp(wait.Value), ctx.RequestAborted);
        });
        api.MapPost("/utterances", (UtteranceRequest body) =>
        {
            var before = room.Status().Utterances;
            room.SpeechOnset();
            room.Utterance(body.Text);
            var status = room.Status();
            return Results.Ok(new { delivered = status.Utterances > before, utteranceId = status.Utterances > before ? status.Utterances : (int?)null, listening = status.Listening });
        });

        api.MapPost("/speech", async (SpeechRequest body, double? wait, HttpContext ctx) =>
        {
            var speech = room.PostSpeech(body.As, body.Text, body.ReplyTo);
            if (wait is not > 0) return speech;
            return await room.WaitForSpeechAsync(speech.Id, Clamp(wait.Value), ctx.RequestAborted) ?? room.GetSpeech(speech.Id);
        });
        api.MapGet("/speech", (string? @as) => room.ListSpeech(@as));
        api.MapGet("/speech/{id}", async (string id, double? wait, HttpContext ctx) =>
            wait is > 0 ? await room.WaitForSpeechAsync(id, Clamp(wait.Value), ctx.RequestAborted) ?? room.GetSpeech(id) : room.GetSpeech(id));
        api.MapDelete("/speech/{id}", (string id, string @as) => { room.WithdrawSpeech(@as, id); return room.GetSpeech(id); });

        api.MapGet("/inbox", async (string @as, long? after, double? wait, HttpContext ctx) =>
            wait is > 0
                ? await room.WaitForInboxAsync(@as, after ?? 0, Clamp(wait.Value), ctx.RequestAborted)
                : room.Inbox(@as, after ?? 0));

        api.MapPost("/handoffs", (HandoffRequest body) => room.PostHandoff(body.As, body.Text, body.UtteranceId));
        api.MapGet("/handoffs", async (string? status, double? wait, HttpContext ctx) =>
        {
            HandoffStatus? filter = status is null ? null
                : Enum.TryParse<HandoffStatus>(status, ignoreCase: true, out var s) ? s
                : throw new BadHttpRequestException("status must be open, working, done or failed.");
            return wait is > 0 && filter == HandoffStatus.Open
                ? await room.WaitForOpenHandoffsAsync(Clamp(wait.Value), ctx.RequestAborted)
                : room.ListHandoffs(filter);
        });
        api.MapGet("/handoffs/{id}", (string id) => room.GetHandoff(id));
        api.MapPatch("/handoffs/{id}", (string id, HandoffPatch body) => room.UpdateHandoff(id, body.By, body.Status, body.Note));

        api.MapGet("/listening", () => Listening(room));
        api.MapPut("/listening", (ListeningRequest body) => { room.SetListening(body.On, body.As); return Listening(room); });
        api.MapPatch("/listening", (ModeRequest body) => { room.SetMode(body.Mode, body.WakeWord); return Listening(room); });

        api.MapGet("/events", async (long? after, double? wait, HttpContext ctx) =>
        {
            var from = after ?? 0;
            if (wait is > 0 && log.Head <= from)
                await Task.WhenAny(log.Changed, Task.Delay(Clamp(wait.Value), ctx.RequestAborted));
            return log.After(from).Select(e => new EventView(e.Seq, e.At, e.Event.Type, e.Event.Stream,
                JsonSerializer.SerializeToElement(e.Event, e.Event.GetType(), Profile.Json)));
        });

        api.MapGet("/devices", () => PortAudioSystem.List());
        api.MapGet("/models", () => new ModelStore(Profile.ModelsDir, config.Stt, config.Tts, app.Logger).List());
    }

    private static object Listening(RoomService room)
    {
        var s = room.Status();
        return new { listening = s.Listening, mode = s.Mode, wakeWord = s.WakeWord };
    }

    private static TimeSpan Clamp(double seconds) => TimeSpan.FromSeconds(Math.Min(seconds, MaxWait.TotalSeconds));

    private static Task Error(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { error = new ErrorBody(code, message) }, Profile.Json);
    }

    public static async Task<bool> IsAliveAsync(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            return (await http.GetAsync($"http://127.0.0.1:{port}/api/health")).IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
