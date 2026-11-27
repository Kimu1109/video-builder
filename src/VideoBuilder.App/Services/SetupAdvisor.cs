using VideoBuilder.Rendering;

namespace VideoBuilder.App.Services;

/// <summary>おすすめの構成の 1 項目 (設計書 22.63)。</summary>
/// <param name="Name">項目の名前 (環境設定の行の名前と同じ)。</param>
/// <param name="Current">今の設定。</param>
/// <param name="Recommended">おすすめ。</param>
/// <param name="Reason">そう決めた理由 (測った値)。</param>
/// <param name="Apply">おすすめを設定に書き込む。変えない項目なら null。</param>
/// <param name="NeedsRestart">反映に、アプリの起動し直しが要る。</param>
public sealed record SetupItem(string Name, string Current, string Recommended, string Reason, Action<AppSettings>? Apply, bool NeedsRestart = false)
{
    /// <summary>今の設定と違う (使うと変わる)。</summary>
    public bool Changes => Apply != null;
}

/// <summary>おすすめを決めるときの、この環境のこと (計測の結果以外)。</summary>
/// <param name="IsLinux">Linux か (プレビューを描く GPU・画面の描画の項目を出す)。</param>
/// <param name="IsWindows">Windows か (画面の表示も「使う GPU」で行うので、どの GPU でも画面と共有できる)。</param>
/// <param name="ScreenRenderer">画面の描画の GPU の名前 (Linux。分からなければ null)。</param>
/// <param name="AllAdapters">GPU の一覧 (ソフトウェアのものも。「自動」が選ぶものを決めるため)。</param>
/// <param name="GpuFixedByEnvironment">環境変数 VIDEO_BUILDER_GPU で GPU が決められている。</param>
/// <param name="ScreenGlFixedByEnvironment">環境変数 VIDEO_BUILDER_SCREEN_GL で画面の描画が決められている。</param>
public sealed record SetupEnvironment(bool IsLinux, bool IsWindows, string? ScreenRenderer, IReadOnlyList<GpuAdapter> AllAdapters,
    bool GpuFixedByEnvironment = false, bool ScreenGlFixedByEnvironment = false);

/// <summary>
/// 計測の結果 (<see cref="SetupBenchmark"/>) から、このパソコンでいちばん速い構成を決める (設計書 22.63)。
/// 画面の操作を伴わない計算だけなので、テストで確かめる。
/// </summary>
public static class SetupAdvisor
{
    /// <summary>これより速くなければ (割合)、わざわざ変えない (測り方のばらつきで行ったり来たりしないように)。</summary>
    internal const double Margin = 0.9;

    /// <summary>
    /// 実際の編集の場面は、計測のシーンより重いことが多い。プレビューを画面の GPU で描くかは、
    /// 描画の時間をこの倍にしても (読み戻しの手間は変わらない) 速いときだけ勧める。
    /// </summary>
    internal const double HeavierScene = 2.0;

    /// <summary>
    /// プレビューを画面の GPU に移すのは、これより速いときだけ (割合)。重い効果で遅くなる心配があり、
    /// ノート PC の GPU のクロックで測るたびに値が揺れるので、ほかの項目より大きな差を求める。
    /// </summary>
    internal const double PreviewMoveMargin = 0.75;

