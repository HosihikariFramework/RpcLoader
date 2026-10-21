#!/usr/bin/env python3
"""星光框架的进程外绑定（Python）。

行分隔 JSON-RPC 2.0 over stdio：stdout 只走协议，stderr 是日志。
插件文件定义模块级的 on_load(ctx) / on_enable(ctx) / on_disable(ctx) / on_unload(ctx)。
钩子返回 False 或 {"allowed": False, "reason": "..."} 表示拒绝；其余视为允许。
"""

import importlib.util
import json
import os
import sys
import threading
import traceback

try:
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    _protocol_out = sys.stdout
    _protocol_out.reconfigure(encoding="utf-8", newline="\n")
except (AttributeError, ValueError):
    _protocol_out = sys.stdout

# 插件里的 print 走 stderr；stdout 只留给协议。
sys.stdout = sys.stderr

_write_lock = threading.Lock()


def _send(message):
    data = json.dumps(message, ensure_ascii=False)
    with _write_lock:
        _protocol_out.write(data + "\n")
        _protocol_out.flush()


def _notify(method, params):
    _send({"jsonrpc": "2.0", "method": method, "params": params})


def _normalize(value):
    if value is False:
        return {"allowed": False}
    if isinstance(value, dict) and isinstance(value.get("allowed"), bool):
        return {"allowed": value["allowed"], "reason": value.get("reason")}
    return {"allowed": True}


class _Repeating:
    def __init__(self, interval, callback):
        self._interval = interval
        self._callback = callback
        self._stopped = threading.Event()
        self._timer = None
        self._schedule()

    def _schedule(self):
        if self._stopped.is_set():
            return
        self._timer = threading.Timer(self._interval, self._run)
        self._timer.daemon = True
        self._timer.start()

    def _run(self):
        if self._stopped.is_set():
            return
        try:
            self._callback()
        except Exception:
            traceback.print_exc()
        self._schedule()

    def cancel(self):
        self._stopped.set()
        if self._timer is not None:
            self._timer.cancel()


class Context:
    def __init__(self, params):
        self.name = params.get("name", "")
        self.data_directory = params.get("dataDirectory", "")
        self.package_directory = params.get("packageDirectory", "")
        self.config_object = params.get("config") or {}
        self._lock = threading.Lock()
        self._handlers = {}
        self._subscribed = {}

    def config(self, key):
        return self.config_object.get(key)

    def log_info(self, message):
        _notify("hosihikari/log", {"level": 1, "message": str(message)})

    def log_warn(self, message):
        _notify("hosihikari/log", {"level": 2, "message": str(message)})

    def log_error(self, message):
        _notify("hosihikari/log", {"level": 3, "message": str(message)})

    def write_data(self, file, text):
        with open(os.path.join(self.data_directory, file), "w", encoding="utf-8") as stream:
            stream.write(text)

    def publish(self, topic, json=None):
        _notify("hosihikari/events/publish", {"topic": topic, "json": json})

    def subscribe(self, topic, handler, sync=False):
        with self._lock:
            first = topic not in self._subscribed
            self._handlers.setdefault(topic, []).append(handler)
            if first:
                self._subscribed[topic] = bool(sync)
                _notify("hosihikari/events/subscribe", {"topic": topic, "sync": bool(sync)})
            elif sync and not self._subscribed[topic]:
                self._subscribed[topic] = True
                _notify("hosihikari/events/subscribe", {"topic": topic, "sync": True})

        def unsubscribe():
            with self._lock:
                handlers = self._handlers.get(topic)
                if handlers is None:
                    return
                try:
                    handlers.remove(handler)
                except ValueError:
                    return
                if handlers:
                    return
                del self._handlers[topic]
                self._subscribed.pop(topic, None)
                _notify("hosihikari/events/unsubscribe", {"topic": topic})

        return unsubscribe

    def run_later(self, seconds, callback):
        def run():
            try:
                callback()
            except Exception:
                traceback.print_exc()

        timer = threading.Timer(seconds, run)
        timer.daemon = True
        timer.start()
        return timer

    def run_every(self, seconds, callback):
        return _Repeating(seconds, callback)

    def _deliver(self, topic, json):
        with self._lock:
            handlers = list(self._handlers.get(topic, ()))
        for handler in handlers:
            handler(json)


class _Binding:
    def __init__(self, entry):
        self.entry = entry
        self.plugin = None
        self.context = None

    def load(self, params):
        self.context = Context(params)
        path = os.path.abspath(self.entry)
        entry_directory = os.path.dirname(path)
        if entry_directory not in sys.path:
            sys.path.insert(0, entry_directory)
        spec = importlib.util.spec_from_file_location("hosihikari_plugin", path)
        if spec is None or spec.loader is None:
            raise RuntimeError(f"cannot load plugin: {path}")
        module = importlib.util.module_from_spec(spec)
        sys.modules["hosihikari_plugin"] = module
        spec.loader.exec_module(module)
        self.plugin = module
        hook = getattr(module, "on_load", None)
        if hook is not None:
            hook(self.context)

    def hook(self, name):
        if self.plugin is None:
            return None
        return getattr(self.plugin, name, None)

    def handle(self, message):
        method = message.get("method")
        request_id = message.get("id")
        try:
            result = self.dispatch(method, message.get("params") or {})
            if request_id is not None:
                _send({"jsonrpc": "2.0", "id": request_id, "result": result})
        except Exception:
            if request_id is not None:
                _send({
                    "jsonrpc": "2.0",
                    "id": request_id,
                    "error": {"code": -32603, "message": traceback.format_exc()},
                })
            else:
                traceback.print_exc()

    def dispatch(self, method, params):
        if method == "hosihikari/hello":
            self.load(params)
            return {"name": self.context.name}
        if method == "hosihikari/enable":
            hook = self.hook("on_enable")
            return _normalize(hook(self.context) if hook is not None else None)
        if method == "hosihikari/disable":
            hook = self.hook("on_disable")
            return _normalize(hook(self.context) if hook is not None else None)
        if method == "hosihikari/unload":
            hook = self.hook("on_unload")
            if hook is not None:
                hook(self.context)
            return None
        if method == "hosihikari/events/deliver":
            self.context._deliver(params.get("topic"), params.get("json"))
            return None
        raise ValueError(f"unknown method {method}")


def main():
    if len(sys.argv) < 2:
        print("usage: python hosihikari.py <entry.py>", file=sys.stderr)
        return 2

    binding = _Binding(sys.argv[1])
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            message = json.loads(line)
        except Exception:
            traceback.print_exc()
            continue
        if "method" in message:
            threading.Thread(target=binding.handle, args=(message,), daemon=True).start()
        # 插件→宿主的消息都是通知，不会收到应答。
    return 0


if __name__ == "__main__":
    sys.exit(main())
