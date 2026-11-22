using System.Text.Json.Serialization;

namespace VideoBuilder.Core;

/// <summary>タイムライン上のオブジェクト。Start / End はフレーム番号で、End を含む。</summary>
public sealed class TimelineObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public int Layer { get; set; }
    public int Start { get; set; }
    public int End { get; set; }
    /// <summary>中間点 (絶対フレーム, Start より大きく End 以下, 昇順)。</summary>
    public List<int> KeyFrames { get; set; } = new();
    /// <summary>先頭がメイン効果 (動画ファイルなど)、末尾が標準描画 / 標準再生。</summary>
    public List<Effect> Effects { get; set; } = new();
    /// <summary>映像と音声のペアなど、一緒に動かすオブジェクトのグループ。</summary>
    public string? GroupId { get; set; }
    /// <summary>「複数のオブジェクトとして置いた」テンプレートのどれか (Scene.TemplateGroups の Id)。ないときは null。</summary>
    public string? TemplateGroup { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// プラグインがオブジェクトに残す情報 (キーは "プラグインの ID.名前" にする)。プロジェクトと一緒に保存され、複製にも引き継がれる。
    /// 使わないときは null (保存されない)。
    /// </summary>
    public Dictionary<string, string>? PluginData { get; set; }

    [JsonIgnore] public int Length => End - Start + 1;
    [JsonIgnore] public int Sections => KeyFrames.Count + 1;
    [JsonIgnore] public Effect Main => Effects[0];
    [JsonIgnore] public EffectDefinition Definition => Effects[0].Definition;

    /// <summary>区間境界 (開始, 中間点..., 終了)。</summary>
    [JsonIgnore]
    public int[] Bounds
    {
        get
        {
            var b = new int[KeyFrames.Count + 2];
            b[0] = Start;
            for (int i = 0; i < KeyFrames.Count; i++) b[i + 1] = KeyFrames[i];
            b[^1] = End;
            return b;
        }
    }

    public Effect? FindEffect(string definitionId) => Effects.FirstOrDefault(e => e.DefinitionId == definitionId);

    [JsonIgnore] public Effect? Draw => Effects.Count > 0 && Effects[^1].Definition.Category == EffectCategory.Draw ? Effects[^1] : null;

    /// <summary>フィルタを差し込む位置 (標準描画・標準再生の手前)。</summary>
    [JsonIgnore]
    public int FilterInsertIndex
    {
        get
        {
            if (Effects.Count == 0) return 0;
            var cat = Effects[^1].Definition.Category;
            return cat is EffectCategory.Draw or EffectCategory.AudioPlay && Effects.Count > 1 ? Effects.Count - 1 : Effects.Count;
        }
    }

    public bool Contains(int frame) => frame >= Start && frame <= End;

    public TimelineObject Clone(bool newId = false)
    {
        var o = new TimelineObject
        {
            Id = newId ? Guid.NewGuid().ToString("N")[..12] : Id,
            Layer = Layer, Start = Start, End = End, GroupId = GroupId, Enabled = Enabled, TemplateGroup = TemplateGroup,
            SourceOffsetFrames = SourceOffsetFrames,
            KeyFrames = new List<int>(KeyFrames),
            PluginData = PluginData != null ? new Dictionary<string, string>(PluginData) : null,
        };
        foreach (var e in Effects) o.Effects.Add(e.Clone());
        return o;
    }

    /// <summary>a と b の内容がまったく同じか (ID も含む)。複製を使い回せるかの判断に使う。</summary>
    public static bool SameContent(TimelineObject a, TimelineObject b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Id != b.Id || a.Layer != b.Layer || a.Start != b.Start || a.End != b.End || a.Enabled != b.Enabled
            || a.GroupId != b.GroupId || a.TemplateGroup != b.TemplateGroup || a.SourceOffsetFrames != b.SourceOffsetFrames
            || a.KeyFrames.Count != b.KeyFrames.Count || a.Effects.Count != b.Effects.Count) return false;
        for (int i = 0; i < a.KeyFrames.Count; i++) if (a.KeyFrames[i] != b.KeyFrames[i]) return false;
        for (int i = 0; i < a.Effects.Count; i++) if (!Effect.SameContent(a.Effects[i], b.Effects[i])) return false;
        return SamePluginData(a.PluginData, b.PluginData);
    }

    private static bool SamePluginData(Dictionary<string, string>? a, Dictionary<string, string>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        int na = a?.Count ?? 0, nb = b?.Count ?? 0;
        if (na != nb) return false;
        if (na == 0) return true;
        foreach (var (k, v) in a!) if (!b!.TryGetValue(k, out var w) || w != v) return false;
        return true;
    }

    /// <summary>src の内容 (ID 以外) を写す。ドラッグ中に「元の状態からやり直す」ために使う。</summary>
    public void RestoreFrom(TimelineObject src)
    {
        Layer = src.Layer; Start = src.Start; End = src.End; GroupId = src.GroupId; Enabled = src.Enabled; TemplateGroup = src.TemplateGroup;
        SourceOffsetFrames = src.SourceOffsetFrames;
        KeyFrames = new List<int>(src.KeyFrames);
        Effects = src.Effects.Select(e => e.Clone()).ToList();
        PluginData = src.PluginData != null ? new Dictionary<string, string>(src.PluginData) : null;
    }

    /// <summary>位置と長さを変える。中間点は相対位置を保ったまま移動し、範囲外になったものは消す。</summary>
    public void SetRange(int start, int end)
    {
        if (end < start) end = start;
        int delta = start - Start;
        bool moved = end - start == End - Start;
        var oldBounds = Bounds;
        Start = start; End = end;
        if (moved)
        {
            for (int i = 0; i < KeyFrames.Count; i++) KeyFrames[i] += delta;
            return;
        }
        // 長さが変わった場合: 範囲外の中間点を消す (値も合わせて削除)
        for (int i = KeyFrames.Count - 1; i >= 0; i--)
        {
            if (KeyFrames[i] <= Start || KeyFrames[i] > End) RemoveKeyFrameAt(i);
        }
    }

    /// <summary>中間点を追加する。追加位置の値は、その時点の補間値で埋める。</summary>
    public bool AddKeyFrame(int frame)
    {
        if (frame <= Start || frame > End || KeyFrames.Contains(frame)) return false;
        var bounds = Bounds;
        int index = 0;
        while (index < KeyFrames.Count && KeyFrames[index] < frame) index++;
        foreach (var e in Effects)
        foreach (var v in e.Tracks.Values)
        {
            v.EnsureSections(Sections);
            double value = v.IsMoving && (v.Custom != null || v.Mode != MovementMode.Delta) ? v.Evaluate(bounds, frame) : v.Values[index];
            v.Values.Insert(index + 1, value);
        }
        KeyFrames.Insert(index, frame);
        return true;
    }

    public void RemoveKeyFrameAt(int index)
    {
        if (index < 0 || index >= KeyFrames.Count) return;
        foreach (var e in Effects)
        foreach (var v in e.Tracks.Values)
        {
            v.EnsureSections(Sections);
            v.Values.RemoveAt(index + 1);
        }
        KeyFrames.RemoveAt(index);
    }

    /// <summary>frame が属する区間の番号 (0 始まり)。</summary>
    public int SectionAt(int frame)
    {
        int i = 0;
        while (i < KeyFrames.Count && frame >= KeyFrames[i]) i++;
        return i;
    }

    /// <summary>
    /// frame の位置で値を書き換えるときに、Values のどの要素を変えるか。
    /// 移動なしなら今の区間の値、移動ありなら区間の両端のうち近い方。
    /// </summary>
    public int ValueIndexAt(AnimatedValue value, int frame)
    {
        int section = SectionAt(Math.Clamp(frame, Start, End));
        if (!value.IsMoving) return section;
        var b = Bounds;
        return frame - b[section] <= b[section + 1] - frame ? section : section + 1;
    }

    /// <summary>frame の位置で 2 つに分割し、後半の新しいオブジェクトを返す。</summary>
    public TimelineObject? SplitAt(int frame)
    {
        if (frame <= Start || frame > End) return null;
        AddKeyFrame(frame);
        int k = KeyFrames.IndexOf(frame);
        var second = Clone(newId: true);
        // 後半: 区間 k+1 以降
        second.Start = frame;
        second.KeyFrames = KeyFrames.Skip(k + 1).ToList();
        foreach (var e in second.Effects)
        foreach (var v in e.Tracks.Values)
            v.Values = v.Values.Skip(k + 1).ToList();
        // 前半: 区間 0..k
        int firstEnd = frame - 1;
        var keep = KeyFrames.Take(k).ToList();
        foreach (var e in Effects)
        foreach (var v in e.Tracks.Values)
            v.Values = v.Values.Take(k + 2).ToList();
        KeyFrames = keep;
        End = firstEnd;
        // 動画・音声は再生位置をずらして、分割後も同じ絵・音が続くようにする
        second.SourceOffsetFrames += frame - Start;
        return second;
    }

    /// <summary>素材の再生開始位置のずれ (フレーム)。分割したときに後半へ設定する。</summary>
    public int SourceOffsetFrames { get; set; }
}
