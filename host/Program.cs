using System.Collections.Concurrent;
using System.Text.Json;
using Hosihikari;
using Hosihikari.PluginManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// 载体中立检查（ADR-0015）：普通 .NET 控制台驱动核心，不碰 BDS、Shim、任何载体。
var repoRoot = RepoRoot();
var python = FindPython();
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
StageQuickPlugin("QuickHello", "hello.js");
if (python is not null)
{
    StagePythonPlugin("PythonHello", "hello.py");
    StagePythonPlugin("PyRefuse", "refuse.py", parented: true);
    StagePythonPlugin("PyCrash", "crash.py", parented: true);
}
StagePlugin("Refuse", "refuse.mjs", parented: true);
StagePlugin("Crash", "crash.mjs", parented: true);
StageQuickPlugin("QuickCrash", "crash.js", parented: true);

// 运营方的覆盖放包外（ADR-0018）。
File.WriteAllText(Path.Combine(configRoot, "Hello.conf"), "greeting = \"overridden\"\n");
File.WriteAllText(Path.Combine(configRoot, "QuickHello.conf"), "greeting = \"quick-overridden\"\n");
if (python is not null)
{
    File.WriteAllText(Path.Combine(configRoot, "PythonHello.conf"), "greeting = \"py-overridden\"\n");
}

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

var quickData = Path.Combine(dataRoot, "QuickHello");
checks.Add(("QuickJS 绑定：hello 快照、配置分层与启用",
    host.LoadedNames.Contains("QuickHello")
        && Read(quickData, "body.txt") == "body:quick-overridden"
        && Read(quickData, "enabled.txt") == "enabled",
    $"body={Read(quickData, "body.txt")} enabled={Read(quickData, "enabled.txt")}"));

checks.Add(("QuickJS 绑定：默认异步事件到达",
    Read(quickData, "ping.txt") == "ping:\"from-host\"", Read(quickData, "ping.txt")));

checks.Add(("QuickJS 绑定：同步订阅等到 ack",
    Read(quickData, "sync.txt") == "sync:\"sync-host\"", Read(quickData, "sync.txt")));

// 让 500/700 ms 的自发布定时器跑完：既验定时器，也验插件→宿主的 publish。
Thread.Sleep(500);
checks.Add(("QuickJS 绑定：定时器与自发布",
    Read(quickData, "self.txt") == "self:\"quick-publish\""
        && Read(quickData, "syncself.txt") == "syncself:\"quick-sync\"",
    $"self={Read(quickData, "self.txt")} syncself={Read(quickData, "syncself.txt")}"));

var quickDisabled = host.Disable("QuickHello");
var quickDisabledText = Read(quickData, "disabled.txt");
var quickEnabled = host.Enable("QuickHello");
var quickEnabledText = Read(quickData, "enabled.txt");
var quickUnloaded = host.Unload("QuickHello");
checks.Add(("QuickJS 绑定：启停与卸载",
    quickDisabled && quickDisabledText == "disabled" && quickEnabled && quickEnabledText == "enabled"
        && quickUnloaded.Released && Read(quickData, "unloaded.txt") == "unloaded",
    quickUnloaded.Error ?? Read(quickData, "unloaded.txt")));

