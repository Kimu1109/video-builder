using System.Globalization;
using VideoBuilder.Core;
using VideoBuilder.Plugins;

namespace VoicevoxPlugin;

/// <summary>
/// VOICEVOX 連携。VOICEVOX のエンジン (別に起動しておく) にセリフを送って音声を作り、再生位置に置く。
/// 字幕 (テキストオブジェクト) も一緒に置ける。台本からの一括作成と、セリフを直したあとの作り直しもできる。
/// </summary>
public sealed class Plugin : IPlugin
{
    public const string Group = "VOICEVOX";

    public void Initialize(IPluginHost host)
    {
        // コマンドは API バージョン 3 から (古いアプリでは何も登録しない)
        if (host.ApiVersion < 3) { host.Log("このアプリのプラグイン API が古いため、使えません (3 以上が必要)"); return; }
        host.RegisterCommand(new SynthesizeCommand());
        host.RegisterCommand(new ScriptCommand());
        host.RegisterCommand(new RegenerateCommand());
        host.RegisterCommand(new SettingsCommand());
    }

    /// <summary>待ち時間付きで実行する。待ち時間を過ぎたら VoicevoxException (利用者の中止なら OperationCanceledException)。</summary>
    internal static async Task<T> WithTimeout<T>(TimeSpan timeout, CancellationToken ct, Func<CancellationToken, Task<T>> work)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try { return await work(limit.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new VoicevoxException("VOICEVOX のエンジンから返事がありません。VOICEVOX が起動しているか確かめてください。");
        }
    }

    /// <summary>話者の一覧を取る (進み具合のダイアログ付き)。0 件ならメッセージを出して null。</summary>
    internal static async Task<List<VoiceStyle>?> LoadStylesAsync(IPluginCommandContext context, VoicevoxClient client)
    {
        var styles = await context.RunWithProgressAsync("VOICEVOX", (p, ct) =>
        {
            p.Report(new PluginProgress("VOICEVOX のエンジンにつないでいます..."));
            return WithTimeout(TimeSpan.FromSeconds(10), ct, client.GetStylesAsync);
        });
        if (styles.Count > 0) return styles;
        await context.ShowMessageAsync("VOICEVOX", "使える話者がありません。VOICEVOX のエンジンを確かめてください。");
        return null;
    }

    /// <summary>1 つのセリフを音声にして、dir に保存する。</summary>
    internal static async Task<VoiceClip> SynthesizeToFileAsync(VoicevoxClient client, string dir, string text, VoiceStyle style, VoiceParams voice, CancellationToken ct)
    {
        var wav = await WithTimeout(TimeSpan.FromMinutes(5), ct, t => client.SynthesizeAsync(text, style.Id, voice, t));
        if (!WavInfo.TryGetDuration(wav, out var seconds)) throw new VoicevoxException("作った音声を読めませんでした。");
        Directory.CreateDirectory(dir);
        var file = VoiceFiles.NewPath(dir, style.Speaker, text, DateTime.Now);
        await File.WriteAllBytesAsync(file, wav, ct);
        return new VoiceClip(file, seconds, text, style, voice);
    }

    // ---------------- フォームの部品 ----------------

    internal const string CreditNote = "動画には「VOICEVOX:キャラクター名」のクレジットが必要です。キャラクターごとの利用規約も確かめてください。";

    internal static string? UnsavedNote(IPluginCommandContext context) => context.ProjectDirectory == null
        ? "プロジェクトをまだ保存していないため、音声はアプリのデータフォルダに保存します。先にプロジェクトを保存すると、プロジェクトの隣の voices フォルダに保存します。"
        : null;

    internal static PluginChoiceField StyleField(string key, string label, IReadOnlyList<VoiceStyle> styles, int? selected, string? note = null, PluginChoice? first = null)
    {
        var options = styles.Select(s => new PluginChoice(s.Id.ToString(CultureInfo.InvariantCulture), s.Label)).ToList();
        if (first != null) options.Insert(0, first);
        string value = first?.Value ?? styles[0].Id.ToString(CultureInfo.InvariantCulture);
        if (selected is { } id && styles.Any(s => s.Id == id)) value = id.ToString(CultureInfo.InvariantCulture);
        return new PluginChoiceField { Key = key, Label = label, Options = options, Value = value, Note = note };
    }

