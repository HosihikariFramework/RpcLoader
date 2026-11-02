using System.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hosihikari.PluginLoader.Rpc;

/// <summary>The loader's operator-facing resource set.</summary>
internal sealed class LoaderStrings;

/// <summary>
/// Localized operator text. The lookup reads the current UI culture on every call, so the
/// server's locale decides the language and English is the neutral fallback.
/// </summary>
internal static class LoaderText
{
    private const string ResourceBaseName = "Hosihikari.PluginLoader.Rpc.Resources.LoaderStrings";

    private static readonly IStringLocalizer Localizer =
        new ResourceManagerStringLocalizer(
            new ResourceManager(ResourceBaseName, typeof(LoaderStrings).Assembly),
            typeof(LoaderStrings).Assembly,
            ResourceBaseName,
            new ResourceNamesCache(),
            NullLogger.Instance);

    internal static string Get(string key, params object[] arguments) => Localizer[key, arguments];
}
