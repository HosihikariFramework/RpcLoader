import os


def on_load(ctx):
    pass


def on_enable(ctx):
    return False


def on_disable(ctx):
    if os.path.exists(os.path.join(ctx.data_directory, "allow.txt")):
        return True
    return {"allowed": False, "reason": "还不想停"}


def on_unload(ctx):
    pass
