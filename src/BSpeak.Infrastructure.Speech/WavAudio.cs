using System.Text;

namespace BSpeak.Infrastructure.Speech;

/// <summary>Decodes WAV bytes (16-bit PCM or 32-bit float, any channel count) to mono float PCM, and encodes 16-bit WAV.</summary>
public static class WavAudio
{
    public static (float[] Samples, int SampleRate) Decode(byte[] wav)
    {
        if (wav.Length < 12 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF")
            return (DecodePcm16(wav), 0);

        int pos = 12;
        short channels = 1, bits = 16, format = 1;
        int sampleRate = 24_000;
        while (pos + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, pos, 4);
            var size = BitConverter.ToInt32(wav, pos + 4);
            var body = pos + 8;
            if (id == "fmt ")
            {
                format = BitConverter.ToInt16(wav, body);
                channels = BitConverter.ToInt16(wav, body + 2);
                sampleRate = BitConverter.ToInt32(wav, body + 4);
                bits = BitConverter.ToInt16(wav, body + 14);
                if (format == -2 && size >= 26) format = BitConverter.ToInt16(wav, body + 24); // WAVE_FORMAT_EXTENSIBLE
            }
            else if (id == "data")
            {
                // A streaming writer may leave a 0 or oversized length: read to the end of the buffer.
                var len = size <= 0 ? wav.Length - body : Math.Min(size, wav.Length - body);
                return (DecodeSamples(wav, body, len, channels, bits, format), sampleRate);
            }
            pos = body + size + (size & 1);
        }
        throw new InvalidDataException("WAV has no data chunk.");
    }

    public static float[] DecodePcm16(byte[] pcm)
    {
        var samples = new float[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8)) / 32768f;
        return samples;
    }

    private static float[] DecodeSamples(byte[] data, int offset, int length, short channels, short bits, short format)
    {
        var ch = Math.Max((int)channels, 1);
        var bytesPerSample = Math.Max(bits / 8, 1);
        var frames = length / bytesPerSample / ch;
        var samples = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            var acc = 0f;
            for (var c = 0; c < ch; c++)
            {
                var p = offset + (f * ch + c) * bytesPerSample;
                acc += format == 3 ? BitConverter.ToSingle(data, p) : (short)(data[p] | (data[p + 1] << 8)) / 32768f;
            }
            samples[f] = acc / ch;
        }
        return samples;
    }

    public static byte[] EncodePcm16(ReadOnlySpan<float> samples, int sampleRate)
    {
        using var ms = new MemoryStream(44 + samples.Length * 2);
        using var bw = new BinaryWriter(ms);
        var dataBytes = samples.Length * 2;
        bw.Write("RIFF"u8.ToArray());
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8.ToArray());
        bw.Write("fmt "u8.ToArray());
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write("data"u8.ToArray());
        bw.Write(dataBytes);
        foreach (var s in samples)
            bw.Write((short)Math.Clamp(s * 32767f, short.MinValue, short.MaxValue));
        bw.Flush();
        return ms.ToArray();
    }
}
