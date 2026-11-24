using SkiaSharp;
using VideoBuilder.Core;
using VideoBuilder.Rendering;

namespace VideoBuilder.Tests;

/// <summary>移動方法の動き方 (イージング)・区間ごとの動き方・進む向きに回転・親オブジェクト。</summary>
public class EasingTests
{
    [Fact]
    public void Every_easing_starts_at_0_ends_at_1_and_has_its_shape()
    {
        foreach (EasingKind kind in Enum.GetValues<EasingKind>())
        foreach (EasingDirection dir in Enum.GetValues<EasingDirection>())
        {
            var e = new Easing { Kind = kind, Direction = dir };
            Assert.Equal(0, e.Apply(0), 6);
            Assert.Equal(1, e.Apply(1), 6);
            for (int i = 1; i < 20; i++) Assert.False(double.IsNaN(e.Apply(i / 20.0)), $"{kind} {dir}");
        }
        // 加速はゆっくり始まり、減速は速く始まる
        Assert.True(new Easing { Kind = EasingKind.Cubic, Direction = EasingDirection.In }.Apply(0.5) < 0.2);
        Assert.True(new Easing { Kind = EasingKind.Cubic, Direction = EasingDirection.Out }.Apply(0.5) > 0.8);
        Assert.Equal(0.5, new Easing { Kind = EasingKind.Quint, Direction = EasingDirection.InOut }.Apply(0.5), 6);
        // 行き過ぎて戻る・ゴム・ばねは 1 を超えるところがある。強さを上げると行き過ぎが大きい
        double Max(Easing e) => Enumerable.Range(1, 199).Max(i => e.Apply(i / 200.0));
        Assert.True(Max(new Easing { Kind = EasingKind.Back }) > 1.05);
        Assert.True(Max(new Easing { Kind = EasingKind.Back, Strength = 2 }) > Max(new Easing { Kind = EasingKind.Back }));
        Assert.True(Max(new Easing { Kind = EasingKind.Elastic }) > 1.1);
        Assert.True(Max(new Easing { Kind = EasingKind.Spring }) > 1.05);
        Assert.True(Max(new Easing { Kind = EasingKind.Spring, Damping = 1 }) <= 1.0001);   // 減衰 1 なら揺れない
        Assert.True(Max(new Easing { Kind = EasingKind.Bounce }) <= 1.0001);                // 弾むは行き過ぎない
        // ベジェ: ease-in-out は対称、制御点を枠の外に出すと行き過ぎる
        var inOut = new Easing { Kind = EasingKind.Bezier, X1 = 0.42, Y1 = 0, X2 = 0.58, Y2 = 1 };
        Assert.Equal(0.5, inOut.Apply(0.5), 4);
        Assert.Equal(1 - inOut.Apply(0.2), inOut.Apply(0.8), 4);
        Assert.True(Max(new Easing { Kind = EasingKind.Bezier, X1 = 0.34, Y1 = 1.56, X2 = 0.64, Y2 = 1 }) > 1.05);
    }

    [Fact]
    public void Section_easings_follow_keyframe_edits_and_survive_a_reload()
    {
        var o = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 100);
        var x = o.Draw!.Tracks["x"];
        x.Mode = MovementMode.Linear;
        x.Values[1] = 100;
        x.Ease = new Easing { Kind = EasingKind.Cubic, Direction = EasingDirection.In };
        // 全体の動き方: 加速 (3 次) なので、半分の時間ではまだ約 1/8
        Assert.Equal(100 * Math.Pow(50 / 99.0, 3), x.Evaluate(o.Bounds, 50), 6);   // 区間は 0〜99 フレーム

        o.AddKeyFrame(50);                                            // 区間 0: 0〜50, 区間 1: 50〜99
        x.SetSectionEase(1, new Easing { Kind = EasingKind.Cubic, Direction = EasingDirection.Out });
        double mid1 = x.Evaluate(o.Bounds, 75);                       // 区間 1 は減速 (速く始まる)
        Assert.True(mid1 > x.Values[1] + (x.Values[2] - x.Values[1]) * 0.8, $"区間 1 が減速になっていない: {mid1}");

