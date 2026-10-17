using Hosihikari;

[assembly: EntryPoint<Hosihikari.PluginProvider.Rpc.ProviderBootstrap>]

namespace Hosihikari.PluginProvider.Rpc;

/// <summary>
/// Provider 本身就是一个插件包（ADR-0002）：装载时把自己登记给框架，此后
/// <c>provider: "rpc"</c> 的包才会被它接管。登记之后不能撤销。
/// </summary>
public sealed class ProviderBootstrap : IPlugin
{
    public void OnLoad(IPluginContext context) => context.RegisterProvider(new RpcPluginProvider());

    public PluginHookResult OnEnable() => PluginHookResult.Allow();

    public PluginHookResult OnDisable() => PluginHookResult.Allow();

    public void OnUnload()
    {
    }
}