    /// <summary>話速・音高・抑揚・音量・前後の無音の項目。</summary>
    internal static IEnumerable<PluginField> VoiceFields(VoiceParams voice)
    {
        var v = voice.Clamped();
        yield return new PluginNumberField { Key = "speed", Label = "話速", Value = v.Speed, Minimum = 0.5, Maximum = 2, Decimals = 2 };
        yield return new PluginNumberField { Key = "pitch", Label = "音高", Value = v.Pitch, Minimum = -0.15, Maximum = 0.15, Decimals = 2 };
        yield return new PluginNumberField { Key = "intonation", Label = "抑揚", Value = v.Intonation, Minimum = 0, Maximum = 2, Decimals = 2 };
        yield return new PluginNumberField { Key = "volume", Label = "音量", Value = v.Volume, Minimum = 0, Maximum = 2, Decimals = 2 };
        yield return new PluginNumberField { Key = "pre", Label = "前の無音 (秒)", Value = v.PreSilence, Minimum = 0, Maximum = 1.5, Decimals = 2 };
        yield return new PluginNumberField { Key = "post", Label = "後の無音 (秒)", Value = v.PostSilence, Minimum = 0, Maximum = 1.5, Decimals = 2 };
    }

    internal static VoiceParams ReadVoice(PluginForm form)
    {
        double Num(string key) => form.Get<PluginNumberField>(key).Value;
        return new VoiceParams
        {
            Speed = Num("speed"), Pitch = Num("pitch"), Intonation = Num("intonation"), Volume = Num("volume"),
            PreSilence = Num("pre"), PostSilence = Num("post"),
        }.Clamped();
    }

    /// <summary>選んだスタイル。値が数字でないとき (「今のまま」など) は null。</summary>
    internal static VoiceStyle? ReadStyle(PluginForm form, string key, IReadOnlyList<VoiceStyle> styles) =>
        int.TryParse(form.Get<PluginChoiceField>(key).Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? styles.FirstOrDefault(s => s.Id == id)
            : null;
}

/// <summary>セリフから音声を作って、再生位置に置く。</summary>
public sealed class SynthesizeCommand : IPluginCommand
{
    public string Id => "voicevox.synthesize";
    public string Name => "セリフから音声を作る...";
    public string? Group => Plugin.Group;

    public async Task RunAsync(IPluginCommandContext context)
    {
        var settings = VoiceSettings.Load(context.DataDirectory);
        var client = new VoicevoxClient(settings.EngineUrl);
        try
        {
            if (await Plugin.LoadStylesAsync(context, client) is not { } styles) return;
            var form = BuildForm(context, settings, styles);
            if (!await context.ShowFormAsync(form)) return;
            var request = ReadForm(form, styles);
            settings.StyleId = request.Style.Id;
            settings.Voice = request.Voice;
            settings.AddSubtitle = request.Subtitle;
            settings.Save(context.DataDirectory);

            var dir = VoiceFiles.Directory(context);
            var clip = await context.RunWithProgressAsync("VOICEVOX", (p, ct) =>
            {
                p.Report(new PluginProgress($"{request.Style.Label} の声を作っています..."));
                return Plugin.SynthesizeToFileAsync(client, dir, request.Text, request.Style, request.Voice, ct);
            });
            if (VoicePlacer.Place(context, clip, request.Subtitle) == null)
                await context.ShowMessageAsync("VOICEVOX", $"再生位置に空いているレイヤーがないため、置けませんでした。\n音声は次の場所に保存しました:\n{clip.Path}");
        }
        catch (VoicevoxException e)
        {
            await context.ShowMessageAsync("VOICEVOX", e.Message);
        }
    }

    public sealed record Request(string Text, VoiceStyle Style, VoiceParams Voice, bool Subtitle);

    /// <summary>入力フォーム。前回の値と、選んでいるテキストオブジェクトの文字を初めから入れておく。</summary>
    public static PluginForm BuildForm(IPluginCommandContext context, VoiceSettings settings, IReadOnlyList<VoiceStyle> styles)
    {
        var selectedText = context.SelectedObjects.Count == 1 && context.SelectedObjects[0].Main.DefinitionId == EffectRegistry.Text
            ? context.SelectedObjects[0].Main.Values.GetValueOrDefault("text", "") : "";
        var form = new PluginForm
        {
            Title = "VOICEVOX: セリフから音声を作る",
            OkText = "作って置く",
            Width = 480,
            Message = Plugin.UnsavedNote(context),
            Fields =
            {
                new PluginTextField { Key = "text", Label = "セリフ", Multiline = true, Required = true, Value = selectedText, Placeholder = "しゃべらせたいセリフ" },
                Plugin.StyleField("style", "話者", styles, settings.StyleId, Plugin.CreditNote),
            },
        };
        form.Fields.AddRange(Plugin.VoiceFields(settings.Voice));
        form.Fields.Add(new PluginCheckField { Key = "subtitle", Label = "字幕も置く", Value = settings.AddSubtitle, Note = "セリフのテキストを音声と同じ長さで置き、音声とグループにします。" });
        return form;
    }