        o.AddKeyFrame(25);                                            // 区間 0 を分けても、区間ごとの設定は後ろへずれる
        Assert.Null(x.SectionEaseAt(0)); Assert.Null(x.SectionEaseAt(1));
        Assert.Equal(EasingDirection.Out, x.SectionEaseAt(2)!.Direction);

        var p = new Project();
        p.Root.Objects.Add(o);
        var loaded = ProjectSerializer.FromJson(ProjectSerializer.ToJson(p)).Root.Objects[0].Draw!.Tracks["x"];
        Assert.True(AnimatedValue.SameContent(x, loaded));
        Assert.Equal(EasingKind.Cubic, loaded.Ease!.Kind);

        o.RemoveKeyFrameAt(1);                                        // 区間 1 と 2 を 1 つに: 前の区間 (設定なし) が残る
        Assert.Null(x.SectionEaseAt(1));
        var second = o.SplitAt(25)!;                                  // 分割しても、区間の数と設定の数がそろう
        Assert.True((x.SectionEases?.Count ?? 0) <= o.Sections);
        Assert.True((second.Draw!.Tracks["x"].SectionEases?.Count ?? 0) <= second.Sections);
    }

    [Fact]
    public void Orient_turns_along_the_motion_and_children_follow_the_parent()
    {
        var p = new Project();
        p.Settings.Width = 320; p.Settings.Height = 240;
        var parent = ObjectFactory.Create(EffectRegistry.Shape, 0, 0, 60);
        var px = parent.Draw!.Tracks["x"];
        px.Mode = MovementMode.Linear; px.Values[0] = -100; px.Values[1] = 100;
        p.Root.Objects.Add(parent);

        // 下へ動くものは 90 度
        var mover = ObjectFactory.Create(EffectRegistry.Shape, 1, 0, 60);
        var my = mover.Draw!.Tracks["y"];
        my.Mode = MovementMode.Linear; my.Values[1] = 100;
        Assert.Equal(90, ObjectMotion.OrientAngle(mover, mover.Bounds, 30), 1);

        // 子 (レイヤー 2) は親 (表示のレイヤー 1) の位置に付いていく。遅れを付けると少し前の位置
        var child = ObjectFactory.Create(EffectRegistry.Shape, 2, 0, 60);
        child.Draw!.Tracks["parent"] = new AnimatedValue(1);
        child.Draw.Tracks["y"] = new AnimatedValue(50);
        p.Root.Objects.Add(child);
        var at = ObjectMotion.PointAt(p.Root, p, child, child.Bounds, 30, 160, 120);
        Assert.Equal(160 + parent.Draw.Eval("x", parent.Bounds, 30), at.X, 1);
        Assert.Equal(120 + 50, at.Y, 1);
        child.Draw.Tracks["parentDelay"] = new AnimatedValue(0.5);
        var late = ObjectMotion.PointAt(p.Root, p, child, child.Bounds, 30, 160, 120);
        Assert.True(late.X < at.X - 10, "遅れて追いかけていない");

        // 描画でも子が親と一緒に動く (赤い子が、親の右端あたりにいる)
        child.Draw.Tracks["parentDelay"] = new AnimatedValue(0);
        child.Main.Values["color"] = "#FF0000";
        child.Main.Tracks["size"] = new AnimatedValue(20);
        parent.Main.Tracks["size"] = new AnimatedValue(20);
        using var engine = new RenderEngine();
        using var img = engine.RenderImage(p, p.Root, 59);
        using var bmp = SKBitmap.FromImage(img);
        var c = bmp.GetPixel(160 + 100, 120 + 50);
        Assert.True(c.Red > 200 && c.Green < 60, "子が親に付いていっていない: " + c);
    }
}
