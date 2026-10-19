using System.Collections.Concurrent;
using System.Text.Json;
using Hosihikari;
using Hosihikari.PluginManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// 载体中立检查（ADR-0015）：普通 .NET 控制台驱动核心，不碰 BDS、Shim、任何载体。
var repoRoot = RepoRoot();
var python = FindPython();
var java = FindTool("java", "-version");
var javac = FindTool("javac", "-version");
var kotlinc = FindTool("kotlinc", "-version");
var requireJava = Environment.GetEnvironmentVariable("HOSIHIKARI_REQUIRE_JAVA") == "1";
var requireKotlin = Environment.GetEnvironmentVariable("HOSIHIKARI_REQUIRE_KOTLIN") == "1";
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
var javaTools = java is not null && javac is not null;
var javaStaged = javaTools
    && StageJavaPlugin("JavaHello", "hello.HelloPlugin", "hello/HelloPlugin.java");
if (javaStaged)
{
    StageJavaPlugin("JavaRefuse", "refuse.RefusePlugin", "refuse/RefusePlugin.java", parented: true);
    StageJavaPlugin("JavaCrash", "crash.CrashPlugin", "crash/CrashPlugin.java", parented: true);
}
var kotlinTools = javaTools && kotlinc is not null;
var kotlinStaged = kotlinTools
    && StageKotlinPlugin("KotlinHello", "hello.HelloPlugin", "hello/HelloPlugin.kt");
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
if (javaStaged)
{
    File.WriteAllText(Path.Combine(configRoot, "JavaHello.conf"), "greeting = \"java-overridden\"\n");
}
if (kotlinStaged)
{
    File.WriteAllText(Path.Combine(configRoot, "KotlinHello.conf"), "greeting = \"kotlin-overridden\"\n");
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

if (!javaTools)
{
    checks.Add(("Java 绑定", !requireJava, requireJava ? "（需要 JDK，但未找到）" : "（跳过：没有 JDK）"));
}
else if (!javaStaged)
{
    checks.Add(("Java 绑定", false, "（JDK 在场，但编译/暂存失败；见上方 [Java] 日志）"));
}
else
{
    var javaData = Path.Combine(dataRoot, "JavaHello");
    checks.Add(("Java 绑定：hello 快照、配置分层与启用",
        host.LoadedNames.Contains("JavaHello")
            && Read(javaData, "body.txt") == "body:java-overridden"
            && Read(javaData, "enabled.txt") == "enabled",
        $"body={Read(javaData, "body.txt")} enabled={Read(javaData, "enabled.txt")}"));

    host.Events.Publish(new ChannelMessage("ping", "\"from-host\""));
    Thread.Sleep(300);
    checks.Add(("Java 绑定：默认异步事件到达",
        Read(javaData, "ping.txt") == "ping:\"from-host\"", Read(javaData, "ping.txt")));

    host.Events.Publish(new ChannelMessage("sync", "\"sync-host\""));
    checks.Add(("Java 绑定：同步订阅等到 ack",
        Read(javaData, "sync.txt") == "sync:\"sync-host\"", Read(javaData, "sync.txt")));

    Thread.Sleep(800);
    checks.Add(("Java 绑定：定时器与自发布",
        Read(javaData, "self.txt") == "self:\"java-publish\""
            && Read(javaData, "syncself.txt") == "syncself:\"java-sync\"",
        $"self={Read(javaData, "self.txt")} syncself={Read(javaData, "syncself.txt")}"));

    var everyText = Read(javaData, "every.txt");
    for (var i = 0; i < 30 && (!int.TryParse(everyText, out var ticks) || ticks < 2); i++)
    {
        Thread.Sleep(100);
        everyText = Read(javaData, "every.txt");
    }

    checks.Add(("Java 绑定：runEvery 周期回调",
        int.TryParse(everyText, out var javaTicks) && javaTicks >= 2, everyText));

    var javaDisabled = host.Disable("JavaHello");
    var javaDisabledText = Read(javaData, "disabled.txt");
    var javaEnabled = host.Enable("JavaHello");
    var javaEnabledText = Read(javaData, "enabled.txt");
    var javaUnloaded = host.Unload("JavaHello");
    checks.Add(("Java 绑定：启停与卸载",
        javaDisabled && javaDisabledText == "disabled" && javaEnabled && javaEnabledText == "enabled"
            && javaUnloaded.Released && Read(javaData, "unloaded.txt") == "unloaded",
        javaUnloaded.Error ?? Read(javaData, "unloaded.txt")));

    var javaRefuseLoaded = host.Load(Path.Combine(stage, "parented", "JavaRefuse"));
    var javaRefuseEnable = host.Enable("JavaRefuse");
    var javaRefuseStill = host.LoadedNames.Contains("JavaRefuse");
    var javaRefuseUnload = host.Unload("JavaRefuse");
    checks.Add(("Java 绑定：拒绝启用与拒绝停用",
        javaRefuseLoaded && !javaRefuseEnable && javaRefuseStill && !javaRefuseUnload.Released
            && javaRefuseUnload.Error?.Contains("拒绝停用") == true,
        javaRefuseUnload.Error ?? "（没有拒绝）"));

    File.WriteAllText(Path.Combine(dataRoot, "JavaRefuse", "allow.txt"), "allow");
    var javaRefuseAfter = host.Unload("JavaRefuse");
    checks.Add(("Java 绑定：允许之后能卸掉",
        javaRefuseAfter.Released && !host.LoadedNames.Contains("JavaRefuse"),
        javaRefuseAfter.Error ?? "已回收"));

    var javaCrashLoaded = host.Load(Path.Combine(stage, "parented", "JavaCrash"));
    Thread.Sleep(600);
    checks.Add(("Java 绑定：进程死亡走故障卸载并通知",
        javaCrashLoaded && !host.LoadedNames.Contains("JavaCrash")
            && removed.Any(name => name == "JavaCrash"),
        $"removed=[{string.Join(", ", removed)}]"));
}

if (!kotlinTools)
{
    checks.Add(("Kotlin 绑定", !requireKotlin, requireKotlin ? "（需要 Kotlin 编译器，但未找到）" : "（跳过：没有 Kotlin 编译器）"));
}
else if (!kotlinStaged)
{
    checks.Add(("Kotlin 绑定", false, "（Kotlin 编译器在场，但编译/暂存失败；见上方 [Kotlin] 日志）"));
}
else
{
    var kotlinData = Path.Combine(dataRoot, "KotlinHello");
    checks.Add(("Kotlin 绑定：hello 快照、配置分层与启用",
        host.LoadedNames.Contains("KotlinHello")
            && Read(kotlinData, "body.txt") == "body:kotlin-overridden"
            && Read(kotlinData, "enabled.txt") == "enabled",
        $"body={Read(kotlinData, "body.txt")} enabled={Read(kotlinData, "enabled.txt")}"));

    host.Events.Publish(new ChannelMessage("ping", "\"from-host\""));
    Thread.Sleep(300);
    checks.Add(("Kotlin 绑定：默认异步事件到达",
        Read(kotlinData, "ping.txt") == "ping:\"from-host\"", Read(kotlinData, "ping.txt")));

    host.Events.Publish(new ChannelMessage("sync", "\"sync-host\""));
    checks.Add(("Kotlin 绑定：同步订阅等到 ack",
        Read(kotlinData, "sync.txt") == "sync:\"sync-host\"", Read(kotlinData, "sync.txt")));

    Thread.Sleep(800);
    checks.Add(("Kotlin 绑定：定时器与自发布",
        Read(kotlinData, "self.txt") == "self:\"kotlin-publish\""
            && Read(kotlinData, "syncself.txt") == "syncself:\"kotlin-sync\"",
        $"self={Read(kotlinData, "self.txt")} syncself={Read(kotlinData, "syncself.txt")}"));

    var kotlinDisabled = host.Disable("KotlinHello");
    var kotlinDisabledText = Read(kotlinData, "disabled.txt");
    var kotlinEnabled = host.Enable("KotlinHello");
    var kotlinEnabledText = Read(kotlinData, "enabled.txt");
    var kotlinUnloaded = host.Unload("KotlinHello");
    checks.Add(("Kotlin 绑定：启停与卸载",
        kotlinDisabled && kotlinDisabledText == "disabled" && kotlinEnabled && kotlinEnabledText == "enabled"
            && kotlinUnloaded.Released && Read(kotlinData, "unloaded.txt") == "unloaded",
        kotlinUnloaded.Error ?? Read(kotlinData, "unloaded.txt")));
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

bool StageJavaPlugin(string name, string pluginClass, string fixture, bool parented = false)
{
    var root = parented ? Path.Combine(stage, "parented") : pluginsRoot;
    var package = Path.Combine(root, name);
    var classes = Path.Combine(package, "classes");
    Directory.CreateDirectory(classes);

    var arguments = new List<string> { "-d", classes };
    arguments.AddRange(BindingSources());
    arguments.Add(Path.Combine(repoRoot, "bindings", "java", "fixtures", fixture));

    var compile = RunTool(javac!, arguments);
    if (!compile.Ok)
    {
        Console.WriteLine($"[Java] {name} 编译失败：{compile.Error}");
        return false;
    }

    WritePluginManifest(
        package,
        name,
        "classes",
        ["java", "-cp", "./classes", "hosihikari.Binding", pluginClass]);
    return true;
}

bool StageKotlinPlugin(string name, string pluginClass, string fixture)
{
    var package = Path.Combine(pluginsRoot, name);
    var classes = Path.Combine(package, "classes");
    Directory.CreateDirectory(classes);

    var bindingArguments = new List<string> { "-d", classes };
    bindingArguments.AddRange(BindingSources());
    var bindingCompile = RunTool(javac!, bindingArguments);
    if (!bindingCompile.Ok)
    {
        Console.WriteLine($"[Kotlin] {name} 的 Java 绑定编译失败：{bindingCompile.Error}");
        return false;
    }

    var jar = Path.Combine(package, "plugin.jar");
    var kotlinCompile = RunTool(kotlinc!, new[]
    {
        "-cp", classes,
        "-include-runtime",
        "-d", jar,
        Path.Combine(repoRoot, "bindings", "kotlin", "fixtures", fixture),
    });
    if (!kotlinCompile.Ok)
    {
        Console.WriteLine($"[Kotlin] {name} 编译失败：{kotlinCompile.Error}");
        return false;
    }

    var classpath = "./plugin.jar" + Path.PathSeparator + "./classes";
    WritePluginManifest(
        package,
        name,
        "plugin.jar",
        ["java", "-cp", classpath, "hosihikari.Binding", pluginClass]);
    return true;
}

IEnumerable<string> BindingSources()
{
    foreach (var file in new[] { "Binding.java", "Context.java", "HookResult.java", "Json.java", "Plugin.java" })
    {
        yield return Path.Combine(repoRoot, "bindings", "java", "hosihikari", file);
    }
}

void WritePluginManifest(string package, string name, string entry, string[] command)
{
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
}

static string? FindTool(string command, string argument)
{
    try
    {
        var start = new System.Diagnostics.ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start);
        if (process is null)
        {
            return null;
        }

        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? command : null;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return null;
    }
}

static (bool Ok, string Error) RunTool(string command, IEnumerable<string> arguments)
{
    var start = new System.Diagnostics.ProcessStartInfo(command)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    foreach (var argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }

    using var process = System.Diagnostics.Process.Start(start)!;
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode == 0, (output + error).Trim());
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
