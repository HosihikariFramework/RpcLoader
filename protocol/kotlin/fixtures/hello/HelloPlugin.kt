package hello

import hosihikari.Context
import hosihikari.HookResult
import hosihikari.Plugin

class HelloPlugin : Plugin {
    private lateinit var context: Context
    private var timersStarted = false

    private fun write(file: String, text: String) {
        context.writeData(file, text)
    }

    override fun onLoad(loaded: Context) {
        context = loaded
        write("body.txt", "body:" + context.config("greeting"))
        context.subscribe("ping") { json -> write("ping.txt", "ping:" + json) }
        context.subscribe("sync", { json -> write("sync.txt", "sync:" + json) }, true)
        context.subscribe("kotlin-self") { json -> write("self.txt", "self:" + json) }
        context.subscribe("kotlin-syncself", { json -> write("syncself.txt", "syncself:" + json) }, true)
        context.logInfo("kotlin hello loaded")
    }

    override fun onEnable(loaded: Context): HookResult {
        context = loaded
        write("enabled.txt", "enabled")
        // Timers start once the enable gate is open; scheduling from onLoad can race
        // a slow JVM and drop the events while the plugin is still disabled.
        if (!timersStarted) {
            timersStarted = true
            context.scheduleOnce(100) { context.publish("kotlin-self", "\"kotlin-publish\"") }
            context.scheduleOnce(200) { context.publish("kotlin-syncself", "\"kotlin-sync\"") }
        }
        return HookResult.allow()
    }

    override fun onDisable(loaded: Context): HookResult {
        write("disabled.txt", "disabled")
        return HookResult.allow()
    }

    override fun onUnload(loaded: Context) {
        write("unloaded.txt", "unloaded")
    }
}
