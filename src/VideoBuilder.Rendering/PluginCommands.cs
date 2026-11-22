using VideoBuilder.Plugins;

namespace VideoBuilder.Rendering;

/// <summary>プラグインが登録したコマンド (「プラグイン」メニューの項目)。</summary>
public sealed record PluginCommandEntry(string PluginId, IPluginCommand Command);

/// <summary>プラグインのコマンドの一覧。画面側はここからメニューを作る。</summary>
public static class PluginCommands
{
    private static readonly List<PluginCommandEntry> List = new();

    /// <summary>登録した順。</summary>
    public static IReadOnlyList<PluginCommandEntry> All { get { lock (List) return List.ToList(); } }

    /// <summary>コマンドを登録する。ID が空か、すでに同じ ID があれば ArgumentException。</summary>
    public static void Register(string pluginId, IPluginCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Id)) throw new ArgumentException("コマンドの Id が空です");
        if (string.IsNullOrWhiteSpace(command.Name)) throw new ArgumentException("コマンドの Name が空です");
        lock (List)
        {
            if (List.Any(e => e.Command.Id == command.Id)) throw new ArgumentException($"同じ ID のコマンドがすでにあります: {command.Id}");
            List.Add(new PluginCommandEntry(pluginId, command));
        }
    }

    /// <summary>登録を外す (テスト用)。</summary>
    public static bool Unregister(string commandId)
    {
        lock (List) return List.RemoveAll(e => e.Command.Id == commandId) > 0;
    }
}
