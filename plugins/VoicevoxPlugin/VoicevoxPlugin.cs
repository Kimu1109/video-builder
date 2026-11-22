using System.Globalization;
using VideoBuilder.Core;
using VideoBuilder.Plugins;

namespace VoicevoxPlugin;

/// <summary>
/// VOICEVOX 連携。VOICEVOX のエンジン (別に起動しておく) にセリフを送って音声を作り、再生位置に置く。
/// 字幕 (テキストオブジェクト) も一緒に置ける。
/// </summary>
public sealed class Plugin : IPlugin
{
    public const string Group = "VOICEVOX";

    public void Initialize(IPluginHost host)
    {
        // コマンドは API バージョン 3 から (古いアプリでは何も登録しない)
        if (host.ApiVersion < 3) { host.Log("このアプリのプラグイン API が古いため、使えません (3 以上が必要)"); return; }
        host.RegisterCommand(new SynthesizeCommand());
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
            var styles = await context.RunWithProgressAsync("VOICEVOX", (p, ct) =>
            {
                p.Report(new PluginProgress("VOICEVOX のエンジンにつないでいます..."));
                return Plugin.WithTimeout(TimeSpan.FromSeconds(10), ct, client.GetStylesAsync);
            });
            if (styles.Count == 0)
            {
                await context.ShowMessageAsync("VOICEVOX", "使える話者がありません。VOICEVOX のエンジンを確かめてください。");
                return;
            }

            var form = BuildForm(context, settings, styles);
            if (!await context.ShowFormAsync(form)) return;
            var request = ReadForm(form, styles);
            settings.StyleId = request.Style.Id;
            settings.Voice = request.Voice;
            settings.AddSubtitle = request.Subtitle;
            settings.Save(context.DataDirectory);

            var dir = VoiceFiles.Directory(context);
            var path = await context.RunWithProgressAsync("VOICEVOX", async (p, ct) =>
            {
                p.Report(new PluginProgress($"{request.Style.Label} の声を作っています..."));
                var wav = await Plugin.WithTimeout(TimeSpan.FromMinutes(5), ct, t => client.SynthesizeAsync(request.Text, request.Style.Id, request.Voice, t));
                Directory.CreateDirectory(dir);
                var file = VoiceFiles.NewPath(dir, request.Style.Speaker, request.Text, DateTime.Now);
                await File.WriteAllBytesAsync(file, wav, ct);
                return file;
            });

            if (!WavInfo.TryGetDuration(await File.ReadAllBytesAsync(path), out var seconds))
                throw new VoicevoxException("作った音声を読めませんでした。");
            var placed = VoicePlacer.Place(context, path, seconds, request.Text, request.Style, request.Voice, request.Subtitle);
            if (placed == null)
                await context.ShowMessageAsync("VOICEVOX", $"再生位置に空いているレイヤーがないため、置けませんでした。\n音声は次の場所に保存しました:\n{path}");
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
        int styleId = settings.StyleId is { } id && styles.Any(s => s.Id == id) ? id : styles[0].Id;
        var v = settings.Voice.Clamped();
        return new PluginForm
        {
            Title = "VOICEVOX: セリフから音声を作る",
            OkText = "作って置く",
            Width = 480,
            Message = context.ProjectDirectory == null
                ? "プロジェクトをまだ保存していないため、音声はアプリのデータフォルダに保存します。先にプロジェクトを保存すると、プロジェクトの隣の voices フォルダに保存します。"
                : null,
            Fields =
            {
                new PluginTextField { Key = "text", Label = "セリフ", Multiline = true, Required = true, Value = selectedText, Placeholder = "しゃべらせたいセリフ" },
                new PluginChoiceField
                {
                    Key = "style", Label = "話者",
                    Value = styleId.ToString(CultureInfo.InvariantCulture),
                    Options = styles.Select(s => new PluginChoice(s.Id.ToString(CultureInfo.InvariantCulture), s.Label)).ToList(),
                    Note = "動画には「VOICEVOX:キャラクター名」のクレジットが必要です。キャラクターごとの利用規約も確かめてください。",
                },
                new PluginNumberField { Key = "speed", Label = "話速", Value = v.Speed, Minimum = 0.5, Maximum = 2, Decimals = 2 },
                new PluginNumberField { Key = "pitch", Label = "音高", Value = v.Pitch, Minimum = -0.15, Maximum = 0.15, Decimals = 2 },
                new PluginNumberField { Key = "intonation", Label = "抑揚", Value = v.Intonation, Minimum = 0, Maximum = 2, Decimals = 2 },
                new PluginNumberField { Key = "volume", Label = "音量", Value = v.Volume, Minimum = 0, Maximum = 2, Decimals = 2 },
                new PluginNumberField { Key = "pre", Label = "前の無音 (秒)", Value = v.PreSilence, Minimum = 0, Maximum = 1.5, Decimals = 2 },
                new PluginNumberField { Key = "post", Label = "後の無音 (秒)", Value = v.PostSilence, Minimum = 0, Maximum = 1.5, Decimals = 2 },
                new PluginCheckField { Key = "subtitle", Label = "字幕も置く", Value = settings.AddSubtitle, Note = "セリフのテキストを音声と同じ長さで置き、音声とグループにします。" },
            },
        };
    }

    public static Request ReadForm(PluginForm form, IReadOnlyList<VoiceStyle> styles)
    {
        var text = form.Get<PluginTextField>("text").Value.Trim();
        if (text.Length == 0) throw new VoicevoxException("セリフが空です。");
        var id = int.TryParse(form.Get<PluginChoiceField>("style").Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : styles[0].Id;
        var style = styles.FirstOrDefault(s => s.Id == id) ?? styles[0];
        double Num(string key) => form.Get<PluginNumberField>(key).Value;
        var voice = new VoiceParams
        {
            Speed = Num("speed"), Pitch = Num("pitch"), Intonation = Num("intonation"), Volume = Num("volume"),
            PreSilence = Num("pre"), PostSilence = Num("post"),
        }.Clamped();
        return new Request(text, style, voice, form.Get<PluginCheckField>("subtitle").Value);
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
            var version = await context.RunWithProgressAsync("VOICEVOX", (p, ct) =>
            {
                p.Report(new PluginProgress("VOICEVOX のエンジンにつないでいます..."));
                return Plugin.WithTimeout(TimeSpan.FromSeconds(10), ct, client.GetVersionAsync);
            });
            await context.ShowMessageAsync("VOICEVOX", $"つながりました (エンジンのバージョン {version})。");
        }
        catch (VoicevoxException e)
        {
            await context.ShowMessageAsync("VOICEVOX", "設定は保存しました。\n" + e.Message);
        }
    }
}
