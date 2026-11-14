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
            context.Logger.LogError(
                "{Message}",
                LoaderText.Get("CommandMissing", manifest.Name));
            return null;
        }

        try
        {
            var connection = RpcConnection.Start(command, packageDirectory, context);
            return new RpcPluginHandle(new RpcPlugin(connection), connection, context);
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                ex,
                "{Message}",
                LoaderText.Get("ProcessStartFailed", manifest.Name, string.Join(' ', command)));
            return null;
        }
    }
}

internal sealed class RpcPlugin(RpcConnection connection) : IPlugin
{
    public ValueTask OnLoad(IPluginContext context, CancellationToken cancellationToken)
    {
        connection.Hello(context);
        return ValueTask.CompletedTask;
    }

    public ValueTask<PluginHookResult> OnEnable(CancellationToken cancellationToken) =>
        ValueTask.FromResult(connection.Hook("hosihikari/enable", cancellationToken));

    public ValueTask<PluginHookResult> OnDisable(CancellationToken cancellationToken) =>
        ValueTask.FromResult(connection.Hook("hosihikari/disable", cancellationToken));

    public ValueTask OnUnload(CancellationToken cancellationToken)
    {
        connection.Unload(cancellationToken);
        return ValueTask.CompletedTask;
    }
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
            context.Logger.LogWarning(
                ex,
                "{Message}",
                LoaderText.Get("ProcessStopTimeout", context.Name));
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
