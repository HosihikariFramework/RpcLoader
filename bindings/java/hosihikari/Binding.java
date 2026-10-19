package hosihikari;

import java.io.BufferedReader;
import java.io.FileDescriptor;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.PrintStream;
import java.io.PrintWriter;
import java.io.StringWriter;
import java.nio.charset.StandardCharsets;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;

public final class Binding {
    private final String pluginClassName;
    private final PrintStream protocol;
    private final Object writeLock = new Object();
    private final ScheduledExecutorService scheduler;
    private Plugin plugin;
    private Context context;

    public static void main(String[] args) throws Exception {
        PrintStream protocol = new PrintStream(
            new FileOutputStream(FileDescriptor.out), true, StandardCharsets.UTF_8);
        PrintStream errors = new PrintStream(
            new FileOutputStream(FileDescriptor.err), true, StandardCharsets.UTF_8);
        System.setOut(errors);
        System.setErr(errors);
        if (args.length < 1) {
            errors.println("usage: java hosihikari.Binding <PluginClass>");
            System.exit(2);
            return;
        }
        new Binding(args[0], protocol).run();
    }

    private Binding(String pluginClassName, PrintStream protocol) {
        this.pluginClassName = pluginClassName;
        this.protocol = protocol;
        this.scheduler = Executors.newScheduledThreadPool(1, runnable -> {
            Thread thread = new Thread(runnable, "hosihikari-timer");
            thread.setDaemon(true);
            return thread;
        });
    }

    private void run() throws IOException {
        BufferedReader reader = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));
        String line;
        while ((line = reader.readLine()) != null) {
            line = line.trim();
            if (line.isEmpty()) {
                continue;
            }
            Map<String, Object> message;
            try {
                message = object(Json.parse(line));
            } catch (RuntimeException exception) {
                exception.printStackTrace();
                continue;
            }
            if (!message.containsKey("method")) {
                continue;
            }
            Thread worker = new Thread(() -> handle(message), "hosihikari-rpc");
            worker.setDaemon(true);
            worker.start();
        }
        scheduler.shutdownNow();
    }

    private void handle(Map<String, Object> message) {
        Object id = message.get("id");
        try {
            Map<String, Object> params = message.get("params") instanceof Map<?, ?> values
                ? object(values)
                : Map.of();
            Object result = dispatch((String) message.get("method"), params);
            if (id != null) {
                reply(id, result);
            }
        } catch (Throwable throwable) {
            if (id != null) {
                replyError(id, throwable);
            } else {
                throwable.printStackTrace();
            }
        }
    }

    private Object dispatch(String method, Map<String, Object> params) throws Exception {
        return switch (method) {
            case "hosihikari/hello" -> {
                load(params);
                yield Map.of("name", context.name());
            }
            case "hosihikari/enable" -> hookResult(plugin == null ? null : plugin.onEnable(context));
            case "hosihikari/disable" -> hookResult(plugin == null ? null : plugin.onDisable(context));
            case "hosihikari/unload" -> {
                if (plugin != null) {
                    plugin.onUnload(context);
                }
                yield null;
            }
            case "hosihikari/events/deliver" -> {
                context.deliver(
                    String.valueOf(params.get("topic")),
                    params.get("json") instanceof String json ? json : null);
                yield null;
            }
            default -> throw new IllegalArgumentException("unknown method " + method);
        };
    }

    private void load(Map<String, Object> params) throws Exception {
        context = new Context(this, params);
        Class<?> type = Class.forName(pluginClassName);
        if (!Plugin.class.isAssignableFrom(type)) {
            throw new IllegalStateException(pluginClassName + " does not implement hosihikari.Plugin");
        }
        plugin = (Plugin) type.getDeclaredConstructor().newInstance();
        plugin.onLoad(context);
    }

    private static Map<String, Object> hookResult(HookResult result) {
        HookResult value = result == null ? HookResult.allow() : result;
        Map<String, Object> output = new LinkedHashMap<>();
        output.put("allowed", value.allowed);
        if (value.reason != null) {
            output.put("reason", value.reason);
        }
        return output;
    }

    void notify(String method, Map<String, Object> params) {
        Map<String, Object> message = new LinkedHashMap<>();
        message.put("jsonrpc", "2.0");
        message.put("method", method);
        message.put("params", params);
        send(message);
    }

    ScheduledFuture<?> schedule(Runnable work, long initialMilliseconds, long periodMilliseconds) {
        Runnable wrapped = () -> {
            try {
                work.run();
            } catch (Throwable throwable) {
                throwable.printStackTrace();
            }
        };
        return periodMilliseconds == 0
            ? scheduler.schedule(wrapped, initialMilliseconds, TimeUnit.MILLISECONDS)
            : scheduler.scheduleAtFixedRate(
                wrapped, initialMilliseconds, periodMilliseconds, TimeUnit.MILLISECONDS);
    }

    private void reply(Object id, Object result) {
        Map<String, Object> message = new LinkedHashMap<>();
        message.put("jsonrpc", "2.0");
        message.put("id", id);
        message.put("result", result);
        send(message);
    }

    private void replyError(Object id, Throwable throwable) {
        StringWriter trace = new StringWriter();
        throwable.printStackTrace(new PrintWriter(trace));
        Map<String, Object> error = new LinkedHashMap<>();
        error.put("code", -32603);
        error.put("message", trace.toString());
        Map<String, Object> message = new LinkedHashMap<>();
        message.put("jsonrpc", "2.0");
        message.put("id", id);
        message.put("error", error);
        send(message);
    }

    private void send(Map<String, Object> message) {
        synchronized (writeLock) {
            protocol.print(Json.stringify(message));
            protocol.print('\n');
            protocol.flush();
        }
    }

    @SuppressWarnings("unchecked")
    private static Map<String, Object> object(Object value) {
        if (value instanceof Map<?, ?> map) {
            return (Map<String, Object>) map;
        }
        throw new IllegalArgumentException("expected a JSON object");
    }
}
