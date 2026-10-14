using System.Buffers.Binary;
using System.IO.Compression;

namespace PaperKey.Core.Vision;

/// <summary>Минимальный декодер PNG (8 бит на канал, без чересстрочности) — для тестов и инструментов; в браузере картинки декодирует сам браузер.</summary>
public static class PngReader
{
    public static GrayImage ReadGray(string path)
    {
        var data = File.ReadAllBytes(path);
        if (!data.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            throw new InvalidDataException("Не PNG.");

        int width = 0, height = 0, colorType = 0, bitDepth = 0;
        using var compressed = new MemoryStream();
        var pos = 8;
        while (pos < data.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            var type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            var body = data.AsSpan(pos + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                    bitDepth = body[8];
                    colorType = body[9];
                    if (body[12] != 0) throw new NotSupportedException("Чересстрочный PNG не поддерживается.");
                    break;
                case "IDAT":
                    compressed.Write(body);
                    break;
            }
            pos += 12 + length;
            if (type == "IEND") break;
        }
        if (bitDepth != 8) throw new NotSupportedException("Поддерживается только 8 бит на канал.");

        var channels = colorType switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => throw new NotSupportedException($"Тип цвета {colorType}.") };
        var stride = width * channels;
        compressed.Position = 0;
        var raw = new byte[(stride + 1) * height];
        using (var z = new ZLibStream(compressed, CompressionMode.Decompress)) z.ReadExactly(raw);

        var pixels = new byte[stride * height];
        var prev = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var line = raw.AsSpan(y * (stride + 1) + 1, stride);
            var cur = pixels.AsSpan(y * stride, stride);
            for (var i = 0; i < stride; i++)
            {
                int a = i >= channels ? cur[i - channels] : 0;
                int b = prev[i];
                int c = i >= channels ? prev[i - channels] : 0;
                int x = line[i];
                cur[i] = (byte)(filter switch
                {
                    0 => x,
                    1 => x + a,
                    2 => x + b,
                    3 => x + (a + b) / 2,
                    4 => x + Paeth(a, b, c),
                    _ => throw new InvalidDataException($"Фильтр {filter}."),
                });
            }
            cur.CopyTo(prev);
        }

        var gray = new GrayImage(width, height);
        for (var i = 0; i < width * height; i++)
        {
            var p = i * channels;
            gray.Pixels[i] = channels >= 3
                ? (byte)((pixels[p] * 299 + pixels[p + 1] * 587 + pixels[p + 2] * 114) / 1000)
                : pixels[p];
        }
        return gray;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
