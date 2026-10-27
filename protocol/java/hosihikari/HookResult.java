package hosihikari;

public final class HookResult {
    public final boolean allowed;
    public final String reason;

    private HookResult(boolean allowed, String reason) {
        this.allowed = allowed;
        this.reason = reason;
    }

    public static HookResult allow() {
        return new HookResult(true, null);
    }

    public static HookResult refuse(String reason) {
        return new HookResult(false, reason);
    }
}
