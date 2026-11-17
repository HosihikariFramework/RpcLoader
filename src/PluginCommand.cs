using System.Text.Json;

namespace Hosihikari.PluginLoader.Rpc;

/// <summary>
/// 进程外插件的启动命令：清单 <c>pack.command</c>（ADR-0058 / 0031）。
///
/// 值是参数数组；1.x 把它编码成字符串，两种都收。加载器解码后按参数表启动，不经 shell。
/// </summary>
internal static class PluginCommand
{
    public static IReadOnlyList<string>? Read(string packageDirectory)
    {
        var manifestPath = Path.Combine(packageDirectory, "hosihikari.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("pack", out var pack)
                || pack.ValueKind != JsonValueKind.Object
                || !pack.TryGetProperty("command", out var command))
            {
                return null;
            }

            if (command.ValueKind == JsonValueKind.String)
            {
                var text = command.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                using var decoded = JsonDocument.Parse(text);
                return ReadArray(decoded.RootElement);
            }

            return ReadArray(command);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? ReadArray(JsonElement command)
    {
        if (command.ValueKind != JsonValueKind.Array)
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
