using System.Numerics;
using VideoBuilder.Core;

namespace VideoBuilder.Rendering;

/// <summary>
/// オブジェクトの動きのうち、ほかのオブジェクトや前後のフレームを見て決めるもの:
/// 親オブジェクト (親のレイヤーにあるオブジェクトの位置に付いていく) と、進む向きに回転。
/// 描画 (RenderEngine) とプレビューの移動の軌跡 (モーションパス) の両方で使う。
/// </summary>
public static class ObjectMotion
{
    private const int MaxParentDepth = 8;

    /// <summary>
    /// 親による変換 (オブジェクトの位置 → 画面の中心を原点にした位置)。親がなければ単位行列。
    /// 親の親もたどる (循環していても MaxParentDepth で止まる)。グループ制御・カメラは含まない。
    /// </summary>
    public static Matrix4x4 ParentTransform(Scene scene, Project project, TimelineObject obj, int frame) =>
        Parent(scene, project, obj, frame, 0);

    private static Matrix4x4 Parent(Scene scene, Project project, TimelineObject obj, int frame, int depth)
    {
        var draw = obj.Draw;
        if (draw == null || depth >= MaxParentDepth) return Matrix4x4.Identity;
        var bounds = obj.Bounds;
        int f = Math.Clamp(frame, obj.Start, obj.End);
        int layer = (int)Math.Round(draw.Eval("parent", bounds, f)) - 1;   // 表示のレイヤー番号 (1 始まり)
        if (layer < 0 || layer == obj.Layer) return Matrix4x4.Identity;
        int pf = frame - project.SecondsToFrames(Math.Max(0, draw.Eval("parentDelay", bounds, f)));
        var parent = FindOn(scene, layer, pf);
        var pd = parent?.Draw;
        if (parent == null || pd == null) return Matrix4x4.Identity;
        var pb = parent.Bounds;
        int cf = Math.Clamp(pf, parent.Start, parent.End);
        float E(string k) => (float)pd.Eval(k, pb, cf);
        var move = Matrix4x4.CreateTranslation(E("x"), E("y"), E("z"));
        var m = draw.GetBool("parentInherit")
            ? Matrix4x4.CreateScale(E("scale") / 100f, E("scale") / 100f, 1) * Matrix4x4.CreateRotationZ(E("rotation") * MathF.PI / 180f) * move
            : move;
        return m * Parent(scene, project, parent, pf, depth + 1);
    }

    /// <summary>
    /// layer にあって frame の時刻にいるオブジェクト。いなければ、その前にいた最後のもの (なければ最初のもの)。
    /// 親がいなくなっても、子が最後の位置に残るように。
    /// </summary>
    private static TimelineObject? FindOn(Scene scene, int layer, int frame)
    {
        TimelineObject? before = null, after = null;
        foreach (var o in scene.Objects)
        {
            if (o.Layer != layer || o.Draw == null) continue;
            if (o.Contains(frame)) return o;
            if (o.End < frame) { if (before == null || o.End > before.End) before = o; }
            else if (after == null || o.Start < after.Start) after = o;
        }
        return before ?? after;
    }

    /// <summary>
    /// 「進む向きに回転」の角度 (度。右へ進むと 0、下へ進むと 90)。前後のフレームの X・Y の差から求める。
    /// 止まっているときは、近くの動いているフレームの向き (最大 ±30 フレーム)。ずっと止まっていれば 0。
    /// </summary>
    public static float OrientAngle(TimelineObject obj, IReadOnlyList<int> bounds, int frame)
    {
        var draw = obj.Draw;
        if (draw == null) return 0;
        (double X, double Y) At(int f)
        {
            f = Math.Clamp(f, obj.Start, obj.End);
            return (draw.Eval("x", bounds, f), draw.Eval("y", bounds, f));
        }
        for (int reach = 1; reach <= 30; reach++)
        {
            // 前後どちらかに少しずつ広げて、動きがあるところを探す (始めと終わりでは片側だけになる)
            var a = At(frame - reach);
            var b = At(frame + reach);
            double dx = b.X - a.X, dy = b.Y - a.Y;
            if (dx * dx + dy * dy > 1e-6) return (float)(Math.Atan2(dy, dx) * 180 / Math.PI);
        }
        return 0;
    }

    /// <summary>
    /// 移動の軌跡 (モーションパス): オブジェクトの始めから終わりまで、step フレームごとの画面上の位置 (左上が原点)。
    /// 親の動きは含める。グループ制御・カメラ・効果 (アニメーション効果など) の動きは含めない。
    /// </summary>
    public static List<(int Frame, float X, float Y)> Path(Scene scene, Project project, TimelineObject obj, int step = 1)
    {
        var result = new List<(int, float, float)>();
        var draw = obj.Draw;
        if (draw == null) return result;
        var bounds = obj.Bounds;
        float cx = project.Settings.Width / 2f, cy = project.Settings.Height / 2f;
        step = Math.Max(1, step);
        for (int f = obj.Start; ; f = Math.Min(obj.End, f + step))
        {
            result.Add(PointAt(scene, project, obj, bounds, f, cx, cy));
            if (f >= obj.End) break;
        }
        return result;
    }

    /// <summary>frame での画面上の位置 (左上が原点、親の動きを含む)。</summary>
    public static (int Frame, float X, float Y) PointAt(Scene scene, Project project, TimelineObject obj, IReadOnlyList<int> bounds, int frame, float cx, float cy)
    {
        var draw = obj.Draw!;
        var p = new Vector3((float)draw.Eval("x", bounds, frame), (float)draw.Eval("y", bounds, frame), (float)draw.Eval("z", bounds, frame));
        p = Vector3.Transform(p, ParentTransform(scene, project, obj, frame));
        return (frame, cx + p.X, cy + p.Y);
    }
}
