using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Hosihikari;
using Hosihikari.PluginProvider.Rpc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

if (args.FirstOrDefault() == "--child")
{
    Child(args[1]);
    return;
}

Check("blocked writes obey the request deadline", () =>
{
    using var connection = Start("blocked", new Context());
    var watch = Stopwatch.StartNew();
    ExpectTimeout(() => Request(connection, new string('x', 4 * 1024 * 1024), 150));
    Assert(watch.Elapsed < TimeSpan.FromSeconds(3), "write ignored the deadline");
    Assert(ProcessOf(connection).WaitForExit(2000), "timed out child survived");
});

Check("waiting for another writer uses the same deadline", () =>
{
    using var connection = Start("blocked", new Context());
    var first = Task.Run(() =>
    {
        try { Request(connection, new string('x', 4 * 1024 * 1024), 1500); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException) { return; }
        throw new Exception("blocked writer unexpectedly succeeded");
    });
    Thread.Sleep(100);
    var watch = Stopwatch.StartNew();
    ExpectTimeout(() => Request(connection, "small", 150));
    Assert(watch.Elapsed < TimeSpan.FromSeconds(1), "write gate ignored the deadline");
    Assert(first.Wait(2500), "first writer was left blocked");
});

Check("write and response share one deadline", () =>
{
    using var connection = Start("delayed", new Context());
    ExpectTimeout(() => Request(connection, new string('x', 4 * 1024 * 1024), 250));
});

Check("asynchronous notifications cannot block forever", () =>
{
    using var connection = Start("blocked", new Context());
    var watch = Stopwatch.StartNew();
    Invoke(connection, "Deliver", "test", new string('x', 4 * 1024 * 1024), false);
    Assert(watch.Elapsed < TimeSpan.FromSeconds(8), "notification never finished");
    Assert(ProcessOf(connection).WaitForExit(2000), "notification timeout left the child alive");
});

Check("disposing an active writer terminates the write", () =>
{
    var connection = Start("blocked", new Context());
    var writer = Task.Run(() =>
    {
        try { Request(connection, new string('x', 4 * 1024 * 1024), 30000); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException) { return; }
        throw new Exception("blocked writer unexpectedly succeeded");
    });
    Thread.Sleep(100);
    var watch = Stopwatch.StartNew();
    connection.Dispose();
    Assert(watch.Elapsed < TimeSpan.FromSeconds(3), "dispose waited for the request's long deadline");
    Assert(writer.Wait(2000), "dispose left the writer running");
    Assert(connection.HasExited, "disposed child survived");
});

Check("subscription changes retain receive order", () =>
{
    var context = new Context();
    using var connection = Start("order", context);
    Assert(ProcessOf(connection).WaitForExit(10000), "subscription replies did not finish");
    Assert(context.Bus.Active == 0, $"{context.Bus.Active} subscriptions survived their unsubscribe");
});

Check("synchronous delivery permits a reentrant publish", () =>
{
    var context = new Context();
    using var connection = Start("reentrant", context);
    var result = Request(connection, "", 3000);
    Assert(result.GetString() == "done", "nested publication deadlocked");
    Assert(context.Bus.Published.Contains("nested"), "nested publication never reached the bus");
});

static void Check(string name, Action test)
{
    test();
    Console.WriteLine("PASS " + name);
}
static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void ExpectTimeout(Action action)
{
    try { action(); }
    catch (TimeoutException) { return; }

    throw new Exception("request unexpectedly succeeded");
}
static object? Invoke(RpcConnection connection, string method, params object[] arguments)
{
    try
    {
        return typeof(RpcConnection).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(connection, arguments);
    }
    catch (TargetInvocationException ex) { throw ex.InnerException!; }
}
static JsonElement Request(RpcConnection connection, string payload, int milliseconds) =>
    (JsonElement)Invoke(connection, "SendRequest", "test", new { payload }, TimeSpan.FromMilliseconds(milliseconds))!;
static Process ProcessOf(RpcConnection connection) =>
    (Process)typeof(RpcConnection).GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
