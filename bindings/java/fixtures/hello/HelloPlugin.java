package hello;

import hosihikari.Context;
import hosihikari.HookResult;
import hosihikari.Plugin;

public final class HelloPlugin implements Plugin {
    private Context context;
    private int ticks;

    private void write(String file, String text) {
        context.writeData(file, text);
    }

    @Override
    public void onLoad(Context loaded) {
        context = loaded;
        write("body.txt", "body:" + context.config("greeting"));
        context.subscribe("ping", json -> write("ping.txt", "ping:" + json));
        context.subscribe("sync", json -> write("sync.txt", "sync:" + json), true);
        context.subscribe("java-self", json -> write("self.txt", "self:" + json));
        context.subscribe("java-syncself", json -> write("syncself.txt", "syncself:" + json), true);
        context.runLater(500, () -> context.publish("java-self", "\"java-publish\""));
        context.runLater(700, () -> context.publish("java-syncself", "\"java-sync\""));
        context.runEvery(200, () -> write("every.txt", Integer.toString(++ticks)));
        context.logInfo("java hello loaded");
    }

    @Override
    public HookResult onEnable(Context loaded) {
        write("enabled.txt", "enabled");
        return HookResult.allow();
    }

    @Override
    public HookResult onDisable(Context loaded) {
        write("disabled.txt", "disabled");
        return HookResult.allow();
    }

    @Override
    public void onUnload(Context loaded) {
        write("unloaded.txt", "unloaded");
    }
}
