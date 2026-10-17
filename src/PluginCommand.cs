using System.Text.Json;

namespace Hosihikari.PluginProvider.Rpc;

/// <summary>
/// 进程外插件的启动命令：清单 <c>extraInfo.command</c>，一个非空的字符串数组。
/// 命令不经 shell，第一个元素是可执行文件，其余逐个进参数表（ADR-0031）。
/// </summary>
internal static class PluginCommand
{
    public static IReadOnlyList<string>? Read(string packageDirectory)
    {
        var manifestPath = Path.Combine(packageDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!document.RootElement.TryGetProperty("extraInfo", out var extraInfo)
            || extraInfo.ValueKind != JsonValueKind.Object
            || !extraInfo.TryGetProperty("command", out var command)
            || command.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var item in command.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                return null;
            }

            parts.Add(item.GetString()!);
        }

        return parts.Count == 0 ? null : parts;
    }
}
