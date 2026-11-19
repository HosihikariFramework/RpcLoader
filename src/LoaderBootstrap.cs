using Hosihikari;

[assembly: EntryPoint<Hosihikari.PluginLoader.Rpc.LoaderBootstrap>]

namespace Hosihikari.PluginLoader.Rpc;

/// <summary>
/// 加载器本身就是一个插件包（FX-0002）：装载时把自己登记给框架，此后
/// <c>loader: "rpc"</c> 的包才会被它接管。登记之后不能撤销。
/// </summary>
public sealed class LoaderBootstrap : IPlugin
{
    public ValueTask OnLoad(IPluginContext context, CancellationToken cancellationToken)
    {
        // 加载器包按清单的 kind 拿到 ILoaderContext（FX-0059）；拿不到说明清单漏写了 kind。
        if (context is not ILoaderContext loaderContext)
        {
            throw new InvalidOperationException(
                "The context is not an ILoaderContext; the package manifest must declare "
                + "\"kind\": \"loader\"");
        }

        // 重复登记说明另一个包已经接管 rpc；本包留在场上只会空转。
        if (!loaderContext.RegisterLoader(new RpcPluginLoader()))
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
