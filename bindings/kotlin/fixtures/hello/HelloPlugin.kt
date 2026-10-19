package hello

import hosihikari.Context
import hosihikari.HookResult
import hosihikari.Plugin

class HelloPlugin : Plugin {
    private lateinit var context: Context

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
        context.runLater(500) { context.publish("kotlin-self", "\"kotlin-publish\"") }
        context.runLater(700) { context.publish("kotlin-syncself", "\"kotlin-sync\"") }
        context.logInfo("kotlin hello loaded")
    }

    override fun onEnable(loaded: Context): HookResult {
        write("enabled.txt", "enabled")
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
