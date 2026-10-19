import os


def on_load(ctx):
    # 钩子之外死亡：宿主应当按故障卸载并通知载体（ADR-0030）。
    ctx.run_later(0.05, lambda: os._exit(1))


def on_enable(ctx):
    pass


def on_disable(ctx):
    pass


def on_unload(ctx):
    pass
