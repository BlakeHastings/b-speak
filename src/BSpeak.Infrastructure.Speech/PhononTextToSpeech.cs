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
public sealed class PhononTextToSpeech(string exe, string modelDir, PhononOptions options) : ITextToSpeech
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public int SampleRate => 24_000;

    /// <summary>Null when the prebuilt ptts can run here; otherwise the reason it cannot.</summary>
    public static string? CheckSupported(PhononOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Path)) return File.Exists(options.Path) ? null : $"tts.phonon.path '{options.Path}' does not exist.";
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return "no prebuilt ptts download for this platform; build it from github.com/gradium-ai/xn-ptts and set tts.phonon.path.";
        if (!Avx2.IsSupported || !Fma.IsSupported)
            return "this CPU lacks AVX2/FMA, which the prebuilt ptts needs.";
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
