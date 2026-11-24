using BSpeak.Application;
using Microsoft.Extensions.Logging;

namespace BSpeak.Host;

/// <summary>
/// Holds playback requests until the real audio side is ready, then forwards to it. Lets agents speak
/// while models are still downloading: their speech plays as soon as audio comes up.
/// </summary>
public sealed class PlaybackRouter : IPlayback
{
    private readonly object _gate = new();
    private readonly List<Action<IPlayback>> _pending = [];
    private IPlayback? _target;

    public void Use(IPlayback target)
    {
        List<Action<IPlayback>> replay;
        lock (_gate)
        {
            _target = target;
            replay = [.. _pending];
            _pending.Clear();
        }
        foreach (var call in replay) call(target);
    }

    public void Play(PlaybackRequest request) => Route(p => p.Play(request));
    public void Stop(string speechId) => Route(p => p.Stop(speechId));
    public void PlayEarcon(Earcon earcon) => Route(p => p.PlayEarcon(earcon));

    private void Route(Action<IPlayback> call)
    {
        IPlayback? target;
        lock (_gate)
        {
            target = _target;
            if (target is null) { _pending.Add(call); return; }
        }
        call(target);
    }
}

/// <summary>
/// Playback without devices: logs each speech and reports it as played after roughly the time it would
/// take to say. Used with audio disabled, and as the fallback when audio fails to start.
/// </summary>
public sealed class SimulatedPlayback(ILogger logger) : IPlayback
{
    private readonly Dictionary<string, CancellationTokenSource> _active = [];
    private RoomService? _room;

    public void Attach(RoomService room) => _room = room;

    public void Play(PlaybackRequest request)
    {
        var cts = new CancellationTokenSource();
        lock (_active) _active[request.SpeechId] = cts;
        _ = Task.Run(async () =>
        {
            var room = _room!;
            try
            {
                await Task.Delay(100, cts.Token);
                room.PlaybackStarted(request.SpeechId);
                for (var i = 0; i < request.Sentences.Count; i++)
                {
                    var sentence = request.Sentences[i];
                    logger.LogInformation("[speaks] ({Voice}) {Text}", request.Voice, sentence);
                    var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(400, words * 280)), cts.Token);
                    room.SentencePlayed(request.SpeechId, i);
                }
                room.PlaybackDrained(request.SpeechId);
            }
            catch (OperationCanceledException) { }
            finally { lock (_active) _active.Remove(request.SpeechId); }
        });
    }

    public void Stop(string speechId)
    {
        lock (_active) if (_active.Remove(speechId, out var cts)) cts.Cancel();
    }

    public void PlayEarcon(Earcon earcon) => logger.LogInformation("[earcon] {Earcon}", earcon);
}

/// <summary>Appends log lines to a file (~/.bspeak/host.log), so a background host can be diagnosed.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public FileLoggerProvider(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this);

    private void Write(LogLevel level, string message, Exception? ex)
    {
        lock (_gate)
        {
            _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level.ToString()[..4].ToUpperInvariant()} {message}");
            if (ex is not null) _writer.WriteLine(ex);
        }
    }

    public void Dispose() => _writer.Dispose();

    private sealed class FileLogger(FileLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) owner.Write(logLevel, formatter(state, exception), exception);
        }
    }
}
