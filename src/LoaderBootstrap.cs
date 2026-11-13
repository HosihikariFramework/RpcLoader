using Hosihikari;

[assembly: EntryPoint<Hosihikari.PluginLoader.Rpc.LoaderBootstrap>]

namespace Hosihikari.PluginLoader.Rpc;

/// <summary>
/// 加载器本身就是一个插件包（ADR-0002）：装载时把自己登记给框架，此后
/// <c>loader: "rpc"</c> 的包才会被它接管。登记之后不能撤销。
/// </summary>
public sealed class LoaderBootstrap : IPlugin
{
    public ValueTask OnLoad(IPluginContext context, CancellationToken cancellationToken)
    {
        // 重复登记说明另一个包已经接管 rpc；本包留在场上只会空转。
        // 加载器包拿到的是 ILoaderContext（ADR-0059），插件面之外才看得见 RegisterLoader。
        if (!((ILoaderContext)context).RegisterLoader(new RpcPluginLoader()))
        {
            throw new InvalidOperationException("The 'rpc' loader kind is already registered");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<PluginHookResult> OnEnable(CancellationToken cancellationToken) =>
        ValueTask.FromResult(PluginHookResult.Allow());

    public ValueTask<PluginHookResult> OnDisable(CancellationToken cancellationToken) =>
        ValueTask.FromResult(PluginHookResult.Allow());

    public ValueTask OnUnload(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
