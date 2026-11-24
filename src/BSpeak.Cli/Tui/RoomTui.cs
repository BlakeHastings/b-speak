using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BSpeak.Host;

namespace BSpeak.Cli.Tui;

/// <summary>
/// <c>bspeak room tui</c>: a live view of the room. Replays the host's event log into a
/// <see cref="RoomScreen"/>, follows it with long polls, and redraws a few times a second.
/// Never starts a host; it waits for one and starts over when the host restarts.
/// </summary>
public static class RoomTui
{
    /// <summary>Prints one frame as plain text and exits: for agents, scripts, and terminals without ANSI.</summary>
    public static async Task<int> OnceAsync(int width = 100, int height = 30)
    {
        var client = await HostClient.TryExistingAsync() ?? throw new HostUnavailableException("The b-speak host is not running.");
        var screen = new RoomScreen { Volume = Preferences.Load().Volume };
        while (true)
        {
            var (code, body) = await client.RawAsync(HttpMethod.Get, $"/api/events?after={screen.Cursor}");
            if (code != HttpStatusCode.OK) return client.Print(code, body);
            using var events = JsonDocument.Parse(body);
            if (events.RootElement.GetArrayLength() == 0) break;
            foreach (var e in events.RootElement.EnumerateArray())
                screen.Apply(e.GetProperty("seq").GetInt64(), e.GetProperty("at").GetDateTimeOffset(), e.GetProperty("type").GetString()!, e.GetProperty("data"));
        }
        var (hostCode, hostBody) = await client.RawAsync(HttpMethod.Get, "/api/host");
        if (hostCode != HttpStatusCode.OK) return client.Print(hostCode, hostBody);
        using (var host = JsonDocument.Parse(hostBody)) screen.Sync(host.RootElement);
        var text = screen.Render(DateTimeOffset.Now, width, height).SkipLast(1).Select(r => r.Plain.TrimEnd()).ToList();
        while (text.Count > 0 && text[^1].Length == 0) text.RemoveAt(text.Count - 1);
        text.ForEach(Console.WriteLine);
        return ExitCodes.Ok;
    }