    public static Request ReadForm(PluginForm form, IReadOnlyList<VoiceStyle> styles)
    {
        var text = form.Get<PluginTextField>("text").Value.Trim();
        if (text.Length == 0) throw new VoicevoxException("セリフが空です。");
        var style = Plugin.ReadStyle(form, "style", styles) ?? styles[0];
        return new Request(text, style, Plugin.ReadVoice(form), form.Get<PluginCheckField>("subtitle").Value);
    }
}

/// <summary>台本 (1 行 1 セリフ) から音声をまとめて作り、再生位置から順に並べる。</summary>
public sealed class ScriptCommand : IPluginCommand
{
    public const int MaxLines = 500;

    public string Id => "voicevox.script";
    public string Name => "台本から一括で作る...";
    public string? Group => Plugin.Group;

    public async Task RunAsync(IPluginCommandContext context)
    {
        var settings = VoiceSettings.Load(context.DataDirectory);
        var client = new VoicevoxClient(settings.EngineUrl);
        try
        {
            if (await Plugin.LoadStylesAsync(context, client) is not { } styles) return;
            var form = BuildForm(context, settings, styles);
            if (!await context.ShowFormAsync(form)) return;
            var request = ReadForm(form, styles);
            settings.StyleId = request.DefaultStyle.Id;
            settings.Voice = request.Voice;
            settings.AddSubtitle = request.Subtitle;
            settings.ScriptGap = request.GapSeconds;
            settings.Save(context.DataDirectory);

            var dir = VoiceFiles.Directory(context);
            var clips = await context.RunWithProgressAsync("VOICEVOX", async (p, ct) =>
            {
                var list = new List<VoiceClip>();
                for (int i = 0; i < request.Lines.Count; i++)
                {
                    var line = request.Lines[i];
                    p.Report(new PluginProgress($"{i + 1} / {request.Lines.Count}: {line.Style.Label} の声を作っています...", (double)i / request.Lines.Count));
                    list.Add(await Plugin.SynthesizeToFileAsync(client, dir, line.Text, line.Style, request.Voice, ct));
                }
                return list;
            });
            int gap = (int)Math.Round(request.GapSeconds * context.Project.Settings.Fps.Value);
            if (VoicePlacer.PlaceMany(context, clips, gap, request.Subtitle, "VOICEVOX の台本を置く") == null)
                await context.ShowMessageAsync("VOICEVOX", $"再生位置から先に、{clips.Count} 個のセリフがすべて入る空いたレイヤーがないため、置けませんでした。\n音声は次の場所に保存しました:\n{dir}");
        }
        catch (VoicevoxException e)
        {
            await context.ShowMessageAsync("VOICEVOX", e.Message);
        }
    }

    public sealed record Request(IReadOnlyList<ScriptLine> Lines, VoiceStyle DefaultStyle, VoiceParams Voice, double GapSeconds, bool Subtitle);

    public static PluginForm BuildForm(IPluginCommandContext context, VoiceSettings settings, IReadOnlyList<VoiceStyle> styles)
    {
        var form = new PluginForm
        {
            Title = "VOICEVOX: 台本から一括で作る",
            OkText = "作って並べる",
            Width = 520,
            Message = string.Join("\n", new[]
            {
                "1 行に 1 つのセリフを書きます。「ずんだもん: こんにちは」のように書くと、その話者で読みます (「ずんだもん(あまあま): ...」でスタイルも選べます)。",
                "話者を書かない行は既定の話者で読みます。# で始まる行は飛ばします。",
                Plugin.UnsavedNote(context),
            }.Where(s => s != null)),
            Fields =
            {
                new PluginTextField { Key = "script", Label = "台本", Multiline = true, Required = true, Placeholder = "ずんだもん: こんにちはなのだ\n四国めたん: こんにちは" },
                Plugin.StyleField("style", "既定の話者", styles, settings.StyleId, Plugin.CreditNote),
                new PluginNumberField { Key = "gap", Label = "セリフの間 (秒)", Value = settings.ScriptGap, Minimum = 0, Maximum = 10, Decimals = 2 },
            },
        };
        form.Fields.AddRange(Plugin.VoiceFields(settings.Voice));
        form.Fields.Add(new PluginCheckField { Key = "subtitle", Label = "字幕も置く", Value = settings.AddSubtitle, Note = "字幕を 1 つのレイヤーに、音声をそのすぐ下のレイヤーに並べ、1 組ずつグループにします。" });
        return form;
    }

