using Hosihikari;
using Microsoft.Extensions.Logging;

namespace Hosihikari.PluginProvider.Rpc;

/// <summary>
/// 进程外插件的 Provider（ADR-0031）。它不认识语言，只认命令与行分隔的 JSON-RPC 2.0。
/// 一个插件一个进程；卸载就是让进程真正退出（ADR-0004 / 0030）。
/// </summary>
public sealed class RpcPluginProvider : IPluginProvider
{
    public string Type => "rpc";

    public bool CanLoad(PluginManifest manifest) => manifest.Provider == Type;

    public PluginInstance? Load(PluginManifest manifest, string packageDirectory, IPluginContext context)
    {
        var command = PluginCommand.Read(packageDirectory);
        if (command is null)
        {
            context.Logger.LogError("{Name}: extraInfo.command 缺失或不是非空字符串数组", manifest.Name);
            return null;
        }

        try
        {
            var connection = RpcConnection.Start(command, packageDirectory, context);
            return new RpcPluginInstance(new RpcPlugin(connection), connection);
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "{Name}: 启动进程失败（{Command}）", manifest.Name, string.Join(' ', command));
            return null;
        }
    }
}

internal sealed class RpcPlugin(RpcConnection connection) : IPlugin
{
    public void OnLoad(IPluginContext context) => connection.Hello(context);

    public PluginHookResult OnEnable() => connection.Hook("hosihikari/enable");

    public PluginHookResult OnDisable() => connection.Hook("hosihikari/disable");

    public void OnUnload() => connection.Unload();
}

internal sealed class RpcPluginInstance(RpcPlugin plugin, RpcConnection connection) : PluginInstance(plugin)
{
    /// <summary>搬运单元就是这个进程：关 stdin、等退出、必要时杀进程树；验证器看它是否退出。</summary>
    public override Func<bool> Release()
    {
        connection.Dispose();
        return () => connection.HasExited;
    }
}
