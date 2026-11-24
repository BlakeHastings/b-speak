using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using BSpeak.Application;

namespace BSpeak.Infrastructure.Speech;

/// <summary>
/// Phonon through Gradium's own <c>ptts</c> runner, one process per sentence. Uses their tool as shipped
/// rather than reimplementing the model. Their prebuilt desktop binaries are CPU-only and need an
/// x86-64-v3 CPU (AVX2, FMA, F16C); <see cref="CheckSupported"/> says why it cannot run elsewhere so the
/// provider chain can step down. Catalog voices are fetched by ptts on first use.
/// </summary>
public sealed class PhononTextToSpeech(string exe, string modelDir, PhononOptions options) : ITextToSpeech, IVoiceCatalog
{
    /// <summary>English voices from <c>ptts --list-voices</c> (Gradium's catalog), or the configured list if that fails.</summary>
    public async Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--list-voices");
            using var process = Process.Start(psi)!;
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            _ = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var voices = output.Split('\n')
                .Select(l => l.Split('\t'))
                .Where(c => c.Length >= 2 && c[1].Trim() == "en" && c[0].Trim().Length > 0)
                .Select(c => c[0].Trim()).ToList();
            if (voices.Count > 0) return voices;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        return options.Voices.Count > 0 ? options.Voices : PhononOptions.DefaultVoices;
    }

    private readonly SemaphoreSlim _gate = new(1, 1);

    public int SampleRate => 24_000;

    /// <summary>Null when the prebuilt ptts can run here; otherwise the reason it cannot.</summary>
    public static string? CheckSupported(PhononOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Path)) return File.Exists(options.Path) ? null : $"tts.phonon.path '{options.Path}' does not exist.";
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return "Gradium publishes no ready-made ptts for this platform, so this voice is skipped and the next one is used (expected; to use it anyway, build ptts from github.com/gradium-ai/xn-ptts and set tts.phonon.path).";
        if (!Avx2.IsSupported || !Fma.IsSupported)
            return "this CPU lacks AVX2/FMA, which the prebuilt ptts needs, so this voice is skipped and the next one is used.";
        return null;
    }

    public async Task<float[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        var output = Path.Combine(Path.GetTempPath(), $"bspeak-phonon-{Guid.NewGuid():N}.wav");
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "--model", modelDir, "--lang", "en", "--voice", voice, "--temperature", options.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture), "-o", output })
                psi.ArgumentList.Add(arg);
            if (options.Threads > 0) { psi.ArgumentList.Add("--threads"); psi.ArgumentList.Add(options.Threads.ToString()); }
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(text);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ptts.");
            var stderr = process.StandardError.ReadToEndAsync(ct);
            _ = process.StandardOutput.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { try { process.Kill(); } catch { } throw; }
            if (process.ExitCode != 0)
            {
                var err = (await stderr).Trim();
                throw new InvalidOperationException($"ptts failed ({process.ExitCode}): {(err.Length > 400 ? err[^400..] : err)}");
            }
            return WavAudio.Decode(await File.ReadAllBytesAsync(output, ct)).Samples;
        }
        finally
        {
            _gate.Release();
            try { File.Delete(output); } catch { }
        }
    }
}
