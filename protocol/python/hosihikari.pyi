"""Type stubs for hosihikari.py. Keep this file in step with the binding."""

from typing import Any, Callable, Dict, Optional, TypedDict

class Context:
    name: str
    data_directory: str
    package_directory: str
    config_object: Dict[str, Any]
    def config(self, key: str) -> Optional[str]: ...
    def log_info(self, message: object) -> None: ...
    def log_warn(self, message: object) -> None: ...
    def log_error(self, message: object) -> None: ...
    def write_data(self, file: str, text: str) -> None: ...
    def publish(self, topic: str, json: Optional[str] = None) -> None: ...
    def subscribe(
        self,
        topic: str,
        handler: Callable[[Optional[str]], None],
        sync: bool = False,
    ) -> Callable[[], None]: ...
    def schedule_once(self, seconds: float, callback: Callable[[], None]) -> Any: ...
    def schedule_periodic(self, seconds: float, callback: Callable[[], None]) -> Any: ...

Hook = Callable[[Context], Any]

class Diagnostic(TypedDict, total=False):
    """钩子拒绝的诊断（ADR-0059）：code 由插件定义，severity 是 Info / Warning / Error。"""

    code: str
    severity: str
    message: str


class HookResult(TypedDict, total=False):
    allowed: bool
    diagnostic: Diagnostic