if (python is null)
{
    checks.Add(("Python 绑定", true, "（跳过：没有 Python）"));
}
else
{
    var pyData = Path.Combine(dataRoot, "PythonHello");
    checks.Add(("Python 绑定：hello 快照、配置分层与启用",
        host.LoadedNames.Contains("PythonHello")
            && Read(pyData, "body.txt") == "body:py-overridden"
            && Read(pyData, "enabled.txt") == "enabled",
        $"body={Read(pyData, "body.txt")} enabled={Read(pyData, "enabled.txt")}"));

    host.Events.Publish(new ChannelMessage("ping", "\"from-host\""));
    Thread.Sleep(300);
    checks.Add(("Python 绑定：默认异步事件到达",
        Read(pyData, "ping.txt") == "ping:\"from-host\"", Read(pyData, "ping.txt")));

    host.Events.Publish(new ChannelMessage("sync", "\"sync-host\""));
    checks.Add(("Python 绑定：同步订阅等到 ack",
        Read(pyData, "sync.txt") == "sync:\"sync-host\"", Read(pyData, "sync.txt")));

    Thread.Sleep(800);
    checks.Add(("Python 绑定：定时器与自发布",
        Read(pyData, "self.txt") == "self:\"py-publish\""
            && Read(pyData, "syncself.txt") == "syncself:\"py-sync\"",
        $"self={Read(pyData, "self.txt")} syncself={Read(pyData, "syncself.txt")}"));

    var pyDisabled = host.Disable("PythonHello");
    var pyDisabledText = Read(pyData, "disabled.txt");
    var pyEnabled = host.Enable("PythonHello");
    var pyEnabledText = Read(pyData, "enabled.txt");
    var pyUnloaded = host.Unload("PythonHello");
    checks.Add(("Python 绑定：启停与卸载",
        pyDisabled && pyDisabledText == "disabled" && pyEnabled && pyEnabledText == "enabled"
            && pyUnloaded.Released && Read(pyData, "unloaded.txt") == "unloaded",
        pyUnloaded.Error ?? Read(pyData, "unloaded.txt")));

    var pyRefuseLoaded = host.Load(Path.Combine(stage, "parented", "PyRefuse"));
    var pyRefuseEnable = host.Enable("PyRefuse");
    var pyRefuseStill = host.LoadedNames.Contains("PyRefuse");
    var pyRefuseUnload = host.Unload("PyRefuse");
    checks.Add(("Python 绑定：拒绝启用与拒绝停用",
        pyRefuseLoaded && !pyRefuseEnable && pyRefuseStill && !pyRefuseUnload.Released
            && pyRefuseUnload.Error?.Contains("拒绝停用") == true,
        pyRefuseUnload.Error ?? "（没有拒绝）"));

    File.WriteAllText(Path.Combine(dataRoot, "PyRefuse", "allow.txt"), "allow");
    var pyRefuseAfter = host.Unload("PyRefuse");
    checks.Add(("Python 绑定：允许之后能卸掉",
        pyRefuseAfter.Released && !host.LoadedNames.Contains("PyRefuse"),
        pyRefuseAfter.Error ?? "已回收"));

    var pyCrashLoaded = host.Load(Path.Combine(stage, "parented", "PyCrash"));
    Thread.Sleep(400);
    checks.Add(("Python 绑定：进程死亡走故障卸载并通知",
        pyCrashLoaded && !host.LoadedNames.Contains("PyCrash")
            && removed.Any(name => name == "PyCrash"),
        $"removed=[{string.Join(", ", removed)}]"));
}

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

var quickCrashLoaded = host.Load(Path.Combine(stage, "parented", "QuickCrash"));
var quickCrashEnable = host.Enable("QuickCrash");
Thread.Sleep(300);
checks.Add(("QuickJS 绑定：进程死亡走故障卸载并通知",
    quickCrashLoaded && !quickCrashEnable && !host.LoadedNames.Contains("QuickCrash")
        && removed.Any(name => name == "QuickCrash"),
    $"loaded={quickCrashLoaded} enable={quickCrashEnable} removed=[{string.Join(", ", removed)}]"));

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
            "command": "[\"node\",\"./hosihikari.mjs\",\"./{{entry}}\"]"
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

void StageQuickPlugin(string name, string entry, bool parented = false)
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
            "command": "[\"qjs\",\"./hosihikari.js\",\"./{{entry}}\"]"
          }
        }
        """);

    File.Copy(
        Path.Combine(repoRoot, "bindings", "quickjs", "hosihikari.js"),
        Path.Combine(package, "hosihikari.js"));
    File.Copy(
        Path.Combine(repoRoot, "bindings", "quickjs", "stdin_worker.js"),
        Path.Combine(package, "stdin_worker.js"));
    File.Copy(
        Path.Combine(repoRoot, "bindings", "quickjs", entry),
        Path.Combine(package, entry));
    File.WriteAllText(Path.Combine(package, $"{name}.conf"), "greeting = \"quick-package\"\n");
}

void StagePythonPlugin(string name, string entry, bool parented = false)
{
    var root = parented ? Path.Combine(stage, "parented") : pluginsRoot;
    var package = Path.Combine(root, name);
    Directory.CreateDirectory(package);

    var command = python!.Concat(new[] { "./hosihikari.py", "./" + entry }).ToArray();
    var manifest = JsonSerializer.Serialize(new
    {
        name,
        entry,
        type = "hosihikari",
        version = "1.0.0",
        dependencies = new[] { new { name = "hosihikari" }, new { name = "hosihikari-rpc" } },
        extraInfo = new Dictionary<string, string>
        {
            ["api"] = "2",
            ["format_version"] = "1",
            ["provider"] = "rpc",
            ["command"] = JsonSerializer.Serialize(command),
        },
    });
    File.WriteAllText(Path.Combine(package, "manifest.json"), manifest);

    File.Copy(
        Path.Combine(repoRoot, "bindings", "python", "hosihikari.py"),
        Path.Combine(package, "hosihikari.py"));
    File.Copy(
        Path.Combine(repoRoot, "bindings", "python", entry),
        Path.Combine(package, entry));
}

static string[]? FindPython()
{
    string[][] candidates = OperatingSystem.IsWindows()
        ? [["python"], ["python3"], ["py", "-3"]]
        : [["python3"], ["python"]];
    foreach (var candidate in candidates)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(candidate[0])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in candidate.Skip(1))
            {
                start.ArgumentList.Add(argument);
            }

            start.ArgumentList.Add("--version");
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null)
            {
                continue;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                return candidate;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    return null;
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
