using System.Collections.Concurrent;
using Hosihikari;
using Hosihikari.PluginManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// 载体中立检查（ADR-0015）：普通 .NET 控制台驱动核心，不碰 BDS、Shim、任何载体。
var repoRoot = RepoRoot();
var stage = Path.Combine(Path.GetTempPath(), "hosihikari-rpc-spike");
if (Directory.Exists(stage))
{
    Directory.Delete(stage, true);
}

var pluginsRoot = Path.Combine(stage, "plugins");
var dataRoot = Path.Combine(stage, "data");
var configRoot = Path.Combine(stage, "config", "hosihikari");
Directory.CreateDirectory(configRoot);

StageProviderPackage();
StagePlugin("Hello", "hello.mjs");
StagePlugin("Refuse", "refuse.mjs", parented: true);
StagePlugin("Crash", "crash.mjs", parented: true);

// 运营方的覆盖放包外（ADR-0018）。
File.WriteAllText(Path.Combine(configRoot, "Hello.conf"), "greeting = \"overridden\"\n");

var collection = new ServiceCollection();
collection.AddLogging(builder => builder
    .AddSimpleConsole(options => options.SingleLine = true)
    .SetMinimumLevel(LogLevel.Information));
var services = collection.BuildServiceProvider();

var reported = new List<string>();
var removed = new ConcurrentBag<string>();
var host = new PluginManagerHost(
    pluginsRoot,
    dataRoot,
    configRoot,
    entry =>
    {
        reported.Add(entry);
        Console.WriteLine($"[框架] {entry}");
    },
    services,
    name =>
    {
        removed.Add(name);
        Console.WriteLine($"[框架] 已移除 {name}");
    });
host.AddProvider(new AssemblyPluginProvider());

Console.WriteLine("=== 加载 ===");
host.LoadAll();
Console.WriteLine($"已加载：{string.Join(", ", host.LoadedNames)}");

var checks = new List<(string Name, bool Passed, string Detail)>();
var helloData = Path.Combine(dataRoot, "Hello");

checks.Add((
    "RPC Provider 由插件包登记，rpc 包被它接管",
    host.LoadedNames.Contains("hosihikari-rpc") && host.LoadedNames.Contains("Hello"),
    string.Join(", ", host.LoadedNames)));

checks.Add(("hello 快照到达，onLoad 执行并读到包外覆盖",
    Read(helloData, "body.txt") == "body:overridden", Read(helloData, "body.txt")));

checks.Add(("启用钩子到达进程",
    Read(helloData, "enabled.txt") == "enabled", Read(helloData, "enabled.txt")));

// 默认异步：发布方不等。
host.Events.Publish(new ChannelMessage("ping", "\"from-host\""));
Thread.Sleep(300);
checks.Add(("默认异步事件到达进程",
    Read(helloData, "ping.txt") == "ping:\"from-host\"", Read(helloData, "ping.txt")));

// 按订阅声明同步：发布方等 ack，返回时文件已经写好。
host.Events.Publish(new ChannelMessage("sync", "\"sync-host\""));
checks.Add(("同步订阅等到 ack",
    Read(helloData, "sync.txt") == "sync:\"sync-host\"", Read(helloData, "sync.txt")));

var disabled = host.Disable("Hello");
var disabledText = Read(helloData, "disabled.txt");
var enabled = host.Enable("Hello");
var enabledText = Read(helloData, "enabled.txt");
checks.Add(("停用与再次启用都到达进程",
    disabled && disabledText == "disabled" && enabled && enabledText == "enabled",
    $"disabled={disabledText} enabled={enabledText}"));

var unloaded = host.Unload("Hello");
checks.Add(("卸载请求到达，进程退出并回收",
    unloaded.Released && Read(helloData, "unloaded.txt") == "unloaded" && !host.LoadedNames.Contains("Hello"),
    unloaded.Error ?? Read(helloData, "unloaded.txt")));

