package hosihikari;

public final class HookResult {
    public final boolean allowed;
    public final String code;
    public final String severity;
    public final String message;

    private HookResult(boolean allowed, String code, String severity, String message) {
        this.allowed = allowed;
        this.code = code;
        this.severity = severity;
        this.message = message;
    }

    public static HookResult allow() {
        return new HookResult(true, null, null, null);
    }

    /// 拒绝：code 由插件定义，severity 取 Info / Warning / Error，message 进日志与父级的账（ADR-0059）。
    public static HookResult refuse(String code, String severity, String message) {
        return new HookResult(false, code, severity, message);
    }

    public static HookResult refuse(String message) {
        return refuse("refused", "Error", message);
    }
}
