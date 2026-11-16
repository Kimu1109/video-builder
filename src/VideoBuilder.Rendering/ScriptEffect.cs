using System.Collections.Concurrent;
using MoonSharp.Interpreter;
using VideoBuilder.Core;

namespace VideoBuilder.Rendering;

/// <summary>
/// スクリプト制御: Lua でオブジェクトの位置・回転・拡大率・不透明度を毎フレーム書き換える。
/// AviUtl のスクリプトと同じ名前 (obj.ox, obj.time など) の一部を使える。ファイルや OS には触れない。
/// </summary>
public sealed class ScriptEffect : IVideoEffect
{
    public string Id => "filter.script";

    /// <summary>1 回の実行で許す命令数。無限ループで描画が止まらないようにする。</summary>
    private const int InstructionLimit = 2_000_000;

    private sealed class Compiled
    {
        public Script Script = null!;
        public DynValue? Function;
        public Table Obj = null!;
        public string? Error;
    }

    // 描画スレッドごとにコンパイル結果を持つ (プレビューと出力が同時に動いても競合しない)
    [ThreadStatic] private static Dictionary<string, Compiled>? _cache;

    private static readonly ConcurrentDictionary<string, string> Errors = new();

    /// <summary>オブジェクトのスクリプトで直近に起きたエラー。なければ null。</summary>
    public static string? ErrorOf(string objectId) => Errors.GetValueOrDefault(objectId);

    public Sprite Apply(Sprite s, EffectArgs a)
    {
        if (a.IsFrameBuffer) return s;
        string code = a.Effect.GetString("code");
        if (string.IsNullOrWhiteSpace(code)) { Errors.TryRemove(a.Object.Id, out _); return s; }

        _cache ??= new Dictionary<string, Compiled>();
        if (!_cache.TryGetValue(code, out var c))
        {
            if (_cache.Count > 64) _cache.Clear();
            c = new Compiled { Script = new Script(CoreModules.Preset_HardSandbox) };
            c.Obj = new Table(c.Script);
            c.Script.Globals["obj"] = c.Obj;
            try { c.Function = c.Script.LoadString(code, null, "script"); }
            catch (InterpreterException ex) { c.Error = ex.DecoratedMessage ?? ex.Message; }
            _cache[code] = c;
        }
        if (c.Function == null) { Errors[a.Object.Id] = c.Error ?? "スクリプトを読み込めません"; return s; }

        var ctx = a.Context;
        var p = ctx.Project;
        var o = a.Object;
        var obj = c.Obj;
        var draw = o.Draw;
        double D(string key) => draw?.Eval(key, a.Bounds, ctx.Frame) ?? 0;
        var m = ctx.Modifiers;
        obj["ox"] = m.Ox; obj["oy"] = m.Oy; obj["oz"] = m.Oz;
        obj["rx"] = m.Rx; obj["ry"] = m.Ry; obj["rz"] = m.Rz;
        obj["zoom"] = m.Zoom; obj["alpha"] = m.Alpha;
        obj["x"] = D("x"); obj["y"] = D("y"); obj["z"] = D("z");
        obj["w"] = s.Width; obj["h"] = s.Height;
        obj["frame"] = ctx.Frame - o.Start;
        obj["totalframe"] = o.Length;
        obj["time"] = p.FrameToSeconds(ctx.Frame - o.Start);
        obj["totaltime"] = p.FrameToSeconds(o.Length);
        obj["framerate"] = p.Settings.Fps.Value;
        obj["layer"] = o.Layer + 1;
        obj["screen_w"] = p.Settings.Width; obj["screen_h"] = p.Settings.Height;
        int frame = ctx.Frame;
        obj["rand"] = (Func<double, double, double?, double?, double>)((min, max, seed, f) =>
        {
            // 同じ引数なら同じ値を返す (プレビューと出力で結果が変わらないように)
            unchecked
            {
                uint h = (uint)((long)(seed ?? o.Layer + 1) * 73856093 ^ (long)(f ?? frame) * 19349663);
                h ^= h >> 16; h *= 0x7feb352d; h ^= h >> 15; h *= 0x846ca68b; h ^= h >> 16;
                if (max < min) (min, max) = (max, min);
                return Math.Floor(min + h / (double)uint.MaxValue * (max - min + 1 - 1e-9));
            }
        });

        try
        {
            var co = c.Script.CreateCoroutine(c.Function);
            co.Coroutine.AutoYieldCounter = InstructionLimit;
            co.Coroutine.Resume();
            if (co.Coroutine.State == CoroutineState.ForceSuspended)
            {
                Errors[o.Id] = "処理が長すぎるため中断しました (無限ループになっていませんか?)";
                return s;
            }
            float F(string key, float fallback)
            {
                var v = obj.Get(key);
                return v.Type == DataType.Number && !double.IsNaN(v.Number) && !double.IsInfinity(v.Number) ? (float)v.Number : fallback;
            }
            ctx.Modifiers = new DrawModifiers
            {
                Ox = F("ox", m.Ox), Oy = F("oy", m.Oy), Oz = F("oz", m.Oz),
                Rx = F("rx", m.Rx), Ry = F("ry", m.Ry), Rz = F("rz", m.Rz),
                Zoom = F("zoom", m.Zoom), Alpha = Math.Clamp(F("alpha", m.Alpha), 0, 1),
            };
            Errors.TryRemove(o.Id, out _);
        }
        catch (InterpreterException ex) { Errors[o.Id] = ex.DecoratedMessage ?? ex.Message; }
        catch (Exception ex) { Errors[o.Id] = ex.Message; }
        return s;
    }
}