    public static Request ReadForm(PluginForm form, IReadOnlyList<VoiceStyle> styles)
    {
        var defaultStyle = Plugin.ReadStyle(form, "style", styles) ?? styles[0];
        var lines = ScriptParser.Parse(form.Get<PluginTextField>("script").Value, styles, defaultStyle);
        if (lines.Count == 0) throw new VoicevoxException("台本にセリフがありません。");
        if (lines.Count > MaxLines) throw new VoicevoxException($"台本のセリフが多すぎます ({lines.Count} 行)。{MaxLines} 行までに分けてください。");
        return new Request(lines, defaultStyle, Plugin.ReadVoice(form), Math.Clamp(form.Get<PluginNumberField>("gap").Value, 0, 10), form.Get<PluginCheckField>("subtitle").Value);
    }
}

/// <summary>
/// 選んだ音声 (このプラグインで作ったもの) を作り直す。字幕の文字を直してから実行すると、直した文字で作り直す。
/// 1 つなら、セリフ・話者・調整の値を変えられる。いくつも選んだときは、話者だけをまとめて変えられる。
/// </summary>
public sealed class RegenerateCommand : IPluginCommand
{
    public string Id => "voicevox.regenerate";
    public string Name => "選んだ音声を作り直す...";
    public string? Group => Plugin.Group;

    public bool CanRun(IPluginCommandContext context) => VoicePlacer.FindTargets(context).Count > 0;

    public async Task RunAsync(IPluginCommandContext context)
    {
        var targets = VoicePlacer.FindTargets(context);
        if (targets.Count == 0)
        {
            await context.ShowMessageAsync("VOICEVOX", "VOICEVOX で作った音声 (またはその字幕) を選んでから実行してください。");
            return;
        }
        var settings = VoiceSettings.Load(context.DataDirectory);
        var client = new VoicevoxClient(settings.EngineUrl);
        try
        {
            if (await Plugin.LoadStylesAsync(context, client) is not { } styles) return;
            var form = BuildForm(context, targets, styles);
            if (!await context.ShowFormAsync(form)) return;
            var requests = ReadForm(form, targets, styles);

            var dir = VoiceFiles.Directory(context);
            var items = await context.RunWithProgressAsync("VOICEVOX", async (p, ct) =>
            {
                var list = new List<(VoicePlacer.Target, VoiceClip)>();
                for (int i = 0; i < requests.Count; i++)
                {
                    var (t, text, style, voice) = requests[i];
                    p.Report(new PluginProgress($"{i + 1} / {requests.Count}: {style.Label} の声を作っています...", (double)i / requests.Count));
                    list.Add((t, await Plugin.SynthesizeToFileAsync(client, dir, text, style, voice, ct)));
                }
                return list;
            });
            var blocked = VoicePlacer.Replace(context, items);
            if (blocked.Count > 0)
                await context.ShowMessageAsync("VOICEVOX",
                    $"音声が長くなり、後ろのオブジェクトと重なるため、{blocked.Count} 個は差し替えませんでした:\n"
                    + string.Join("\n", blocked.Select(t => $"・{t.CurrentText}")) + $"\n作った音声は次の場所に保存しました:\n{dir}");
        }
        catch (VoicevoxException e)
        {
            await context.ShowMessageAsync("VOICEVOX", e.Message);
        }
    }

    /// <summary>「今のまま」の選択肢の値。</summary>
    public const string Keep = "keep";