    public static List<SetupItem> Recommend(SetupBenchmarkResult r, AppSettings s, SetupEnvironment env)
    {
        var items = new List<SetupItem>();
        var cpu = r.Render.FirstOrDefault(t => t.IsCpu && t.Ok);
        var gpus = r.Render.Where(t => !t.IsCpu && t.Ok && double.IsFinite(t.WithReadbackMs)).ToList();

        // ---- 使う GPU: 描いて読み戻すまでがいちばん速いもの (出力・動画のデコード・共有しないプレビューの速さ) ----
        var best = gpus.OrderBy(t => t.WithReadbackMs).FirstOrDefault();
        var auto = GpuContext.ChooseAdapter(env.AllAdapters, null);
        // プレビューの 1 フレーム: 画面と共有できれば読み戻さない時間、できなければ読み戻す時間
        bool Shared(RenderTiming t) => env.IsWindows || env.ScreenRenderer != null && t.Adapter != null && t.Adapter.Matches(env.ScreenRenderer);
        double PreviewMs(RenderTiming t) => Shared(t) ? t.RenderOnlyMs : t.WithReadbackMs;
        var screen = env.IsLinux && env.ScreenRenderer != null ? gpus.FirstOrDefault(t => t.Adapter!.Matches(env.ScreenRenderer)) : null;

        // ---- 描画方式: GPU が CPU より遅ければ CPU ----
        bool useCpu = best == null || cpu != null && cpu.WithReadbackMs < Math.Min(best.WithReadbackMs, PreviewMs(best)) * Margin;
        var backendNow = Describe(s.RenderBackend);
        if (best == null)
        {
            var why = r.Render.FirstOrDefault(t => !t.IsCpu)?.Error;
            items.Add(Item("描画方式", backendNow, Describe(RenderBackend.Auto),
                L.T("使える GPU が見つかりません。自動なら CPU で描きます。") + (why != null ? " (" + why + ")" : ""),
                s.RenderBackend == RenderBackend.Gpu, x => x.RenderBackend = RenderBackend.Auto));
        }
        else if (useCpu)
            items.Add(Item("描画方式", backendNow, Describe(RenderBackend.Cpu),
                L.Format("CPU の方が速い (CPU {0} / GPU {1})", Ms(cpu!.WithReadbackMs), Ms(best.WithReadbackMs)),
                s.RenderBackend != RenderBackend.Cpu, x => x.RenderBackend = RenderBackend.Cpu));
        else
            items.Add(Item("描画方式", backendNow, Describe(RenderBackend.Auto),
                L.Format("GPU の方が速い (GPU {0} / CPU {1})", Ms(best.WithReadbackMs), cpu != null ? Ms(cpu.WithReadbackMs) : "-"),
                s.RenderBackend == RenderBackend.Cpu, x => x.RenderBackend = RenderBackend.Auto));

        if (best != null && !useCpu && gpus.Count > 1)
        {
            string id = best.Adapter == auto ? "" : best.Adapter!.Id;
            string nowName = AdapterName(s.GpuAdapter, env.AllAdapters, auto);
            string reason = string.Join(" / ", gpus.OrderBy(t => t.WithReadbackMs).Select(t => $"{t.Adapter!.Name} {Ms(t.WithReadbackMs)}"));
            bool same = SameChoice(s.GpuAdapter, id, env.AllAdapters, auto);
            if (env.GpuFixedByEnvironment)
                items.Add(Item("使う GPU", nowName, best.Adapter!.Name, reason + "\n" + L.T("環境変数 VIDEO_BUILDER_GPU で指定されているため、ここでは変えられません。"), false, null));
            else
                items.Add(Item("使う GPU", nowName, id.Length == 0 ? L.Format("自動 ({0})", best.Adapter!.Name) : best.Adapter!.Name, reason,
                    !same, x => x.GpuAdapter = id, env.IsWindows));
        }

        // ---- プレビューを描く GPU (Linux): 選んだ GPU と画面の GPU が違うとき、画面の GPU で読み戻さずに描く方が速いか ----
        if (env.IsLinux && best != null && !useCpu && screen != null && screen != best)
        {
            // 読み戻しの手間 (描いて読み戻す時間 - 描くだけの時間) は場面の重さによらず、描く時間は重いほど延びる
            double overhead = Math.Max(0, best.WithReadbackMs - best.RenderOnlyMs);
            double screenHeavy = screen.RenderOnlyMs * HeavierScene, bestHeavy = best.RenderOnlyMs * HeavierScene + overhead;
            bool onScreen = screen.RenderOnlyMs < best.WithReadbackMs * PreviewMoveMargin && screenHeavy < bestHeavy * PreviewMoveMargin;
            items.Add(Item("プレビューを描く GPU", s.PreviewOnScreenGpu ? L.T("画面の表示と同じ GPU (読み戻さない)") : L.T("使う GPU と同じ"),
                onScreen ? L.T("画面の表示と同じ GPU (読み戻さない)") : L.T("使う GPU と同じ"),
                L.Format("画面の GPU で読み戻さずに描く {0} / 使う GPU で描いて読み戻す {1} (2 倍重い場面の見積もり: {2} / {3})",
                    Ms(screen.RenderOnlyMs), Ms(best.WithReadbackMs), Ms(screenHeavy), Ms(bestHeavy)),
                s.PreviewOnScreenGpu != onScreen, x => x.PreviewOnScreenGpu = onScreen));
        }

        // ---- 画面の描画 (Linux): EGL なら、画面と共有するプレビューでも GPU のデコードのフレームを CPU へ写さない ----
        if (env.IsLinux && s.ScreenGl == ScreenGlMode.Glx && !env.ScreenGlFixedByEnvironment)
            items.Add(Item("画面の描画 (Linux)", "GLX", "EGL",
                L.T("画面と共有するプレビューでも、GPU でデコードした動画を CPU へ写さずに出せます。"), true, x => x.ScreenGl = ScreenGlMode.Egl, needsRestart: true));

        // ---- 動画のデコード: ハードウェアが使えて、ソフトウェアより極端に遅くなければ使う (CPU を空けられる) ----
        if (r.Decode is { } d)
        {
            string now = s.HardwareDecode == VideoBuilder.Media.HardwareDecodeMode.Auto ? L.T("自動 (GPU があれば使う)") : L.T("ソフトウェアのみ");
            if (d.HardwareName == null)
                items.Add(Item("動画のデコード", now, now, L.Format("GPU でデコードできません ({0})。ソフトウェア {1}", d.HardwareError ?? "-", Ms(d.SoftwareMs)), false, null));
            else
            {
                bool hw = d.HardwareMs <= d.SoftwareMs * 1.3;
                var want = hw ? VideoBuilder.Media.HardwareDecodeMode.Auto : VideoBuilder.Media.HardwareDecodeMode.Off;
                items.Add(Item("動画のデコード", now, hw ? L.T("自動 (GPU があれば使う)") : L.T("ソフトウェアのみ"),
                    L.Format("1080p の H.264 の 1 フレーム: {0} {1} / ソフトウェア {2}", d.HardwareName, Ms(d.HardwareMs), Ms(d.SoftwareMs)),
                    s.HardwareDecode != want, x => x.HardwareDecode = want));
            }
        }

        // ---- 書き出しの形式: いちばん速い H.264 のエンコーダを、書き出しの画面で最初に選ぶ ----
        var encoders = r.Encoders.Where(e => e.Ok && double.IsFinite(e.Fps) && e.Fps > 0).OrderByDescending(e => e.Fps).ToList();
        if (encoders.Count > 0)
        {
            var fastest = encoders[0];
            string want = fastest.Encoder == "libx264" ? "" : fastest.Encoder;
            string Label(string encoder) => encoder.Length == 0 ? "H.264 (x264)" : r.Encoders.FirstOrDefault(e => e.Encoder == encoder)?.Label ?? encoder;
            string reason = string.Join(" / ", encoders.Select(e => $"{e.Label} {e.Fps:0} fps"));
            if (want.Length > 0) reason += "\n" + L.T("ハードウェアのエンコーダは、同じ品質の指定でもファイルが大きくなることがあります。");
            items.Add(Item("書き出しの形式 (最初に選ぶもの)", Label(s.PreferredVideoEncoder), Label(want), reason,
                s.PreferredVideoEncoder != want, x => x.PreferredVideoEncoder = want));
        }

        // ---- 再生が間に合わないとき: 1080p を 30fps で描けないなら、再生中だけ解像度を下げる ----
        double previewMs = useCpu || best == null ? cpu?.WithReadbackMs ?? double.NaN
            : Math.Min(PreviewMs(best), screen != null ? screen.RenderOnlyMs : double.PositiveInfinity);
        if (!s.AdaptivePlaybackResolution && double.IsFinite(previewMs) && previewMs > 1000.0 / 30)
            items.Add(Item("再生が間に合わないときは、再生中だけ解像度を下げる", L.T("オフ"), L.T("オン"),
                L.Format("1080p の 1 フレームに {0} かかる (30fps には 33 ms 以内)", Ms(previewMs)), true, x => x.AdaptivePlaybackResolution = true));
        return items;
    }

