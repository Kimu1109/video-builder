using VideoBuilder.Plugins;

namespace VoicevoxPlugin;

/// <summary>
/// ポンポン置くモード。開いたままの入力パネルにセリフを入れて Enter を押すと、裏で音声を作って再生位置に置き、
/// 再生位置を置いた音声の後ろへ進める。作っている間も次のセリフを入力できる (入れた順に置く)。
/// </summary>
public sealed class QuickCommand : IPluginCommand
{
    public string Id => "voicevox.quick";
    public string Name => "ポンポン置くモード...";
    public string? Group => Plugin.Group;

    public async Task RunAsync(IPluginCommandContext context)
    {
        var settings = VoiceSettings.Load(context.DataDirectory);
        var client = new VoicevoxClient(settings.EngineUrl);
        try
        {
            if (await Plugin.LoadStylesAsync(context, client) is not { } styles) return;
            var quick = new QuickSession(context, client, styles, settings);
            quick.Open();
        }
        catch (VoicevoxException e)
        {
            await context.ShowMessageAsync("VOICEVOX", e.Message);
        }
    }
}

/// <summary>ポンポン置くモードの入力パネル 1 つぶんの状態 (待ち行列と、置いていくレイヤー)。</summary>
public sealed class QuickSession
{
    public const string PanelId = "voicevox.quick";

    private readonly IPluginCommandContext _context;
    private readonly VoicevoxClient _client;
    private readonly IReadOnlyList<VoiceStyle> _styles;
    private readonly Queue<Item> _pending = new();
    private IPluginPanel? _panel;
    private string? _last;
    private int? _layer;

    private sealed record Item(ScriptLine Line, VoiceParams Voice, bool Subtitle, double GapSeconds);

    public PluginPanel Spec { get; }

    public QuickSession(IPluginCommandContext context, VoicevoxClient client, IReadOnlyList<VoiceStyle> styles, VoiceSettings settings)
    {
        _context = context;
        _client = client;
        _styles = styles;
        Spec = new PluginPanel
        {
            Id = PanelId,
            Title = "VOICEVOX: ポンポン置く",
            Width = 480,
            FocusKey = "text",
            Message = "セリフを入れて Enter を押すと、再生位置に置いて、再生位置をその後ろへ進めます。作っている間も、続けて入力できます。"
                      + "「ずんだもん: こんにちは」のように書くと、その話者で読みます。話速などは前回の値を使います。",
            Fields =
            {
                new PluginTextField { Key = "text", Label = "セリフ", Required = true, Placeholder = "セリフを入れて Enter" },
                Plugin.StyleField("style", "話者", styles, settings.StyleId),
                new PluginNumberField { Key = "gap", Label = "セリフの間 (秒)", Value = settings.ScriptGap, Minimum = 0, Maximum = 10, Decimals = 2 },
                new PluginCheckField { Key = "subtitle", Label = "字幕も置く", Value = settings.AddSubtitle },
            },
            Buttons =
            {
                new PluginPanelButton { Text = "置く", IsDefault = true, OnClick = p => { Submit(p); return Task.CompletedTask; } },
                new PluginPanelButton { Text = "待ちを取り消す", CheckRequired = false, OnClick = p => { _panel = p; CancelPending(); return Task.CompletedTask; } },
            },
        };
    }

    /// <summary>作っている・待っているセリフの数。</summary>
    public int Pending => _pending.Count;
    /// <summary>置いた数。</summary>
    public int Placed { get; private set; }
    /// <summary>待ち行列の処理 (終わっていれば完了したタスク)。</summary>
    public Task Running { get; private set; } = Task.CompletedTask;
    /// <summary>最後に置いた (または失敗した) ときの知らせ。</summary>
    public string? Last => _last;

    /// <summary>入力パネルを出す。</summary>
    public IPluginPanel Open() => _panel = _context.ShowPanel(Spec);

