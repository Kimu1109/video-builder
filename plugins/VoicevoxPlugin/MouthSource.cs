using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Plugins;
using VideoBuilder.Rendering;

namespace VoicevoxPlugin;

/// <summary>
/// 口パク立ち絵。同じシーンにある VOICEVOX の音声 (このプラグインで作ったもの) のうち、指定したキャラクターの声に合わせて、
/// 口の形 (閉・あ・い・う・え・お) の絵を切り替えて出す。音声を動かしたり作り直したりしても、合ったまま動く。
/// </summary>
public sealed class MouthSource : IVideoSource
{
    public const string Id = "voicevox.mouth";

    private static readonly string[] Images = EffectRegistry.ImageExtensions;

    public static EffectDefinition Definition { get; } = new()
    {
        Id = Id, Name = "口パク立ち絵 (VOICEVOX)", Category = EffectCategory.VideoSource, Icon = "Icon.Image", Color = "#3A7F5A",
        Params = new[]
        {
            new ParamDefinition { Key = "character", Name = "キャラクター", Kind = ParamKind.Text, DefaultText = "" },
            new ParamDefinition { Key = "closed", Name = "口を閉じた絵", Kind = ParamKind.File, FileExtensions = Images },
            new ParamDefinition { Key = "a", Name = "「あ」の口", Kind = ParamKind.File, FileExtensions = Images },
            new ParamDefinition { Key = "i", Name = "「い」の口", Kind = ParamKind.File, FileExtensions = Images },
            new ParamDefinition { Key = "u", Name = "「う」の口", Kind = ParamKind.File, FileExtensions = Images },
            new ParamDefinition { Key = "e", Name = "「え」の口", Kind = ParamKind.File, FileExtensions = Images },
            new ParamDefinition { Key = "o", Name = "「お」の口", Kind = ParamKind.File, FileExtensions = Images },
        },
    };

    public SKImage? Render(EffectArgs args)
    {
        var mouth = CurrentMouth(args.Context.Project, args.Object, args.Effect.GetString("character"), args.Frame);
        var path = ImageFor(args.Effect, mouth);
        return path.Length > 0 ? args.Context.LoadImage?.Invoke(path) : null;
    }

    /// <summary>
    /// 口の形の絵のファイル。その形の絵がなければ「あ」の絵、それもなければ閉じた絵
    /// (閉じた絵と「あ」の絵の 2 枚だけでも、開け閉めで口パクできる)。閉じた口で閉じた絵がなければ、ほかのどれか。
    /// </summary>
    public static string ImageFor(Effect effect, Mouth mouth)
    {
        string Get(string key) => effect.GetString(key) is { Length: > 0 } s ? s : "";
        string closed = Get("closed");
        if (mouth == Mouth.Closed)
            return closed.Length > 0 ? closed : new[] { "a", "i", "u", "e", "o" }.Select(Get).FirstOrDefault(s => s.Length > 0) ?? "";
        var own = Get(mouth.ToString().ToLowerInvariant());
        if (own.Length > 0) return own;
        var a = Get("a");
        return a.Length > 0 ? a : closed;
    }

    /// <summary>
    /// 立ち絵 (owner) と同じシーンで、frame に鳴っている character の VOICEVOX の音声から、口の形を決める。
    /// character が空なら、どのキャラクターの声でもよい。いくつも鳴っていれば、口を開けているものを優先する。
    /// </summary>
    public static Mouth CurrentMouth(Project project, TimelineObject owner, string character, int frame)
    {
        var scene = project.Scenes.FirstOrDefault(s => s.Objects.Contains(owner)) ?? project.Root;
        var want = ScriptParser.Normalize(character ?? "");
        var result = Mouth.Closed;
        foreach (var o in scene.Objects)
        {
            if (!o.Enabled || !o.Contains(frame) || o.Main.DefinitionId != EffectRegistry.Audio) continue;
            if (o.PluginData?.GetValueOrDefault(VoiceInfo.KeyLips) is not { Length: > 0 } lips) continue;
            if (want.Length > 0 && ScriptParser.Normalize(VoiceInfo.CharacterOf(o) ?? "") != want) continue;
            if (!scene.GetLayer(o.Layer).Visible) continue;
            var mouth = LipSync.Decode(lips).At(AudioSeconds(project, o, frame));
            if (mouth != Mouth.Closed) return mouth;
            result = mouth;
        }
        return result;
    }

    /// <summary>音声オブジェクト o の frame の位置が、音声ファイルの何秒目にあたるか (再生位置・分割・再生速度を含む)。</summary>
    public static double AudioSeconds(Project project, TimelineObject o, int frame)
    {
        var bounds = o.Bounds;
        double speed = Math.Max(0.01, o.Main.Eval("speed", bounds, frame) / 100.0);
        double start = o.Main.Eval("position", bounds, o.Start) + project.FrameToSeconds(o.SourceOffsetFrames);
        return start + project.FrameToSeconds(frame - o.Start) * speed;
    }
}
