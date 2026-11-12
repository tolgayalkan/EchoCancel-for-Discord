using EchoCancel.Core;

static class Wav
{
    /// <summary>PCM16/24/32 ya da float32 WAV dosyasını 48 kHz mono float olarak okur.</summary>
    public static float[] Read48kMono(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        if (b.Length < 44 || b[0] != 'R' || b[8] != 'W') throw new InvalidDataException($"{path}: WAV değil.");
        int pos = 12, fmtTag = 1, ch = 1, rate = 48000, bits = 16; int dataPos = -1, dataLen = 0;
        while (pos + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            int size = BitConverter.ToInt32(b, pos + 4);
            if (id == "fmt ")
            {
                fmtTag = BitConverter.ToUInt16(b, pos + 8); ch = BitConverter.ToUInt16(b, pos + 10);
                rate = BitConverter.ToInt32(b, pos + 12); bits = BitConverter.ToUInt16(b, pos + 22);
                if (fmtTag == 0xFFFE && size >= 26) fmtTag = BitConverter.ToUInt16(b, pos + 32);
            }
            else if (id == "data") { dataPos = pos + 8; dataLen = Math.Min(size < 0 ? int.MaxValue : size, b.Length - dataPos); break; }
            pos += 8 + size + (size & 1);
        }
        if (dataPos < 0) throw new InvalidDataException($"{path}: veri bölümü yok.");
        SampleKind kind = fmtTag == 3 ? SampleKind.Float32 : bits switch { 16 => SampleKind.Pcm16, 24 => SampleKind.Pcm24, 32 => SampleKind.Pcm32, _ => throw new InvalidDataException("Desteklenmeyen bit derinliği") };
        var conv = new MonoConverter(kind, ch, rate);
        var mono = new float[conv.MaxOutput(dataLen)];
        int n = conv.Convert(b.AsSpan(dataPos, dataLen), mono);
        if (conv.OutputRate == 48000) return mono.AsSpan(0, n).ToArray();
        var rs = new StreamResampler(conv.OutputRate, 48000);
        var o = new float[rs.MaxOutput(n)];
        int m = rs.Process(mono.AsSpan(0, n), o);
        return o.AsSpan(0, m).ToArray();
    }

    public static void Write48kMono(string path, ReadOnlySpan<float> x)
    {
        using var w = new BinaryWriter(File.Create(path));
        int bytes = x.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(48000); w.Write(96000); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(bytes);
        foreach (float s in x) w.Write((short)Math.Clamp((int)MathF.Round(s * 32767f), -32768, 32767));
    }
}
