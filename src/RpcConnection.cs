using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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
/// 订阅变更按接收顺序处理；发布在线程池处理，避免读线程自己等自己。
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
    private readonly Stream _input;
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly object _pendingLock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<string, IDisposable> _subscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _syncTopics = new(StringComparer.Ordinal);
    private readonly Channel<Incoming> _incoming = Channel.CreateUnbounded<Incoming>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly object _subscriptionLock = new();
    private long _nextId;
    private volatile bool _dead;
    private volatile bool _disposing;
    private volatile bool _hasExited;
    private int _faultReported;
    private int _syncDeliveryDepth;

    private RpcConnection(Process process, IPluginContext context)
    {
        _process = process;
        _context = context;
        _name = context.Name;
        _input = process.StandardInput.BaseStream;

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnProcessExited();
        if (process.HasExited)
        {
            OnProcessExited();
        }

        new Thread(ReadLoop) { IsBackground = true, Name = $"hosihikari-rpc:{_name}" }.Start();
        new Thread(ErrorLoop) { IsBackground = true, Name = $"hosihikari-rpc-err:{_name}" }.Start();
        new Thread(ProcessIncoming) { IsBackground = true, Name = $"hosihikari-rpc-in:{_name}" }.Start();
    }

    private sealed record Incoming(string Method, JsonElement Parameters, JsonElement RequestId);

    /// <summary>
    /// 子进程发来的通知在一条有序队列上串行处理。发布不能再并发跑，
    /// 否则同一个子进程先发的状态可能后到；读线程仍空着收宿主请求的应答。
    /// </summary>
    private void ProcessIncoming()
    {
        while (_incoming.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (_incoming.Reader.TryRead(out var message))
            {
                HandlePluginRequest(message.Method, message.Parameters, message.RequestId);
            }
        }
    }

    /// <summary>
    /// 子进程退出的权威信号。stdout 可能被孙进程继承而迟迟不 EOF，不能只等 ReadLoop。
    /// </summary>
    private void OnProcessExited()
    {
        if (_disposing)
        {
            return;
        }

        _dead = true;
        FailPending(new InvalidOperationException("插件进程已退出"));
        Fault("插件进程退出");
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

        using var deadline = new CancellationTokenSource(timeout);
        using var abort = deadline.Token.Register(Abort);
        try
        {
            Write(new { jsonrpc = "2.0", id, method, @params = parameters }, deadline.Token);
            return completion.Task.WaitAsync(deadline.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"{method} 在 {timeout.TotalSeconds:0} 秒内没有完成", ex);
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
        using var deadline = new CancellationTokenSource(SyncEventTimeout);
        using var abort = deadline.Token.Register(Abort);
        Write(message, deadline.Token);
    }

    private void Write(object message, CancellationToken cancellation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        _writeLock.Wait(cancellation);
        try
        {
            if (_dead)
            {
                throw new InvalidOperationException("插件进程已经退出");
            }

            // Cancellation kills a stuck reader as well, so the write itself finishes before releasing the gate.
            _input.WriteAsync(bytes, cancellation).AsTask().GetAwaiter().GetResult();
            _input.WriteAsync(new byte[] { (byte)'\n' }, cancellation).AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void Abort()
    {
        _dead = true;
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _context.Logger.LogDebug(ex, "{Name}: 结束插件进程", _name);
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
                    // JSON-RPC 2.0 的 id 可以是字符串或数字；原样回显，不能只认 Int64。
                    var requestId = root.TryGetProperty("id", out var idValue) ? idValue.Clone() : default;

                    // 普通通知按到达顺序走单消费者。同步投递期间允许重入：子进程可能先发一条
                    // 嵌套发布，等宿主处理完才回投递应答 —— 这一条不能排在阻塞的消费者后面。
                    if (Volatile.Read(ref _syncDeliveryDepth) > 0)
                    {
                        _ = Task.Run(() => HandlePluginRequest(method, parameters, requestId));
                    }
                    else
                    {
                        _incoming.Writer.TryWrite(new Incoming(method, parameters, requestId));
                    }
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
        if (!idElement.TryGetInt64(out var id)
            && !(idElement.ValueKind == JsonValueKind.String
                 && long.TryParse(idElement.GetString(), out id)))
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

    private void HandlePluginRequest(string method, JsonElement parameters, JsonElement requestId)
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

            if (requestId.ValueKind != JsonValueKind.Undefined)
            {
                Reply(new { jsonrpc = "2.0", id = requestId, result = (object?)null });
            }
        }
        catch (Exception ex)
        {
            if (requestId.ValueKind != JsonValueKind.Undefined && !_dead)
            {
                Reply(new { jsonrpc = "2.0", id = requestId, error = new { code = -32603, message = ex.Message } });
            }
            else
            {
                _context.Logger.LogError(ex, "{Name}: 处理 {Method} 失败", _name, method);
            }
        }
    }

    private void Reply(object message)
    {
        // A full stdin pipe must not stop the reader from completing the host's outstanding requests.
        _ = Task.Run(() =>
        {
            try
            {
                Write(message);
            }
            catch (Exception ex)
            {
                _context.Logger.LogDebug(ex, "{Name}: 回复插件请求失败", _name);
            }
        });
    }

    private void Publish(JsonElement parameters)
    {
        var topic = parameters.GetProperty("topic").GetString() ?? "";
        var json = parameters.TryGetProperty("json", out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => value.GetRawText(),
            }
            : null;
        _context.Events.Publish(new ChannelMessage(topic, json));
    }

    private void Subscribe(JsonElement parameters)
    {
        var topic = parameters.GetProperty("topic").GetString() ?? "";
        var sync = parameters.TryGetProperty("sync", out var value) && value.ValueKind == JsonValueKind.True;
        lock (_subscriptionLock)
        {
            if (_disposing || _dead)
            {
                return;
            }

            if (_subscriptions.ContainsKey(topic))
            {
                // 已有订阅时，后来者要求 sync 会把整条 topic 升级为同步；绑定只在首次订阅发标志，
                // 所以这里不能只信第一次的 false。
                if (sync)
                {
                    _syncTopics[topic] = true;
                }

                return;
            }

            _syncTopics[topic] = sync;
            _subscriptions[topic] = _context.Events.Subscribe<ChannelMessage>(message =>
            {
                if (message.Topic != topic)
                {
                    return;
                }

                bool current;
                lock (_subscriptionLock)
                {
                    if (!_syncTopics.TryGetValue(topic, out current))
                    {
                        return;
                    }
                }

                Deliver(topic, message.Json, current);
            });
        }
    }

    private void Unsubscribe(JsonElement parameters)
    {
        var topic = parameters.GetProperty("topic").GetString() ?? "";
        lock (_subscriptionLock)
        {
            _syncTopics.Remove(topic);
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

        Interlocked.Increment(ref _syncDeliveryDepth);
        try
        {
            SendRequest("hosihikari/events/deliver", new { topic, json }, SyncEventTimeout);
        }
        catch (Exception ex)
        {
            Fault($"同步事件投递失败：{ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _syncDeliveryDepth);
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
        if (_disposing)
        {
            return;
        }

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
        _incoming.Writer.TryComplete();

        try
        {
            // Closing a stream with an outstanding write can wait for that write; kill its reader first.
            if (!_writeLock.Wait(0))
            {
                Abort();
                if (!_writeLock.Wait(ShutdownTimeout))
                {
                    throw new TimeoutException("插件进程的写入没有结束");
                }
            }
            try
            {
                _input.Close();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // 已经关了。
            }
            finally
            {
                _writeLock.Release();
            }

            try
            {
                if (!_process.WaitForExit(ShutdownTimeout))
                {
                    _process.Kill(entireProcessTree: true);
                    if (!_process.WaitForExit(ShutdownTimeout))
                    {
                        throw new TimeoutException("插件进程没有结束");
                    }
                }
                _hasExited = true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                _hasExited = true;
            }
        }
        finally
        {
            // 即使写入或进程没在限期内收尾，订阅也不能留在事件总线上。
            lock (_subscriptionLock)
            {
                foreach (var subscription in _subscriptions.Values)
                {
                    subscription.Dispose();
                }
                _subscriptions.Clear();
                _syncTopics.Clear();
            }

            FailPending(new ObjectDisposedException(nameof(RpcConnection)));
        }
    }
}