    /// <summary>おすすめのうち、今と違う項目を設定に書き込む。何か変えたら true。</summary>
    public static bool ApplyAll(IEnumerable<SetupItem> items, AppSettings s)
    {
        bool changed = false;
        foreach (var item in items)
        {
            if (item.Apply == null) continue;
            item.Apply(s);
            changed = true;
        }
        return changed;
    }

    private static SetupItem Item(string name, string current, string recommended, string reason, bool changes, Action<AppSettings>? apply, bool needsRestart = false) =>
        new(name, current, recommended, reason, changes ? apply : null, needsRestart && changes);

    private static string Ms(double ms) => double.IsFinite(ms) ? $"{ms:0.0} ms" : "-";

    private static string Describe(RenderBackend b) => b switch
    {
        RenderBackend.Gpu => "GPU",
        RenderBackend.Cpu => "CPU",
        _ => L.T("自動 (GPU があれば使う)"),
    };

    /// <summary>設定の GPU (空なら自動) の、画面に出す名前。</summary>
    private static string AdapterName(string id, IReadOnlyList<GpuAdapter> all, GpuAdapter? auto)
    {
        if (id.Length == 0) return auto != null ? L.Format("自動 ({0})", auto.Name) : L.T("自動");
        return GpuContext.ChooseAdapter(all, id)?.Name ?? id.Split('|')[0];
    }

    /// <summary>今の設定 (current) とおすすめ (wanted) が、実際に同じ GPU を選ぶか (自動で同じものになるなら同じ)。</summary>
    private static bool SameChoice(string current, string wanted, IReadOnlyList<GpuAdapter> all, GpuAdapter? auto)
    {
        if (current == wanted) return true;
        var a = current.Length == 0 ? auto : GpuContext.ChooseAdapter(all, current);
        var b = wanted.Length == 0 ? auto : GpuContext.ChooseAdapter(all, wanted);
        // 決め打ちで同じ GPU を選んでいるときは、自動に戻すほどではない
        return a != null && a == b && wanted.Length == 0;
    }
}
