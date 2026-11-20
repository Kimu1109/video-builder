using VideoBuilder.Core;
using SkiaSharp;
using SkiaSharp.Skottie;

namespace VideoBuilder.Rendering;

/// <summary>
/// ベクター画像 (SVG / Lottie) の読み込みと描画。読み込んだものはファイルごとに覚えておき、
/// ファイルが書き換わったら読み直す。描画エンジン 1 つにつき 1 つ持つ (スレッドをまたいで使わない)。
/// </summary>
internal sealed class VectorSources : IDisposable
{
    /// <summary>描く絵の長辺の上限 (メモリを使いすぎないように)。</summary>
    public const int MaxSide = 8192;

    public sealed class Item : IDisposable
    {
        public SKPicture? Picture;
        public Animation? Lottie;
        public SKSize Size;
        public DateTime Time;
        /// <summary>読み直したら変わる番号 (描いた絵を覚えておくときの鍵に使う)。</summary>
        public long Version;
        public bool Animated => Lottie != null;
        public double Duration => Lottie?.Duration.TotalSeconds ?? 0;

        public void Dispose()
        {
            Picture?.Dispose();
            Lottie?.Dispose();
        }
    }

    private readonly Dictionary<string, Item?> _items = new();
    private long _version;

    // 色を置き換えた Lottie (キー: パス + 対応表)。色を選んでいる間に増えすぎないよう、古いものから捨てる
    private readonly Dictionary<string, (Item Item, long Version, long LastUsed)> _recolored = new();
    private long _tick;
    private const int MaxRecolored = 16;

    /// <summary>
    /// 色を置き換えたベクター画像 (SVG / Lottie) を返す。map が空なら Get(path) と同じ。
    /// 元のファイルが書き換わったら作り直す。
    /// </summary>
    public Item? Get(string path, IReadOnlyDictionary<string, string> map, IReadOnlySet<string>? hidden = null)
    {
        var item = Get(path);
        // 部品の非表示は SVG だけ
        if (item == null || item.Animated) hidden = null;
        if (item == null || (map.Count == 0 && (hidden == null || hidden.Count == 0))) return item;
        string key = path + "|" + string.Join(",", map.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + ">" + kv.Value))
                   + "|" + (hidden == null ? "" : string.Join(",", hidden.Order(StringComparer.Ordinal)));
        _tick++;
        if (_recolored.TryGetValue(key, out var hit) && hit.Version == item.Version)
        {
            _recolored[key] = hit with { LastUsed = _tick };
            return hit.Item;
        }
        if (_recolored.Remove(key, out var stale)) stale.Item.Dispose();
        Item? made;
        try
        {
            var text = map.Count > 0 ? VectorColors.RecolorFile(path, map) : VectorFiles.ReadText(path);
            if (hidden is { Count: > 0 }) text = VectorFiles.HideSvgElements(text, hidden);
            // 書き換えた SVG は文字列から読む (中から相対パスで参照している画像は読めない)
            made = item.Animated ? LoadLottie(text) : LoadSvg(svg => svg.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text))));
        }
        catch (Exception e) when (e is not OutOfMemoryException) { made = null; }
        if (made == null) return item;
        made.Time = item.Time; made.Version = ++_version;
        if (_recolored.Count >= MaxRecolored)
        {
            var oldest = _recolored.MinBy(kv => kv.Value.LastUsed);
            oldest.Value.Item.Dispose();
            _recolored.Remove(oldest.Key);
        }
        _recolored[key] = (made, item.Version, _tick);
        return made;
    }

    private static Item? LoadLottie(string json)
    {
        using var data = SKData.CreateCopy(System.Text.Encoding.UTF8.GetBytes(json));
        using var stream = new SKMemoryStream(data);
        if (!Animation.TryCreate(stream, out var anim) || anim == null) return null;
        return new Item { Lottie = anim, Size = anim.Size };
    }

    private static Item? LoadSvg(Func<Svg.Skia.SKSvg, SKPicture?> load)
    {
        using (var svg = new Svg.Skia.SKSvg())
        {
            var picture = load(svg);
            if (picture == null) return null;
            // SKSvg を閉じても使えるよう、描き直して自分の持ち物にする
            var bounds = picture.CullRect;
            using var recorder = new SKPictureRecorder();
            var canvas = recorder.BeginRecording(bounds);
            canvas.DrawPicture(picture);
            var own = recorder.EndRecording();
            return new Item { Picture = own, Size = new SKSize(bounds.Width, bounds.Height) };
        }
    }

    /// <summary>ファイルを読む。読めなければ null。</summary>
    public Item? Get(string path)
    {
        if (path.Length == 0 || !File.Exists(path)) return null;
        var time = File.GetLastWriteTimeUtc(path);
        if (_items.TryGetValue(path, out var hit))
        {
            if (hit != null && hit.Time == time) return hit;
            if (hit == null && _failed.GetValueOrDefault(path) == time) return null;
        }
        hit?.Dispose();
        var item = Load(path);
        if (item != null) { item.Time = time; item.Version = ++_version; }
        else _failed[path] = time;
        _items[path] = item;
        return item;
    }

    // 読めなかったファイルの更新日時 (書き換わるまで読み直さない)
    private readonly Dictionary<string, DateTime> _failed = new();

    private static Item? Load(string path)
    {
        try
        {
            if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase)) return LoadSvg(svg => svg.Load(path));
            return LoadLottie(VectorFiles.ReadText(path));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>長辺が size ピクセル (0 ならファイルに書かれた大きさ) になるように描く。Lottie は seconds 秒の位置を描く。</summary>
    public SKImage? Render(Item item, float size, double seconds)
    {
        float w = item.Size.Width, h = item.Size.Height;
        if (w <= 0 || h <= 0) return null;
        float scale = size > 0 ? size / Math.Max(w, h) : 1;
        scale = Math.Min(scale, MaxSide / Math.Max(w, h));
        int pw = Math.Max(1, (int)MathF.Ceiling(w * scale)), ph = Math.Max(1, (int)MathF.Ceiling(h * scale));
        // 描画のほかの絵と同じ形式 (BGRA) で作る (形式が違うと、描くたびに変換が入る)
        using var surface = SKSurface.Create(Effects.Info(pw, ph));
        if (surface == null) return null;
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (item.Picture != null)
        {
            var b = item.Picture.CullRect;
            canvas.Scale(scale);
            canvas.Translate(-b.Left, -b.Top);
            canvas.DrawPicture(item.Picture);
        }
        else if (item.Lottie != null)
        {
            item.Lottie.SeekFrameTime(TimeSpan.FromSeconds(Math.Max(0, seconds)));
            item.Lottie.Render(canvas, SKRect.Create(pw, ph));
        }
        canvas.Flush();
        return surface.Snapshot();
    }

    public void Dispose()
    {
        foreach (var i in _items.Values) i?.Dispose();
        _items.Clear();
        foreach (var r in _recolored.Values) r.Item.Dispose();
        _recolored.Clear();
    }
}