// 拒绝是结果值，不是异常（ADR-0030）。
var refuseLoaded = host.Load(Path.Combine(stage, "parented", "Refuse"));
var refuseEnable = host.Enable("Refuse");
var refuseStillLoaded = host.LoadedNames.Contains("Refuse");
var refuseUnload = host.Unload("Refuse");
checks.Add(("拒绝启用留在册，拒绝停用中止卸载",
    refuseLoaded && !refuseEnable && refuseStillLoaded && !refuseUnload.Released
        && refuseUnload.Error?.Contains("拒绝停用") == true,
    refuseUnload.Error ?? "（没有拒绝）"));

File.WriteAllText(Path.Combine(dataRoot, "Refuse", "allow.txt"), "allow");
var refuseUnloadAfter = host.Unload("Refuse");
checks.Add(("允许之后能真正卸掉",
    refuseUnloadAfter.Released && !host.LoadedNames.Contains("Refuse"),
    refuseUnloadAfter.Error ?? "已回收"));

// 进程在钩子之外死亡：Provider 报告故障，宿主卸载并通知载体（ADR-0030）。
var crashLoaded = host.Load(Path.Combine(stage, "parented", "Crash"));
Thread.Sleep(400);
checks.Add(("进程死亡走故障卸载并通知",
    crashLoaded && !host.LoadedNames.Contains("Crash") && removed.Any(name => name == "Crash"),
    $"removed=[{string.Join(", ", removed)}]"));

Console.WriteLine();
Console.WriteLine("=== 断言 ===");
foreach (var (name, passed, detail) in checks)
{
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")}  {name}  （{detail}）");
}

return checks.All(check => check.Passed) ? 0 : 1;

string Read(string directory, string file)
{
    var path = Path.Combine(directory, file);
    return File.Exists(path) ? File.ReadAllText(path) : "（没写出来）";
}

void StageProviderPackage()
{
    var package = Path.Combine(pluginsRoot, "hosihikari-rpc");
    Directory.CreateDirectory(package);

    File.WriteAllText(Path.Combine(package, "manifest.json"), """
        {
          "name": "hosihikari-rpc",
          "entry": "Hosihikari.PluginProvider.Rpc.dll",
          "type": "hosihikari",
          "version": "0.1.0",
          "dependencies": [ { "name": "hosihikari" } ],
          "extraInfo": { "api": "2", "format_version": "1", "provider": "assembly" }
        }
        """);

    var output = Path.Combine(repoRoot, "src", "bin", ConfigurationName(), "net10.0");
    foreach (var file in Directory.EnumerateFiles(output, "*.dll"))
    {
        if (Path.GetFileName(file) is "Hosihikari.Abstractions.dll")
        {
            continue;
        }

        File.Copy(file, Path.Combine(package, Path.GetFileName(file)));
    }

    var deps = Path.Combine(output, "Hosihikari.PluginProvider.Rpc.deps.json");
    if (File.Exists(deps))
    {
        File.Copy(deps, Path.Combine(package, Path.GetFileName(deps)));
    }
}

void StagePlugin(string name, string entry, bool parented = false)
{
    var root = parented ? Path.Combine(stage, "parented") : pluginsRoot;
    var package = Path.Combine(root, name);
    Directory.CreateDirectory(package);

    File.WriteAllText(Path.Combine(package, "manifest.json"), $$"""
        {
          "name": "{{name}}",
          "entry": "{{entry}}",
          "type": "hosihikari",
          "version": "1.0.0",
          "dependencies": [
            { "name": "hosihikari" },
            { "name": "hosihikari-rpc" }
          ],
          "extraInfo": {
            "api": "2",
            "format_version": "1",
            "provider": "rpc",
            "command": ["node", "./hosihikari.mjs", "./{{entry}}"]
          }
        }
        """);

    File.Copy(
        Path.Combine(repoRoot, "bindings", "node", "hosihikari.mjs"),
        Path.Combine(package, "hosihikari.mjs"));
    File.Copy(
        Path.Combine(repoRoot, "bindings", "node", entry),
        Path.Combine(package, entry));
}

static string ConfigurationName()
{
#if DEBUG
    return "Debug";
#else
    return "Release";
#endif
}

static string RepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null &&
           !File.Exists(Path.Combine(directory.FullName, "src", "Hosihikari.PluginProvider.Rpc.csproj")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? throw new InvalidOperationException("找不到仓库根");
}