    /// <summary>
    /// 「置く」: 入力されたセリフ (Spec の項目に書き戻されている) を待ち行列に入れ、入力欄を空にしてすぐ戻る。
    /// 待ち行列を処理していなければ、処理を始める。
    /// </summary>
    public void Submit(IPluginPanel panel)
    {
        _panel = panel;
        var text = Spec.Get<PluginTextField>("text").Value;
        var style = Plugin.ReadStyle(Spec.Get<PluginChoiceField>("style").Value, _styles) ?? _styles[0];
        bool subtitle = Spec.Get<PluginCheckField>("subtitle").Value;
        double gap = Math.Clamp(Spec.Get<PluginNumberField>("gap").Value, 0, 10);
        var lines = ScriptParser.Parse(text, _styles, style);
        if (lines.Count == 0) return;

        // 選んだ話者などを覚えておく (次に開いたときにも使う)
        var settings = VoiceSettings.Load(_context.DataDirectory);
        settings.StyleId = style.Id;
        settings.AddSubtitle = subtitle;
        settings.ScriptGap = gap;
        try { settings.Save(_context.DataDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { }

        foreach (var line in lines) _pending.Enqueue(new Item(line, settings.Voice, subtitle, gap));
        panel.SetValue("text", "");
        panel.Focus("text");
        UpdateStatus();
        if (Running.IsCompleted) Running = ProcessAsync();
    }

    /// <summary>まだ作り始めていないセリフを取り消す (作っている途中のものは置く)。</summary>
    public void CancelPending()
    {
        int n = Math.Max(0, _pending.Count - (Running.IsCompleted ? 0 : 1));
        var keep = Running.IsCompleted ? Array.Empty<Item>() : _pending.Take(1).ToArray();
        _pending.Clear();
        foreach (var item in keep) _pending.Enqueue(item);
        if (n > 0) _last = $"待っていた {n} 件を取り消しました。";
        UpdateStatus();
    }

    private async Task ProcessAsync()
    {
        while (_pending.Count > 0)
        {
            var item = _pending.Peek();
            UpdateStatus();
            try { _last = await PlaceOneAsync(item); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _last = $"エラー (「{Short(item.Line.Text)}」): {e.Message}";
            }
            if (_pending.Count > 0 && ReferenceEquals(_pending.Peek(), item)) _pending.Dequeue();
            UpdateStatus();
        }
    }

    /// <summary>1 つを作って再生位置に置き、再生位置を後ろへ進める。結果の知らせを返す。</summary>
    private async Task<string> PlaceOneAsync(Item item)
    {
        var line = item.Line;
        var settings = VoiceSettings.Load(_context.DataDirectory);
        var plan = Plugin.PlanSubtitles(_context, settings, item.Subtitle, new[] { line.Style });
        var dir = VoiceFiles.Directory(_context);
        var clip = await Plugin.SynthesizeToFileAsync(_client, dir, line.Text, line.Style, item.Voice, CancellationToken.None);

        // 利用者がドラッグなどで編集している途中なら、終わるまで待つ
        while (!_context.CanEdit) await Task.Delay(100);
        var placed = VoicePlacer.PlaceMany(_context, new[] { clip }, 0, item.Subtitle, "VOICEVOX の音声を置く", plan, _layer);
        if (placed == null)
            return $"再生位置に空いているレイヤーがないため、「{Short(line.Text)}」を置けませんでした (音声は {dir} に保存しました)。";
        var p = placed[0];
        // 次も同じレイヤーに並べる (選んだものが変わって、1 段ずつ下にずれていかないように)
        _layer = p.Subtitle?.Layer ?? p.Audio.Layer;
        int gapFrames = (int)Math.Round(item.GapSeconds * _context.Project.Settings.Fps.Value);
        _context.Seek(p.Audio.End + 1 + gapFrames);
        Placed++;
        return $"置きました: {line.Style.Label}「{Short(line.Text)}」";
    }

    private void UpdateStatus()
    {
        if (_panel is not { IsOpen: true } panel) return;
        var lines = new List<string>();
        if (_pending.Count > 0)
            lines.Add($"作っています: 「{Short(_pending.Peek().Line.Text)}」" + (_pending.Count > 1 ? $" (あと {_pending.Count - 1} 件)" : ""));
        if (_last != null) lines.Add(_last);
        panel.SetStatus(lines.Count > 0 ? string.Join("\n", lines) : null);
    }

    private static string Short(string s) => s.Length <= 20 ? s : s[..20] + "…";
}
