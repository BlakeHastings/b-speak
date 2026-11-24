using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BSpeak.Infrastructure.Speech;

/// <summary>
/// Runs Gradium's ptts-openai-server in Docker, the way Gradium ships it. Reuses the container when it is
/// already there (it is left running between hosts so the next start is instant), starts it if stopped,
/// and creates it otherwise, with a volume caching the model. Throws with the reason when Docker is not
/// available, so the provider chain steps down.
/// </summary>
public static class PhononServer
{
    public static async Task<string> EnsureRunningAsync(PhononServerOptions o, HttpClient http, ILogger logger, CancellationToken ct)
    {
        var endpoint = $"http://127.0.0.1:{o.Port}";
        if (await HealthyAsync(http, endpoint, ct)) return endpoint + "/v1";

        var (infoCode, _) = await DockerAsync(["info", "--format", "{{.ServerVersion}}"], TimeSpan.FromSeconds(20), ct);
        if (infoCode != 0) throw new InvalidOperationException("Docker is not running (or not installed).");

        var (inspectCode, running) = await DockerAsync(["inspect", "-f", "{{.State.Running}}", o.Container], TimeSpan.FromSeconds(20), ct);
        if (inspectCode == 0 && running.Trim() == "false")
        {
            logger.LogInformation("[tts] starting Docker container {Container}", o.Container);
            await RequireAsync(["start", o.Container], TimeSpan.FromMinutes(1), ct);
        }
        else if (inspectCode != 0)
        {
            logger.LogInformation("[tts] creating Docker container {Container} from {Image} (the first start downloads the model)", o.Container, o.Image);
            await RequireAsync([
                "run", "-d", "--name", o.Container,
                "-p", $"127.0.0.1:{o.Port}:8880",
                "-e", "PTTS_LANG=en", "-e", $"PTTS_QUANT={o.Quant}",
                "-v", $"{o.Container}-cache:/home/phonon/.cache/huggingface",
                o.Image,
            ], TimeSpan.FromMinutes(10), ct);
        }

        var deadline = DateTime.UtcNow.AddSeconds(o.StartTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await HealthyAsync(http, endpoint, ct)) return endpoint + "/v1";
            await Task.Delay(1000, ct);
        }
        throw new TimeoutException($"{o.Container} did not become healthy within {o.StartTimeoutSeconds} s. See: docker logs {o.Container}");
    }

    private static async Task<bool> HealthyAsync(HttpClient http, string endpoint, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            return (await http.GetAsync(endpoint + "/health", cts.Token)).IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { return false; }
    }

    private static async Task RequireAsync(string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var (code, output) = await DockerAsync(args, timeout, ct);
        if (code != 0) throw new InvalidOperationException($"docker {args[0]} failed: {output.Trim()}");
    }

    private static async Task<(int Code, string Output)> DockerAsync(string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process process;
        try { process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start docker."); }
        catch (System.ComponentModel.Win32Exception) { return (-1, "docker is not installed"); }
        using (process)
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            try { await process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(); } catch { }
                return (-1, $"docker {args[0]} timed out");
            }
            return (process.ExitCode, (await stdout) + (await stderr));
        }
    }
}
