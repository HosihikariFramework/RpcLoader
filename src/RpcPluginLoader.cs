using Hosihikari;
using Microsoft.Extensions.Logging;

namespace Hosihikari.PluginLoader.Rpc;

/// <summary>
/// 进程外插件的加载器（ADR-0031）。它不认识语言，只认命令与行分隔的 JSON-RPC 2.0。
/// 一个插件一个进程；卸载就是让进程真正退出（ADR-0004 / 0030）。
/// </summary>
public sealed class RpcPluginLoader : IPluginLoader
{
    public string Id => "rpc";

    public bool CanLoad(PluginManifest manifest) => manifest.Loader == Id;

    public PluginHandle? Load(PluginManifest manifest, string packageDirectory, IPluginContext context)
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
            return new RpcPluginHandle(new RpcPlugin(connection), connection, context);
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

internal sealed class RpcPluginHandle(
    RpcPlugin plugin,
    RpcConnection connection,
    IPluginContext context) : PluginHandle(plugin)
{
    private bool _unloadStarted;
    private RpcConnection? _connection = connection;

    public override bool IsUnloaded => _unloadStarted && _connection?.HasExited == true;

    /// <summary>搬运单元就是这个进程：关 stdin、等退出、必要时杀进程树；验证看它是否退出。</summary>
    public override void Unload()
    {
        if (_unloadStarted)
        {
            return;
        }

        _unloadStarted = true;
        try
        {
            _connection?.Dispose();
        }
        catch (Exception ex)
        {
            // 进程没在限期内收尾：交给验证如实报失败，卸载停在已放下（ADR-0025）。
            context.Logger.LogWarning(ex, "{Name}: 关闭插件进程没有在限期内完成", context.Name);
        }

        Plugin = null;
    }

    public override async Task<bool> WaitForUnloadAsync(CancellationToken cancellationToken = default)
    {
        if (!_unloadStarted || _connection is null)
        {
            return false;
        }

        for (var i = 0; i < 100 && !_connection.HasExited; i++)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        return _connection.HasExited;
    }
}
