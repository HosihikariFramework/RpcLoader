using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Hosihikari;
using Hosihikari.PluginLoader.Rpc;
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

Check("subscription changes keep order inside synchronous delivery", () =>
{
    var context = new Context();
    using var connection = Start("sync-order", context);
    var deadline = Stopwatch.StartNew();
    while (context.Bus.Active == 0 && deadline.Elapsed < TimeSpan.FromSeconds(5))
    {
        Thread.Sleep(20);
    }

    Assert(context.Bus.Active == 1, $"initial subscriptions: {context.Bus.Active}");
    context.Bus.Publish(new ChannelMessage("gate", "{}"));
    deadline.Restart();
    while (!context.Bus.Published.Contains("sync-order-done") && deadline.Elapsed < TimeSpan.FromSeconds(10))
    {
        Thread.Sleep(20);
    }

    Assert(context.Bus.Published.Contains("sync-order-done"), "child did not finish");
    Assert(context.Bus.Active == 1, $"{context.Bus.Active} subscriptions survived");
});

Check("dispose discards queued incoming work", () =>
{
    var context = new Context();
    context.Bus.BlockTopic = "blocked";
    var connection = Start("dispose-queue", context);
    try
    {
        Assert(context.Bus.Entered.Wait(TimeSpan.FromSeconds(5)), "first event never reached the bus");
        Thread.Sleep(200);
        connection.Dispose();
        context.Bus.Release.Set();
        Thread.Sleep(200);
        Assert(!context.Bus.Published.Contains("after"), "queued event ran after Dispose");
    }
    finally
    {
        context.Bus.Release.Set();
        connection.Dispose();
    }
});

