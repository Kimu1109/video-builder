using Avalonia.Controls;
using Avalonia.Platform;

namespace VideoBuilder.App.Shell;

/// <summary>ウィンドウのタイトルバーやタスクバーに出すアプリのアイコン (packaging/video-builder.svg から作った ICO)。</summary>
public static class AppIcon
{
    private static WindowIcon? _icon;

    public static WindowIcon? Get()
    {
        if (_icon != null) return _icon;
        try
        {
            using var stream = AssetLoader.Open(new System.Uri("avares://video-builder/Assets/video-builder.ico"));
            return _icon = new WindowIcon(stream);
        }
        catch
        {
            return null; // 読み込めなくても起動は続ける
        }
    }
}