    public static PluginForm BuildForm(IPluginCommandContext context, IReadOnlyList<VoicePlacer.Target> targets, IReadOnlyList<VoiceStyle> styles)
    {
        if (targets.Count == 1)
        {
            var t = targets[0];
            var form = new PluginForm
            {
                Title = "VOICEVOX: 音声を作り直す",
                OkText = "作り直す",
                Width = 480,
                Message = t.Subtitle != null ? "字幕の文字も、このセリフに合わせて直します。" : null,
                Fields =
                {
                    new PluginTextField { Key = "text", Label = "セリフ", Multiline = true, Required = true, Value = t.CurrentText },
                    Plugin.StyleField("style", "話者", styles, t.Info.StyleId, Plugin.CreditNote),
                },
            };
            form.Fields.AddRange(Plugin.VoiceFields(t.Info.Voice));
            return form;
        }
        return new PluginForm
        {
            Title = "VOICEVOX: 音声を作り直す",
            OkText = "作り直す",
            Message = $"選んだ {targets.Count} 個の音声を、今のセリフ (字幕を直していれば直した文字) で作り直します。話速などの調整の値は、それぞれ作ったときのままです。",
            Fields = { Plugin.StyleField("style", "話者", styles, null, Plugin.CreditNote, new PluginChoice(Keep, "(それぞれ今のまま)")) },
        };
    }

    public static List<(VoicePlacer.Target Target, string Text, VoiceStyle Style, VoiceParams Voice)> ReadForm(
        PluginForm form, IReadOnlyList<VoicePlacer.Target> targets, IReadOnlyList<VoiceStyle> styles)
    {
        var chosen = Plugin.ReadStyle(form, "style", styles);
        VoiceStyle StyleOf(VoicePlacer.Target t) => chosen ?? styles.FirstOrDefault(s => s.Id == t.Info.StyleId)
            ?? throw new VoicevoxException($"作ったときの話者 ({t.Info.Speaker}) が、今のエンジンにありません。話者を選んで作り直してください。");
        if (targets.Count == 1)
        {
            var text = form.Get<PluginTextField>("text").Value.Trim();
            if (text.Length == 0) throw new VoicevoxException("セリフが空です。");
            return new() { (targets[0], text, StyleOf(targets[0]), Plugin.ReadVoice(form)) };
        }
        return targets.Select(t => (t, t.CurrentText.Trim(), StyleOf(t), t.Info.Voice)).Where(x => x.Item2.Length > 0).ToList();
    }
}

/// <summary>エンジンの URL を設定し、つながるかを確かめる。</summary>
public sealed class SettingsCommand : IPluginCommand
{
    public string Id => "voicevox.settings";
    public string Name => "設定...";
    public string? Group => Plugin.Group;

    public async Task RunAsync(IPluginCommandContext context)
    {
        var settings = VoiceSettings.Load(context.DataDirectory);
        var form = new PluginForm
        {
            Title = "VOICEVOX の設定",
            OkText = "保存してつながるか確かめる",
            Fields =
            {
                new PluginTextField
                {
                    Key = "url", Label = "エンジンの URL", Value = settings.EngineUrl, Required = true, Placeholder = VoicevoxClient.DefaultUrl,
                    Note = $"VOICEVOX の既定は {VoicevoxClient.DefaultUrl} です。同じ API を持つエンジン (AivisSpeech など) の URL も指定できます。",
                },
            },
        };
        if (!await context.ShowFormAsync(form)) return;
        var url = form.Get<PluginTextField>("url").Value.Trim();
        if (VoicevoxClient.ParseUrl(url) == null)
        {
            await context.ShowMessageAsync("VOICEVOX", $"URL が正しくありません: {url}\n例: {VoicevoxClient.DefaultUrl}");
            return;
        }
        settings.EngineUrl = url;
        settings.Save(context.DataDirectory);

        var client = new VoicevoxClient(url);
        try
        {
            // バージョンだけでなく話者の一覧も読む (つながっても話者を読めないことがあるため)
            var (version, styles) = await context.RunWithProgressAsync("VOICEVOX", async (p, ct) =>
            {
                p.Report(new PluginProgress("VOICEVOX のエンジンにつないでいます..."));
                var v = await Plugin.WithTimeout(TimeSpan.FromSeconds(10), ct, client.GetVersionAsync);
                var s = await Plugin.WithTimeout(TimeSpan.FromSeconds(10), ct, client.GetStylesAsync);
                return (v, s);
            });
            int speakers = styles.Select(s => s.Speaker).Distinct().Count();
            await context.ShowMessageAsync("VOICEVOX", styles.Count > 0
                ? $"つながりました (エンジンのバージョン {version}、話者 {speakers} 人・スタイル {styles.Count} 個)。"
                : $"つながりましたが (エンジンのバージョン {version})、使える話者がありません。");
        }
        catch (VoicevoxException e)
        {
            await context.ShowMessageAsync("VOICEVOX", "設定は保存しました。\n" + e.Message);
        }
    }
}