    public static async Task<int> RunAsync()
    {
        if (Console.IsOutputRedirected || Console.IsInputRedirected)
            throw new UsageException("bspeak room tui needs an interactive terminal (or pass --once for a single plain-text frame).");
        EnableAnsi();

        var screen = new RoomScreen { Disconnected = "Looking for the b-speak host..." };
        var gate = new object();
        var dirty = new SemaphoreSlim(0);
        using var stop = new CancellationTokenSource();
        HostClient? client = null;

        Console.TreatControlCAsInput = true;
        Console.Write("\x1b[?1049h\x1b[?25l\x1b[2J");
        try
        {
            var follow = FollowAsync(screen, gate, dirty, c => client = c, stop.Token);
            var keys = KeysAsync(screen, gate, dirty, () => client, stop);
            var lastSize = (0, 0);
            while (!stop.IsCancellationRequested)
            {
                var size = (Console.WindowWidth, Console.WindowHeight);
                if (size != lastSize) { Console.Write("\x1b[2J"); lastSize = size; }
                List<Line> rows;
                lock (gate)
                {
                    screen.Volume = Preferences.Load().Volume;
                    rows = screen.Render(DateTimeOffset.Now, size.Item1, size.Item2);
                }
                Draw(rows, size.Item1, size.Item2);
                try { await dirty.WaitAsync(TimeSpan.FromMilliseconds(140), stop.Token); }
                catch (OperationCanceledException) { }
            }
            await Task.WhenAll(follow, keys).WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { });
        }
        finally
        {
            Console.Write("\x1b[0m\x1b[?25h\x1b[?1049l");
            Console.TreatControlCAsInput = false;
        }
        return ExitCodes.Ok;
    }

    /// <summary>Connects, replays the log, then follows it. Starts over from nothing if the host restarts.</summary>
    private static async Task FollowAsync(RoomScreen screen, object gate, SemaphoreSlim dirty, Action<HostClient?> connected, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var client = await HostClient.TryExistingAsync();
            if (client is null)
            {
                lock (gate) screen.Disconnected = "The b-speak host is not running. It starts with any bspeak command, for example when an agent joins.";
                dirty.Release();
                await Delay(TimeSpan.FromSeconds(1), ct);
                continue;
            }

            int? pid = null;
            try
            {
                lock (gate) screen.Clear();
                connected(client);
                while (!ct.IsCancellationRequested)
                {
                    var (hostCode, hostBody) = await client.RawAsync(HttpMethod.Get, "/api/host");
                    if (hostCode != HttpStatusCode.OK) break;
                    using var host = JsonDocument.Parse(hostBody);
                    var hostPid = host.RootElement.GetProperty("pid").GetInt32();
                    if (pid is { } known && known != hostPid) break; // a different host: start over
                    pid = hostPid;

                    long cursor;
                    lock (gate) { screen.Disconnected = null; screen.Sync(host.RootElement); cursor = screen.Cursor; }
                    dirty.Release();

                    var (code, body) = await client.RawAsync(HttpMethod.Get, $"/api/events?after={cursor}&wait=1");
                    if (code != HttpStatusCode.OK) break;
                    using var events = JsonDocument.Parse(body);
                    lock (gate)
                    {
                        foreach (var e in events.RootElement.EnumerateArray())
                            screen.Apply(e.GetProperty("seq").GetInt64(), e.GetProperty("at").GetDateTimeOffset(), e.GetProperty("type").GetString()!, e.GetProperty("data"));
                    }
                    dirty.Release();
                }
            }
            catch (HostUnavailableException) { }
            catch (JsonException) { }
            connected(null);
        }
    }

    private static async Task KeysAsync(RoomScreen screen, object gate, SemaphoreSlim dirty, Func<HostClient?> client, CancellationTokenSource stop)
    {
        while (!stop.IsCancellationRequested)
        {
            if (!Console.KeyAvailable) { await Delay(TimeSpan.FromMilliseconds(40), stop.Token); continue; }
            var key = Console.ReadKey(intercept: true);
            if (key.Key is ConsoleKey.Q or ConsoleKey.Escape || (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
            {
                stop.Cancel();
                dirty.Release();
                return;
            }
            if (client() is not { } c) continue;
            try
            {
                switch (key.KeyChar)
                {
                    case ' ':
                        bool on;
                        lock (gate) on = !screen.Listening;
                        await c.RawAsync(HttpMethod.Put, "/api/listening", new { on, @as = "the room view" });
                        break;
                    case '+' or '=':
                        await c.RawAsync(HttpMethod.Put, "/api/volume", new { step = "up" });
                        break;
                    case '-' or '_':
                        await c.RawAsync(HttpMethod.Put, "/api/volume", new { step = "down" });
                        break;
                }
            }
            catch (HostUnavailableException) { }
            dirty.Release();
        }
    }

    private static async Task Delay(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }

    /// <summary>Writes a whole frame at once, each row cut to the window width so nothing wraps or scrolls.</summary>
    private static void Draw(List<Line> rows, int width, int height)
    {
        var sb = new StringBuilder("\x1b[H");
        var count = Math.Min(rows.Count, Math.Max(1, height - 1));
        for (var r = 0; r < count; r++)
        {
            var left = Math.Max(0, width - 1);
            foreach (var seg in rows[r].Segs)
            {
                if (left <= 0) break;
                var text = seg.Text.Length > left ? seg.Text[..left] : seg.Text;
                left -= text.Length;
                if (seg.Style is null) sb.Append(text);
                else sb.Append("\x1b[").Append(seg.Style).Append('m').Append(text).Append("\x1b[0m");
            }
            sb.Append("\x1b[K");
            if (r < count - 1) sb.Append("\r\n");
        }
        sb.Append("\x1b[J");
        Console.Out.Write(sb.ToString());
        Console.Out.Flush();
    }

    private static void EnableAnsi()
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = GetStdHandle(-11);
        if (GetConsoleMode(handle, out var mode)) SetConsoleMode(handle, mode | 0x0004 /* ENABLE_VIRTUAL_TERMINAL_PROCESSING */);
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(IntPtr handle, uint mode);
}