Check("dispose releases a connection whose stdout is held by a grandchild", () =>
{
    var context = new Context();
    var connection = Start("orphan", context);
    var deadline = Stopwatch.StartNew();
    while (context.Faults.IsEmpty && deadline.Elapsed < TimeSpan.FromSeconds(5))
    {
        Thread.Sleep(20);
    }

    Assert(!context.Faults.IsEmpty, "parent exit was not reported");
    var weak = new WeakReference(connection);
    connection.Dispose();
    connection = null!;
    Thread.Sleep(500);
    for (var i = 0; i < 10; i++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    Assert(!weak.IsAlive, "connection is still rooted by inherited stdout");
});

Check("published notifications keep wire order", () =>
{
    var context = new Context();
    context.Bus.SlowTopic = "first";
    using var connection = Start("publish-order", context);
    var deadline = Stopwatch.StartNew();
    while (context.Bus.Published.Count < 2 && deadline.Elapsed < TimeSpan.FromSeconds(5))
    {
        Thread.Sleep(20);
    }

    Assert(context.Bus.Published.SequenceEqual(["first", "second"]),
        string.Join(",", context.Bus.Published));
});

Check("duplicate subscribe upgrades the topic to sync", () =>
{
    var context = new Context();
    using var connection = Start("dupsync", context);
    var deadline = Stopwatch.StartNew();
    while (!context.Bus.Published.Contains("ready") && deadline.Elapsed < TimeSpan.FromSeconds(5))
    {
        Thread.Sleep(20);
    }

    Assert(context.Bus.Published.Contains("ready"), "subscribe upgrade was not acknowledged");
    var watch = Stopwatch.StartNew();
    context.Bus.Publish(new ChannelMessage("dup", "{}"));
    Assert(watch.Elapsed >= TimeSpan.FromMilliseconds(200),
        $"publish returned after {watch.ElapsedMilliseconds}ms");
});

Check("string ids echo and structured payloads survive", () =>
{
    var context = new Context();
    using var connection = Start("string-id", context);
    var deadline = Stopwatch.StartNew();
    while (!context.Bus.Messages.Any(message => message.Topic == "ack")
           && deadline.Elapsed < TimeSpan.FromSeconds(5))
    {
        Thread.Sleep(20);
    }

    var structured = context.Bus.Messages.FirstOrDefault(message => message.Topic == "object");
    Assert(structured is not null && structured.Json == "{\"amount\":5}",
        structured?.Json ?? "(missing)");
    Assert(context.Bus.Messages.Any(message => message.Topic == "ack"), "string id was not echoed");
});

Check("synchronous reentry is bounded", () =>
{
    var context = new Context();
    context.Bus.HoldTopic = "flood";
    using var connection = Start("sync-reentry-flood", context);
    var deadline = Stopwatch.StartNew();
    while (context.Bus.Active == 0 && deadline.Elapsed < TimeSpan.FromSeconds(5))
    {
        Thread.Sleep(20);
    }

    Assert(context.Bus.Active > 0, "subscription never arrived");
    _ = Task.Run(() => context.Bus.Publish(new ChannelMessage("gate", "{}")));

    deadline.Restart();
    while (context.Faults.IsEmpty && deadline.Elapsed < TimeSpan.FromSeconds(20))
    {
        Thread.Sleep(20);
    }

    context.Bus.HoldRelease.Set();
    Assert(context.Faults.Any(reason => reason.Contains("重入请求", StringComparison.Ordinal)),
        context.Faults.IsEmpty ? "no fault" : string.Join(" | ", context.Faults));
    Assert(ProcessOf(connection).WaitForExit(5000), "flooding child survived");
});

Check("an oversized line faults and kills", () =>
{
    var context = new Context();
    using var connection = Start("oversized-line", context);
    var deadline = Stopwatch.StartNew();
    while (context.Faults.IsEmpty && deadline.Elapsed < TimeSpan.FromSeconds(20))
    {
        Thread.Sleep(20);
    }

    Assert(context.Faults.Any(reason => reason.Contains("超过", StringComparison.Ordinal)),
        context.Faults.IsEmpty ? "no fault" : string.Join(" | ", context.Faults));
    Assert(ProcessOf(connection).WaitForExit(5000), "child survived oversized line");
});

Check("malformed lines do not stop the read loop", () =>
{
    var context = new Context();
    using var connection = Start("fuzz-lines", context);
    var deadline = Stopwatch.StartNew();
    while (!context.Bus.Published.Contains("fuzz-done") && deadline.Elapsed < TimeSpan.FromSeconds(5))
    {
        Thread.Sleep(20);
    }

    Assert(context.Bus.Published.Contains("fuzz-done"), string.Join(",", context.Bus.Published));
});

Check("parent exit is detected while stdout stays open", () =>
{
    var context = new Context();
    using var connection = Start("orphan", context);
    var deadline = Stopwatch.StartNew();
    while (context.Faults.IsEmpty && deadline.Elapsed < TimeSpan.FromSeconds(5))
    {
        Thread.Sleep(20);
    }

    Assert(!context.Faults.IsEmpty, "parent exit was not reported");
    Assert(connection.HasExited, "parent still reported alive");
});

Check("synchronous delivery permits a reentrant publish", () =>
{
    var context = new Context();
    using var connection = Start("reentrant", context);
    var result = Request(connection, "", 3000);
    Assert(result.GetString() == "done", "nested publication deadlocked");
    Assert(context.Bus.Published.Contains("nested"), "nested publication never reached the bus");
});

Check("nested synchronous delivery does not deadlock", () =>
{
    var context = new Context();
    using var connection = Start("nested-sync", context);
    var deadline = Stopwatch.StartNew();
    while (context.Bus.Active < 3 && deadline.Elapsed < TimeSpan.FromSeconds(5))
    {
        Thread.Sleep(20);
    }

    Assert(context.Bus.Active >= 3, $"subscriptions: {context.Bus.Active}");
    var watch = Stopwatch.StartNew();
    context.Bus.Publish(new ChannelMessage("gate", "{}"));
    Assert(watch.Elapsed < TimeSpan.FromSeconds(3), $"gate publish took {watch.ElapsedMilliseconds}ms");
    Assert(context.Bus.Published.Contains("inner") && context.Bus.Published.Contains("leaf"),
        string.Join(",", context.Bus.Published));
    Assert(context.Faults.IsEmpty, string.Join(" | ", context.Faults));
});

Check("notifications keep order during synchronous delivery", () =>
{
    var context = new Context();
    context.Bus.SlowTopic = "first";
    using var connection = Start("sync-notify-order", context);
    var deadline = Stopwatch.StartNew();
    while (context.Bus.Active == 0 && deadline.Elapsed < TimeSpan.FromSeconds(5))
    {
        Thread.Sleep(20);
    }

    Assert(context.Bus.Active > 0, "subscription never arrived");
    context.Bus.Publish(new ChannelMessage("gate", "{}"));

    deadline.Restart();
    while (!context.Bus.Published.Contains("second") && deadline.Elapsed < TimeSpan.FromSeconds(5))
    {
        Thread.Sleep(20);
    }

    var sequence = context.Bus.Published.Where(topic => topic is "first" or "second").ToArray();
    Assert(sequence.SequenceEqual(["first", "second"]), string.Join(",", sequence));
});

Check("incoming notification flood faults and kills", () =>
{
    var context = new Context();
    context.Bus.SlowTopic = "flood";
    using var connection = Start("flood-incoming", context);
    var deadline = Stopwatch.StartNew();
    while (context.Faults.IsEmpty && deadline.Elapsed < TimeSpan.FromSeconds(20))
    {
        Thread.Sleep(20);
    }

    Assert(context.Faults.Any(reason => reason.Contains("队列溢出", StringComparison.Ordinal)),
        context.Faults.IsEmpty ? "no fault" : string.Join(" | ", context.Faults));
    Assert(ProcessOf(connection).WaitForExit(5000), "overflowing child survived");
});

Check("a child that never reads replies hits the bounded queues", () =>
{
    var context = new Context();
    using var connection = Start("no-read-replies", context);
    var deadline = Stopwatch.StartNew();
    while (context.Faults.IsEmpty && deadline.Elapsed < TimeSpan.FromSeconds(20))
    {
        Thread.Sleep(20);
    }

    Assert(context.Faults.Any(reason => reason.Contains("队列溢出", StringComparison.Ordinal)),
        context.Faults.IsEmpty ? "no fault" : string.Join(" | ", context.Faults));
    Assert(ProcessOf(connection).WaitForExit(5000), "child survived reply backpressure");
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
    if (mode == "orphan")
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!) == "dotnet")
        {
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }

        start.ArgumentList.Add("--child");
        start.ArgumentList.Add("orphan-child");
        Process.Start(start);
        return;
    }
    if (mode == "orphan-child") { Thread.Sleep(3000); return; }
    if (mode == "fuzz-lines")
    {
        var random = new Random(424242);
        string[] fragments =
        [
            "{", "}", "\"jsonrpc\":\"2.0\"", "\"id\":", "1", "\"method\":",
            "\"hosihikari/log\"", "\"params\":", "null", "[]", "{}", "garbage",
            "\"topic\":", "\"x\"", ",",
        ];
        for (var i = 0; i < 200; i++)
        {
            var builder = new StringBuilder();
            for (var j = 0; j < random.Next(1, 12); j++)
            {
                builder.Append(fragments[random.Next(fragments.Length)]);
            }

            Console.WriteLine(builder.ToString());
        }

        Send(new { jsonrpc = "2.0", method = "hosihikari/events/publish", @params = new { topic = "fuzz-done", json = "\"ok\"" } });
        Thread.Sleep(1000);
        return;
    }
    if (mode == "sync-reentry-flood")
    {
        Send(new { jsonrpc = "2.0", id = 1, method = "hosihikari/events/subscribe", @params = new { topic = "gate", sync = true } });
        if (Console.ReadLine() is null) return;
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("method", out var method)
                || method.GetString() != "hosihikari/events/deliver")
            {
                continue;
            }

            var flood = new StringBuilder();
            for (var i = 0; i < 1100; i++)
            {
                flood.Append(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = 1000 + i,
                    method = "hosihikari/events/publish",
                    @params = new { topic = "flood", json = "\"x\"" },
                })).Append('\n');
            }

            Console.Out.Write(flood);
            Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);
            return;
        }
        return;
    }
    if (mode == "sync-order")
    {
        Send(new { jsonrpc = "2.0", id = 1, method = "hosihikari/events/subscribe", @params = new { topic = "gate", sync = true } });
        if (Console.ReadLine() is null) return;
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("method", out var method)
                || method.GetString() != "hosihikari/events/deliver")
            {
                continue;
            }

            for (var i = 0; i < 100; i++)
            {
                Send(new { jsonrpc = "2.0", id = 1000 + i * 2, method = "hosihikari/events/subscribe", @params = new { topic = "pair" + i } });
                Send(new { jsonrpc = "2.0", id = 1001 + i * 2, method = "hosihikari/events/unsubscribe", @params = new { topic = "pair" + i } });
            }

            var replies = 0;
            while (replies < 200 && Console.ReadLine() is { } reply)
            {
                using var replyDocument = JsonDocument.Parse(reply);
                if (replyDocument.RootElement.TryGetProperty("id", out _))
                {
                    replies++;
                }
            }

            Send(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt64(), result = (object?)null });
            Send(new { jsonrpc = "2.0", method = "hosihikari/events/publish", @params = new { topic = "sync-order-done", json = "\"ok\"" } });
            Thread.Sleep(1000);
            return;
        }
        return;
    }
    if (mode == "dispose-queue")
    {
        Send(new { jsonrpc = "2.0", method = "hosihikari/events/publish", @params = new { topic = "blocked", json = "\"first\"" } });
        Send(new { jsonrpc = "2.0", method = "hosihikari/events/publish", @params = new { topic = "after", json = "\"second\"" } });
        while (Console.ReadLine() is not null)
        {
        }
        return;
    }
    if (mode == "nested-sync")
    {
        Send(new { jsonrpc = "2.0", id = 1, method = "hosihikari/events/subscribe", @params = new { topic = "gate", sync = true } });
        Send(new { jsonrpc = "2.0", id = 2, method = "hosihikari/events/subscribe", @params = new { topic = "inner", sync = true } });
        Send(new { jsonrpc = "2.0", id = 3, method = "hosihikari/events/subscribe", @params = new { topic = "leaf" } });
        long gateDeliver = 0;
        long innerDeliver = 0;
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("method", out var methodElement))
            {
                if (methodElement.GetString() != "hosihikari/events/deliver")
                {
                    continue;
                }

                var topic = root.GetProperty("params").GetProperty("topic").GetString();
                var id = root.TryGetProperty("id", out var idElement) ? idElement.GetInt64() : 0;
                if (topic == "gate")
                {
                    gateDeliver = id;
                    Send(new { jsonrpc = "2.0", id = 10, method = "hosihikari/events/publish", @params = new { topic = "inner", json = "\"i\"" } });
                }
                else if (topic == "inner")
                {
                    innerDeliver = id;
                    Send(new { jsonrpc = "2.0", id = 11, method = "hosihikari/events/publish", @params = new { topic = "leaf", json = "\"l\"" } });
                }
                else if (id != 0)
                {
                    Send(new { jsonrpc = "2.0", id, result = (object?)null });
                }

                continue;
            }

            if (!root.TryGetProperty("id", out var responseId))
            {
                continue;
            }

            var response = responseId.GetInt64();
            if (response == 11)
            {
                Send(new { jsonrpc = "2.0", id = innerDeliver, result = (object?)null });
            }
            else if (response == 10)
            {
                Send(new { jsonrpc = "2.0", id = gateDeliver, result = (object?)null });
            }
        }
        return;
    }
    if (mode == "oversized-line")
    {
        Console.WriteLine(new string('x', 4 * 1024 * 1024 + 1));
        Thread.Sleep(Timeout.Infinite);
        return;
    }
    if (mode == "flood-incoming")
    {
        for (var i = 0; i < 6000; i++)
        {
            Send(new { jsonrpc = "2.0", method = "hosihikari/events/publish", @params = new { topic = "flood", json = "\"x\"" } });
        }

        Thread.Sleep(Timeout.Infinite);
        return;
    }
    if (mode == "no-read-replies")
    {
        for (var i = 0; i < 20000; i++)
        {
            Send(new { jsonrpc = "2.0", id = i, method = "hosihikari/events/subscribe", @params = new { topic = "same" } });
        }

        Thread.Sleep(Timeout.Infinite);
        return;
    }
    if (mode == "sync-notify-order")
    {
        Send(new { jsonrpc = "2.0", id = 1, method = "hosihikari/events/subscribe", @params = new { topic = "gate", sync = true } });
        if (Console.ReadLine() is null) return;
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("method", out var method)
                || method.GetString() != "hosihikari/events/deliver")
            {
                continue;
            }

            Send(new { jsonrpc = "2.0", method = "hosihikari/events/publish", @params = new { topic = "first", json = "\"1\"" } });
            Send(new { jsonrpc = "2.0", method = "hosihikari/events/publish", @params = new { topic = "second", json = "\"2\"" } });
            Thread.Sleep(100);
            Send(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt64(), result = (object?)null });
            Thread.Sleep(1000);
            return;
        }
        return;
    }
    if (mode == "string-id")
    {
        Send(new { jsonrpc = "2.0", id = "17", method = "hosihikari/events/publish", @params = new { topic = "object", json = new { amount = 5 } } });
        var reply = Console.ReadLine();
        if (reply is null || !reply.Contains("\"id\":\"17\""))
        {
            return;
        }

        Send(new { jsonrpc = "2.0", method = "hosihikari/events/publish", @params = new { topic = "ack", json = "\"ok\"" } });
        Thread.Sleep(1000);
        return;
    }
    if (mode == "publish-order")
    {
        Send(new { jsonrpc = "2.0", method = "hosihikari/events/publish", @params = new { topic = "first", json = "\"1\"" } });
        Send(new { jsonrpc = "2.0", method = "hosihikari/events/publish", @params = new { topic = "second", json = "\"2\"" } });
        Thread.Sleep(1000);
        return;
    }
    if (mode == "dupsync")
    {
        Send(new { jsonrpc = "2.0", id = 1, method = "hosihikari/events/subscribe", @params = new { topic = "dup", sync = false } });
        Send(new { jsonrpc = "2.0", id = 2, method = "hosihikari/events/subscribe", @params = new { topic = "dup", sync = true } });
        for (var i = 0; i < 2; i++) if (Console.ReadLine() is null) return;
        Send(new { jsonrpc = "2.0", method = "hosihikari/events/publish", @params = new { topic = "ready", json = "\"ok\"" } });
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("method", out var method)
                && method.GetString() == "hosihikari/events/deliver")
            {
                Thread.Sleep(300);
                Send(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt64(), result = (object?)null });
                return;
            }
        }
        return;
    }
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
    public IServiceProvider CarrierServices => throw new NotSupportedException();
    public bool RegisterLoader(IPluginLoader loader) => throw new NotSupportedException();
    public ConcurrentQueue<string> Faults { get; } = new();
    public void ReportFault(string reason) => Faults.Enqueue(reason);
}
sealed class Bus : IEventBus
{
    private readonly object _gate = new();
    private readonly List<Action<ChannelMessage>> _handlers = [];
    public List<string> Published { get; } = [];
    public List<ChannelMessage> Messages { get; } = [];
    public string? SlowTopic { get; set; }
    public int SlowMilliseconds { get; set; } = 100;
    public string? HoldTopic { get; set; }
    public ManualResetEventSlim HoldRelease { get; } = new(false);
    public string? BlockTopic { get; set; }
    public ManualResetEventSlim Entered { get; } = new(false);
    public ManualResetEventSlim Release { get; } = new(false);
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
        if (channel.Topic == SlowTopic)
        {
            Thread.Sleep(SlowMilliseconds);
        }

        if (channel.Topic == HoldTopic)
        {
            HoldRelease.Wait(TimeSpan.FromSeconds(10));
        }

        if (channel.Topic == BlockTopic)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
        }

        Action<ChannelMessage>[] snapshot;
        lock (_gate) { Published.Add(channel.Topic); Messages.Add(channel); snapshot = _handlers.ToArray(); }
        foreach (var handler in snapshot) handler(channel);
    }
    private sealed class Handle(Action release) : IDisposable { public void Dispose() => release(); }
}
