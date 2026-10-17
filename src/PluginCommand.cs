using System.Text.Json;

namespace Hosihikari.PluginProvider.Rpc;

/// <summary>
/// 进程外插件的启动命令：清单 <c>extraInfo.command</c>。
///
/// 父级（LeviLamina）的清单解析要求 <c>extraInfo</c> 的值是字符串，所以这里约定：
/// 值是**编码成字符串的 JSON 数组**，例如 <c>"[\"node\",\"./main.mjs\"]"</c>。
/// Provider 解码后按参数表启动，不经 shell（ADR-0031）。
/// 也接受真正的数组，便于不经过父级的验证宿主直接喂清单。
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

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("extraInfo", out var extraInfo)
                || extraInfo.ValueKind != JsonValueKind.Object
                || !extraInfo.TryGetProperty("command", out var command))
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
