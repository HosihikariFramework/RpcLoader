package hosihikari;

import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ScheduledFuture;
import java.util.function.Consumer;

public final class Context {
    private final Binding binding;
    private final String name;
    private final String dataDirectory;
    private final String packageDirectory;
    private final Map<String, String> config = new LinkedHashMap<>();
    private final Map<String, List<Consumer<String>>> handlers = new HashMap<>();
    private final Set<String> subscribed = new HashSet<>();

    Context(Binding binding, Map<String, Object> params) {
        this.binding = binding;
        this.name = string(params.get("name"));
        this.dataDirectory = string(params.get("dataDirectory"));
        this.packageDirectory = string(params.get("packageDirectory"));
        if (params.get("config") instanceof Map<?, ?> values) {
            for (Map.Entry<?, ?> entry : values.entrySet()) {
                if (entry.getValue() instanceof String text) {
                    config.put(String.valueOf(entry.getKey()), text);
                }
            }
        }
    }

    public String name() {
        return name;
    }

    public String dataDirectory() {
        return dataDirectory;
    }

    public String packageDirectory() {
        return packageDirectory;
    }

    public String config(String key) {
        return config.get(key);
    }

    public Map<String, String> configObject() {
        return Collections.unmodifiableMap(config);
    }

    public void logInfo(String message) {
        log(1, message);
    }

    public void logWarn(String message) {
        log(2, message);
    }

    public void logError(String message) {
        log(3, message);
    }

    private void log(int level, String message) {
        Map<String, Object> params = new LinkedHashMap<>();
        params.put("level", level);
        params.put("message", String.valueOf(message));
        binding.notify("hosihikari/log", params);
    }

    public void writeData(String file, String text) {
        try {
            Files.writeString(Path.of(dataDirectory, file), text, StandardCharsets.UTF_8);
        } catch (IOException exception) {
            throw new UncheckedIOException(exception);
        }
    }

    public void publish(String topic, String json) {
        Map<String, Object> params = new LinkedHashMap<>();
        params.put("topic", topic);
        params.put("json", json);
        binding.notify("hosihikari/events/publish", params);
    }

    public Runnable subscribe(String topic, Consumer<String> handler) {
        return subscribe(topic, handler, false);
    }

    public synchronized Runnable subscribe(String topic, Consumer<String> handler, boolean sync) {
        handlers.computeIfAbsent(topic, key -> new ArrayList<>()).add(handler);
        if (subscribed.add(topic)) {
            Map<String, Object> params = new LinkedHashMap<>();
            params.put("topic", topic);
            params.put("sync", sync);
            binding.notify("hosihikari/events/subscribe", params);
        }
        return () -> {
            synchronized (Context.this) {
                List<Consumer<String>> list = handlers.get(topic);
                if (list == null || !list.remove(handler)) {
                    return;
                }
                if (!list.isEmpty()) {
                    return;
                }
                handlers.remove(topic);
                subscribed.remove(topic);
                binding.notify("hosihikari/events/unsubscribe", Map.of("topic", topic));
            }
        };
    }

    synchronized void deliver(String topic, String json) {
        List<Consumer<String>> list = handlers.get(topic);
        if (list == null) {
            return;
        }
        for (Consumer<String> handler : List.copyOf(list)) {
            handler.accept(json);
        }
    }

    public ScheduledFuture<?> runLater(long milliseconds, Runnable work) {
        return binding.schedule(work, milliseconds, 0);
    }

    public ScheduledFuture<?> runEvery(long milliseconds, Runnable work) {
        return binding.schedule(work, milliseconds, milliseconds);
    }

    private static String string(Object value) {
        return value == null ? "" : String.valueOf(value);
    }
}
