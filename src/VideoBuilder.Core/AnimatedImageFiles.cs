namespace VideoBuilder.Core;

/// <summary>
/// アニメーションする画像 (アニメーション WebP・アニメーション GIF) を見分ける。
/// アニメーションするものは動画オブジェクトとして (FFmpeg で) 読み、しないものは画像オブジェクトのままにする。
/// ファイルの先頭 (GIF はブロックの並び) だけを読むので速い。
/// </summary>
public static class AnimatedImageFiles
{
    /// <summary>アニメーションしうる画像の拡張子。</summary>
    public static readonly string[] Extensions = { "webp", "gif" };

    public static bool IsAnimated(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            return Path.GetExtension(path).TrimStart('.').ToLowerInvariant() switch
            {
                "webp" => IsAnimatedWebP(s),
                "gif" => IsAnimatedGif(s),
                _ => false,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>拡張 (VP8X) チャンクにアニメーションの印があるか。</summary>
    internal static bool IsAnimatedWebP(Stream s)
    {
        // "RIFF" <大きさ> "WEBP" "VP8X" <チャンクの大きさ> <印 1 バイト> ...  印の 0x02 がアニメーション
        var head = new byte[21];
        if (ReadFully(s, head) < head.Length) return false;
        return Ascii(head, 0, "RIFF") && Ascii(head, 8, "WEBP") && Ascii(head, 12, "VP8X") && (head[20] & 0x02) != 0;
    }

    /// <summary>画像 (Image Descriptor) が 2 枚以上あるか。</summary>
    internal static bool IsAnimatedGif(Stream s)
    {
        var head = new byte[13];
        if (ReadFully(s, head) < head.Length || !(Ascii(head, 0, "GIF87a") || Ascii(head, 0, "GIF89a"))) return false;
        if ((head[10] & 0x80) != 0 && !Skip(s, 3 << ((head[10] & 0x07) + 1))) return false;   // 全体の色表
        int images = 0;
        while (true)
        {
            int kind = s.ReadByte();
            switch (kind)
            {
                case 0x21:   // 拡張ブロック: 種類 1 バイトのあと、小ブロックが続く
                    if (s.ReadByte() < 0 || !SkipSubBlocks(s)) return false;
                    break;
                case 0x2C:   // 画像: 位置と大きさ (9 バイト)、色表、LZW の符号長、小ブロック
                    var desc = new byte[9];
                    if (ReadFully(s, desc) < desc.Length) return false;
                    if ((desc[8] & 0x80) != 0 && !Skip(s, 3 << ((desc[8] & 0x07) + 1))) return false;
                    if (s.ReadByte() < 0 || !SkipSubBlocks(s)) return false;
                    if (++images >= 2) return true;
                    break;
                default:     // 0x3B (終わり) や壊れたファイル
                    return false;
            }
        }
    }

    private static bool SkipSubBlocks(Stream s)
    {
        while (true)
        {
            int size = s.ReadByte();
            if (size < 0) return false;
            if (size == 0) return true;
            if (!Skip(s, size)) return false;
        }
    }

    private static bool Skip(Stream s, int count)
    {
        if (s.CanSeek)
        {
            if (s.Position + count > s.Length) return false;
            s.Seek(count, SeekOrigin.Current);
            return true;
        }
        var buf = new byte[count];
        return ReadFully(s, buf) == count;
    }

    private static int ReadFully(Stream s, byte[] buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = s.Read(buf, total, buf.Length - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    private static bool Ascii(byte[] b, int offset, string text)
    {
        for (int i = 0; i < text.Length; i++)
            if (b[offset + i] != text[i]) return false;
        return true;
    }
}