static RpcConnection Start(string mode, Context context)
{
    var command = new List<string> { Environment.ProcessPath! };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
        command.Add(Assembly.GetExecutingAssembly().Location);
    command.AddRange(["--child", mode]);
    return RpcConnection.Start(command, Path.GetTempPath(), context);
}

static void Child(string mode)
{
    static void Send(object message) => Console.WriteLine(JsonSerializer.Serialize(message));
    if (mode == "blocked") { Thread.Sleep(Timeout.Infinite); return; }
    if (mode == "delayed") Thread.Sleep(180);
    if (mode == "order")
    {
        for (var i = 0; i < 200; i++)
        {
            Send(new { jsonrpc = "2.0", id = i * 2, method = "hosihikari/events/subscribe", @params = new { topic = "topic" + i } });
            Send(new { jsonrpc = "2.0", id = i * 2 + 1, method = "hosihikari/events/unsubscribe", @params = new { topic = "topic" + i } });
        }
        for (var i = 0; i < 400; i++) if (Console.ReadLine() is null) return;
        return;
    }

    long hostId = 0;
    long deliveryId = 0;
    while (Console.ReadLine() is { } line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.TryGetProperty("method", out var method))
        {
            if (mode == "delayed")
            {
                Thread.Sleep(180);
                Send(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt64(), result = "late" });
            }
            else if (method.GetString() == "test")
            {
                hostId = root.GetProperty("id").GetInt64();
                Send(new { jsonrpc = "2.0", id = 1, method = "hosihikari/events/subscribe", @params = new { topic = "outer", sync = true } });
            }
            else if (method.GetString() == "hosihikari/events/deliver")
            {
                deliveryId = root.GetProperty("id").GetInt64();
                Send(new { jsonrpc = "2.0", id = 3, method = "hosihikari/events/publish", @params = new { topic = "nested" } });
            }
        }
        else
        {
            switch (root.GetProperty("id").GetInt64())
            {
                case 1:
                    Send(new { jsonrpc = "2.0", id = 2, method = "hosihikari/events/publish", @params = new { topic = "outer" } });
                    break;
                case 3:
                    Send(new { jsonrpc = "2.0", id = deliveryId, result = (object?)null });
                    break;
                case 2:
                    Send(new { jsonrpc = "2.0", id = hostId, result = "done" });
                    break;
            }
        }
    }
}

sealed class Context : IPluginContext
{
    public Bus Bus { get; } = new();
    public string Name => "regression";
    public string PackageDirectory => Path.GetTempPath();
    public string DataDirectory => Path.GetTempPath();
    public ILogger Logger => NullLogger.Instance;
    public IConfiguration Configuration => new ConfigurationBuilder().Build();
    public IEventBus Events => Bus;
    public IScheduler Scheduler => throw new NotSupportedException();
    public IPluginServiceRegistry PluginServices => throw new NotSupportedException();
    public IServiceProvider Services => throw new NotSupportedException();
    public void RegisterProvider(IPluginProvider provider) => throw new NotSupportedException();
    public void ReportFault(string reason) { }
}
sealed class Bus : IEventBus
{
    private readonly object _gate = new();
    private readonly List<Action<ChannelMessage>> _handlers = [];
    public List<string> Published { get; } = [];
    public int Active { get { lock (_gate) return _handlers.Count; } }
    public IDisposable Subscribe<T>(Action<T> handler)
    {
        Thread.Sleep(2); // Make an overtaking unsubscribe observable on the old dispatcher.
        Action<ChannelMessage> channelHandler = message => handler((T)(object)message);
        lock (_gate) _handlers.Add(channelHandler);
        return new Handle(() => { lock (_gate) _handlers.Remove(channelHandler); });
    }
    public void Publish<T>(T message)
    {
        var channel = (ChannelMessage)(object)message!;
        Action<ChannelMessage>[] snapshot;
        lock (_gate) { Published.Add(channel.Topic); snapshot = _handlers.ToArray(); }
        foreach (var handler in snapshot) handler(channel);
    }
    private sealed class Handle(Action release) : IDisposable { public void Dispose() => release(); }
}
