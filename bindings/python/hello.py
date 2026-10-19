import os


def _write(ctx, name, text):
    with open(os.path.join(ctx.data_directory, name), "w", encoding="utf-8") as stream:
        stream.write(text)


def on_load(ctx):
    _write(ctx, "body.txt", "body:" + str(ctx.config("greeting")))
    ctx.subscribe("ping", lambda json: _write(ctx, "ping.txt", "ping:" + str(json)))
    ctx.subscribe("sync", lambda json: _write(ctx, "sync.txt", "sync:" + str(json)), sync=True)
    # 真机没有外部发布者：插件自己发一条，走一遍 publish 与投递。
    ctx.subscribe("py-self", lambda json: _write(ctx, "self.txt", "self:" + str(json)))
    ctx.subscribe("py-syncself", lambda json: _write(ctx, "syncself.txt", "syncself:" + str(json)), sync=True)
    ctx.run_later(0.5, lambda: ctx.publish("py-self", '"py-publish"'))
    ctx.run_later(0.7, lambda: ctx.publish("py-syncself", '"py-sync"'))
    ctx.log_info("python hello loaded")


def on_enable(ctx):
    _write(ctx, "enabled.txt", "enabled")


def on_disable(ctx):
    _write(ctx, "disabled.txt", "disabled")


def on_unload(ctx):
    _write(ctx, "unloaded.txt", "unloaded")
