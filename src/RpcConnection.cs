using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hosihikari;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Hosihikari.PluginProvider.Rpc;

/// <summary>
/// 一个进程外插件的 JSON-RPC 连接与进程监督（ADR-0031）。
///
/// 传输是行分隔的 JSON-RPC 2.0：stdout 只走协议，stderr 是日志文本。
/// 宿主→插件发请求（hello / enable / disable / unload / events/deliver），
/// 插件→宿主也发请求（events/publish / subscribe / unsubscribe / log）。
/// 读线程只负责解析与配对；插件发来的请求丢到线程池处理，避免读线程自己等自己。
/// </summary>
internal sealed class RpcConnection : IDisposable
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UnloadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SyncEventTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly Process _process;
    private readonly IPluginContext _context;
    private readonly string _name;
    private readonly StreamWriter _input;
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly object _pendingLock = new();
    private readonly object _writeLock = new();
    private readonly Dictionary<string, IDisposable> _subscriptions = new(StringComparer.Ordinal);
    private readonly object _subscriptionLock = new();
    private long _nextId;
    private volatile bool _dead;
    private volatile bool _disposing;
    private volatile bool _hasExited;
    private int _faultReported;

    private RpcConnection(Process process, IPluginContext context)
    {
        _process = process;
        _context = context;
        _name = context.Name;
        _input = process.StandardInput;
        _input.AutoFlush = false;

        new Thread(ReadLoop) { IsBackground = true, Name = $"hosihikari-rpc:{_name}" }.Start();
        new Thread(ErrorLoop) { IsBackground = true, Name = $"hosihikari-rpc-err:{_name}" }.Start();
    }

    public static RpcConnection Start(IReadOnlyList<string> command, string packageDirectory, IPluginContext context)
    {
        var start = new ProcessStartInfo(command[0])
        {
            WorkingDirectory = packageDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        for (var i = 1; i < command.Count; i++)
        {
            start.ArgumentList.Add(command[i]);
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException("进程没有启动");
        return new RpcConnection(process, context);
    }

    public bool HasExited
    {
        get
        {
            if (_hasExited)
            {
                return true;
            }

            try
            {
                return _hasExited = _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    /// <summary>握手：把名字、目录与配置快照交给插件；返回时绑定的 onLoad 已经跑完。</summary>
    public void Hello(IPluginContext context)
    {
        var config = context.Configuration.AsEnumerable()
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);

        SendRequest("hosihikari/hello", new
        {
            protocolVersion = 1,
            name = context.Name,
            dataDirectory = context.DataDirectory,
            packageDirectory = context.PackageDirectory,
            config,
        }, LoadTimeout);
    }

    public PluginHookResult Hook(string method)
    {
        var result = SendRequest(method, new { }, HookTimeout);
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("allowed", out var allowed)
            || allowed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidOperationException($"{method} 的结果缺少布尔 allowed");
        }

        var reason = result.TryGetProperty("reason", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        return allowed.GetBoolean() ? PluginHookResult.Allow() : PluginHookResult.Refuse(reason);
    }

    public void Unload()
    {
        try
        {
            if (!_dead)
            {
                SendRequest("hosihikari/unload", new { }, UnloadTimeout);
            }
        }
        catch (Exception ex)
        {
            _context.Logger.LogWarning(ex, "{Name}: 卸载请求没有完成", _name);
        }

        Dispose();
    }

    private JsonElement SendRequest(string method, object parameters, TimeSpan timeout)
    {
        if (_dead)
        {
            throw new InvalidOperationException("插件进程已经退出");
        }

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock)
        {
            _pending[id] = completion;
        }

        try
        {
            Write(new { jsonrpc = "2.0", id, method, @params = parameters });
            try
            {
                if (!completion.Task.Wait(timeout))
                {
                    throw new TimeoutException($"{method} 在 {timeout.TotalSeconds:0} 秒内没有响应");
                }
            }
            catch (AggregateException ex)
            {
                throw ex.InnerException ?? ex;
            }

            return completion.Task.GetAwaiter().GetResult();
        }
        finally
        {
            lock (_pendingLock)
            {
                _pending.Remove(id);
            }
        }
    }

    private void Write(object message)
    {
        if (_dead)
        {
            throw new InvalidOperationException("插件进程已经退出");
        }

        var json = JsonSerializer.Serialize(message);
        lock (_writeLock)
        {
            _input.WriteLine(json);
            _input.Flush();
        }
    }

    private void ReadLoop()
    {
        try
        {
            while (true)
            {
                var line = _process.StandardOutput.ReadLine();
                if (line is null)
                {
                    break;
                }
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException ex)
                {
                    _context.Logger.LogWarning(ex, "{Name}: stdout 上收到非 JSON 行", _name);
                    continue;
                }

                using (document)
                {
                    var root = document.RootElement;
                    if (root.TryGetProperty("id", out var idElement)
                        && root.TryGetProperty("result", out var result))
                    {
                        Complete(idElement, result, default, root);
                        continue;
                    }

                    if (root.TryGetProperty("id", out var errorIdElement)
                        && root.TryGetProperty("error", out var error))
                    {
                        Complete(errorIdElement, default, error, root);
                        continue;
                    }

                    if (!root.TryGetProperty("method", out var methodElement))
                    {
                        continue;
                    }

                    var method = methodElement.GetString() ?? "";
                    var parameters = root.TryGetProperty("params", out var parametersElement)
                        ? parametersElement.Clone()
                        : default;
                    var requestId = root.TryGetProperty("id", out var idValue) && idValue.TryGetInt64(out var parsed)
                        ? parsed
                        : (long?)null;

                    // 读线程不能自己去处理：处理里可能再发请求并等响应，一等就把自己等死。
                    _ = Task.Run(() => HandlePluginRequest(method, parameters, requestId));
                }
            }
        }
        catch (Exception ex)
        {
            if (!_disposing)
            {
                _context.Logger.LogError(ex, "{Name}: 读进程输出失败", _name);
            }
        }
        finally
        {
            _dead = true;
            FailPending(new InvalidOperationException("插件进程的 stdout 已关闭"));
            if (!_disposing)
            {
                Fault("插件进程退出");
            }
        }
    }

    private void Complete(JsonElement idElement, JsonElement result, JsonElement error, JsonElement root)
    {
        if (!idElement.TryGetInt64(out var id))
        {
            return;
        }

        TaskCompletionSource<JsonElement>? completion;
        lock (_pendingLock)
        {
            _pending.TryGetValue(id, out completion);
        }
        if (completion is null)
        {
            return;
        }

        if (root.TryGetProperty("error", out _))
        {
            var message = error.ValueKind == JsonValueKind.Object
                          && error.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : error.ToString();
            completion.TrySetException(new InvalidOperationException(message ?? "插件返回错误"));
        }
        else
        {
            completion.TrySetResult(result.Clone());
        }
    }

    private void HandlePluginRequest(string method, JsonElement parameters, long? requestId)
    {
        try
        {
            switch (method)
            {
                case "hosihikari/events/publish":
                    Publish(parameters);
                    break;
                case "hosihikari/events/subscribe":
                    Subscribe(parameters);
                    break;
                case "hosihikari/events/unsubscribe":
                    Unsubscribe(parameters);
                    break;
                case "hosihikari/log":
                    Log(parameters);
                    break;
                default:
                    throw new InvalidOperationException($"未知方法 {method}");
            }

            if (requestId is { } id)
            {
                Write(new { jsonrpc = "2.0", id, result = (object?)null });
            }
        }
        catch (Exception ex)
        {
            if (requestId is { } id && !_dead)
            {
                try
                {
                    Write(new { jsonrpc = "2.0", id, error = new { code = -32603, message = ex.Message } });
                }
                catch (InvalidOperationException)
                {
                    // 进程已经走了。
                }
            }
            else
            {
                _context.Logger.LogError(ex, "{Name}: 处理 {Method} 失败", _name, method);
            }
        }
    }

    private void Publish(JsonElement parameters)
    {
        var topic = parameters.GetProperty("topic").GetString() ?? "";
        var json = parameters.TryGetProperty("json", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        _context.Events.Publish(new ChannelMessage(topic, json));
    }

    private void Subscribe(JsonElement parameters)
    {
        var topic = parameters.GetProperty("topic").GetString() ?? "";
        var sync = parameters.TryGetProperty("sync", out var value) && value.ValueKind == JsonValueKind.True;
        lock (_subscriptionLock)
        {
            if (_subscriptions.ContainsKey(topic))
            {
                return;
            }

            _subscriptions[topic] = _context.Events.Subscribe<ChannelMessage>(message =>
            {
                if (message.Topic == topic)
                {
                    Deliver(topic, message.Json, sync);
                }
            });
        }
    }

    private void Unsubscribe(JsonElement parameters)
    {
        var topic = parameters.GetProperty("topic").GetString() ?? "";
        lock (_subscriptionLock)
        {
            if (_subscriptions.Remove(topic, out var subscription))
            {
                subscription.Dispose();
            }
        }
    }

    private void Deliver(string topic, string? json, bool sync)
    {
        if (!sync)
        {
            try
            {
                Write(new
                {
                    jsonrpc = "2.0",
                    method = "hosihikari/events/deliver",
                    @params = new { topic, json },
                });
            }
            catch (Exception ex)
            {
                _context.Logger.LogWarning(ex, "{Name}: 异步事件投递失败", _name);
            }

            return;
        }

        try
        {
            SendRequest("hosihikari/events/deliver", new { topic, json }, SyncEventTimeout);
        }
        catch (Exception ex)
        {
            Fault($"同步事件投递失败：{ex.Message}");
        }
    }

    private void Log(JsonElement parameters)
    {
        var level = parameters.TryGetProperty("level", out var levelElement) ? levelElement.GetInt32() : 1;
        var message = parameters.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString()
            : null;
        switch (level)
        {
            case 0:
                _context.Logger.LogDebug("{Message}", message);
                break;
            case 2:
                _context.Logger.LogWarning("{Message}", message);
                break;
            case 3:
                _context.Logger.LogError("{Message}", message);
                break;
            case 4:
                _context.Logger.LogCritical("{Message}", message);
                break;
            default:
                _context.Logger.LogInformation("{Message}", message);
                break;
        }
    }

    private void ErrorLoop()
    {
        try
        {
            while (true)
            {
                var line = _process.StandardError.ReadLine();
                if (line is null)
                {
                    break;
                }
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                _context.Logger.LogInformation("{Line}", line);
            }
        }
        catch (Exception ex)
        {
            if (!_disposing)
            {
                _context.Logger.LogWarning(ex, "{Name}: 读 stderr 失败", _name);
            }
        }
    }

    private void Fault(string reason)
    {
        if (Interlocked.Exchange(ref _faultReported, 1) != 0)
        {
            return;
        }

        try
        {
            _context.ReportFault(reason);
        }
        catch (Exception ex)
        {
            _context.Logger.LogError(ex, "{Name}: 报告故障失败", _name);
        }
    }

    private void FailPending(Exception error)
    {
        TaskCompletionSource<JsonElement>[] pending;
        lock (_pendingLock)
        {
            pending = _pending.Values.ToArray();
            _pending.Clear();
        }

        foreach (var completion in pending)
        {
            completion.TrySetException(error);
        }
    }

    public void Dispose()
    {
        if (_disposing)
        {
            return;
        }
        _disposing = true;

        try
        {
            _input.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // 已经关了。
        }

        try
        {
            if (!_process.WaitForExit(ShutdownTimeout))
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit();
            }
            _hasExited = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _hasExited = true;
        }

        lock (_subscriptionLock)
        {
            foreach (var subscription in _subscriptions.Values)
            {
                subscription.Dispose();
            }
            _subscriptions.Clear();
        }

        FailPending(new ObjectDisposedException(nameof(RpcConnection)));
    }
}
