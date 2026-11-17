using System.Runtime.InteropServices;
using SkiaSharp;

namespace VideoBuilder.Rendering;

/// <summary>
/// 作業用のピクセルバッファの使い回し。
/// フレームと同じ大きさのバッファ (1080p で約 8MB) を毎フレーム確保・解放すると、そのたびに OS が
/// メモリを割り当て直すので遅い。使い終わったバッファを取っておいて、次の描画で再利用する。
/// </summary>
public static unsafe class PixelPool
{
    private const long Granularity = 64 * 1024;
    /// <summary>取っておく量の上限。超えたぶんは OS に返す。</summary>
    private const long MaxHeldBytes = 768L * 1024 * 1024;

    private static readonly Dictionary<long, Stack<IntPtr>> Free = new();
    private static readonly object Gate = new();
    private static long _held;

    private static long Round(long bytes) => (Math.Max(1, bytes) + Granularity - 1) / Granularity * Granularity;

    /// <summary>bytes 以上の大きさのバッファを借りる。中身は不定 (前の内容が残っている)。</summary>
    public static IntPtr Rent(long bytes)
    {
        long size = Round(bytes);
        lock (Gate)
        {
            if (Free.TryGetValue(size, out var stack) && stack.Count > 0)
            {
                _held -= size;
                return stack.Pop();
            }
        }
        return (IntPtr)NativeMemory.Alloc((nuint)size);
    }

    public static void Return(IntPtr buffer, long bytes)
    {
        long size = Round(bytes);
        lock (Gate)
        {
            if (_held + size <= MaxHeldBytes)
            {
                if (!Free.TryGetValue(size, out var stack)) Free[size] = stack = new Stack<IntPtr>();
                stack.Push(buffer);
                _held += size;
                return;
            }
        }
        NativeMemory.Free((void*)buffer);
    }

    /// <summary>取っておいたバッファをすべて OS に返す。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            foreach (var stack in Free.Values)
                while (stack.Count > 0) NativeMemory.Free((void*)stack.Pop());
            Free.Clear();
            _held = 0;
        }
    }

    /// <summary>借りたバッファを中身にした画像を作る。画像が破棄されると、バッファは自動的に返却される。</summary>
    public static SKImage ToImage(IntPtr buffer, SKImageInfo info, int rowBytes)
    {
        long bytes = (long)rowBytes * info.Height;
        using var pixmap = new SKPixmap(info, buffer, rowBytes);
        var image = SKImage.FromPixels(pixmap, (address, _) => Return(address, bytes), null);
        if (image == null) { Return(buffer, bytes); throw new InvalidOperationException("画像を作れません"); }
        return image;
    }

    /// <summary>source の内容を写した画像を作る (バッファは使い回しから借りる)。</summary>
    public static SKImage CopyOf(SKPixmap source)
    {
        var info = new SKImageInfo(source.Width, source.Height, source.ColorType, source.AlphaType);
        int rowBytes = source.Width * source.BytesPerPixel;
        var buffer = Rent((long)rowBytes * source.Height);
        byte* src = (byte*)source.GetPixels(), dst = (byte*)buffer;
        int srcRow = source.RowBytes, height = source.Height;
        // 大きな画像は複数スレッドで写す
        PixelOps.ForBands(source.Width, height, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++) Buffer.MemoryCopy(src + (long)y * srcRow, dst + (long)y * rowBytes, rowBytes, rowBytes);
        });
        return ToImage(buffer, info, rowBytes);
    }
}
