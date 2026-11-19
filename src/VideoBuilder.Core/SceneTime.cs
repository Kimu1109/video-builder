namespace VideoBuilder.Core;

/// <summary>シーンオブジェクトの中の位置を、参照先のシーンのフレームに直す。</summary>
public static class SceneTime
{
    /// <summary>
    /// オブジェクトの中の位置 local (0 始まり) を、シーンの中の位置にする (「開始位置」は含まない)。
    /// 「長さに合わせて伸縮」がオンなら、先頭と末尾の固定部分はそのままにして、中間を伸び縮みさせる。
    /// </summary>
    public static int Map(int local, int objectLength, int sceneLength, bool stretch, int intro, int outro)
    {
        if (!stretch || objectLength <= 0 || sceneLength <= 0 || objectLength == sceneLength) return local;
        intro = Math.Max(0, intro); outro = Math.Max(0, outro);
        int limit = Math.Min(objectLength, sceneLength);
        if (intro + outro > limit)
        {
            // 両方を同じ割合で縮める
            double k = (double)limit / (intro + outro);
            intro = (int)Math.Floor(intro * k); outro = Math.Min(limit - intro, (int)Math.Floor(outro * k));
        }
        if (local < intro) return local;
        if (local >= objectLength - outro) return sceneLength - (objectLength - local);
        int middleObj = objectLength - intro - outro, middleScene = sceneLength - intro - outro;
        if (middleObj <= 0) return intro;
        return intro + (int)Math.Floor((double)(local - intro) * middleScene / middleObj);
    }

    /// <summary>シーンオブジェクト obj の frame の位置が、参照先 scene の何フレーム目にあたるか。</summary>
    public static int InnerFrame(Project p, TimelineObject obj, Scene scene, int[] bounds, int frame)
    {
        var main = obj.Main;
        int local = frame - obj.Start + obj.SourceOffsetFrames;
        if (main.GetBool("stretch"))
        {
            int intro = p.SecondsToFrames(main.Eval("intro", bounds, obj.Start));
            int outro = p.SecondsToFrames(main.Eval("outro", bounds, obj.Start));
            local = Map(frame - obj.Start, obj.Length, scene.Length, true, intro, outro);
        }
        return local + p.SecondsToFrames(main.Eval("position", bounds, frame));
    }
}
