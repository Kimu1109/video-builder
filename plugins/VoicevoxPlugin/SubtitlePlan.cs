using VideoBuilder.Plugins;

namespace VoicevoxPlugin;

/// <summary>字幕を置くときに使うテンプレートと、セリフを入れる公開項目の表示名。</summary>
public sealed record SubtitleLook(IPluginTemplate Template, string Field);

/// <summary>
/// 話者ごとの字幕の見た目を、音声を作る前に決めておく (テンプレートがない・文字の項目がないときに、先に知らせるため)。
/// </summary>
public sealed class SubtitlePlan
{
    private readonly Dictionary<string, SubtitleLook?> _bySpeaker = new();

    /// <summary>テンプレートを使わない (文字だけの字幕)。</summary>
    public static readonly SubtitlePlan Plain = new();

    /// <summary>キャラクターの字幕の見た目。null なら文字だけ。</summary>
    public SubtitleLook? For(VoiceStyle style) => _bySpeaker.GetValueOrDefault(style.Speaker);

    /// <summary>
    /// speakers (キャラクター名) の字幕の見た目を決める。キャラクターごとの指定がなければ既定のもの。
    /// テンプレートがライブラリにない・セリフを入れる項目がないときは VoicevoxException。
    /// </summary>
    public static SubtitlePlan Resolve(IPluginCommandContext context, VoiceSettings settings, IEnumerable<string> speakers)
    {
        var plan = new SubtitlePlan();
        var loaded = new Dictionary<string, IPluginTemplate>();
        foreach (var speaker in speakers.Distinct())
        {
            var choice = settings.SpeakerTemplates.GetValueOrDefault(speaker) ?? settings.SubtitleTemplate;
            plan._bySpeaker[speaker] = choice is null or { IsPlain: true } ? null : Look(context, choice, loaded);
        }
        return plan;
    }

    private static SubtitleLook Look(IPluginCommandContext context, SubtitleTemplate choice, Dictionary<string, IPluginTemplate> loaded)
    {
        if (!loaded.TryGetValue(choice.Id, out var template))
        {
            template = context.LoadTemplate(choice.Id)
                ?? throw new VoicevoxException($"字幕のテンプレート「{choice.Name}」がライブラリにありません。「字幕のテンプレート...」で選び直してください。");
            loaded[choice.Id] = template;
        }
        return new SubtitleLook(template, FieldOf(template, choice.Field));
    }

    /// <summary>
    /// セリフを入れる項目の表示名。field が空なら、最初の文字の項目。見つからなければ VoicevoxException。
    /// </summary>
    public static string FieldOf(IPluginTemplate template, string field)
    {
        if (field.Length > 0)
        {
            if (template.Fields.Any(f => f.Label == field)) return field;
            var labels = template.Fields.Select(f => f.Label).Distinct().ToList();
            throw new VoicevoxException($"テンプレート「{template.Name}」に、項目「{field}」がありません。"
                + (labels.Count > 0 ? $"\n公開されている項目: {string.Join("、", labels)}" : ""));
        }
        return template.Fields.FirstOrDefault(f => f.IsText)?.Label
            ?? throw new VoicevoxException($"テンプレート「{template.Name}」に、文字の公開項目がありません。テンプレートを作るときに、テキストの「テキスト」を公開してください。");
    }
}
