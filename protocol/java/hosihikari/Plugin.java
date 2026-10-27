package hosihikari;

public interface Plugin {
    default void onLoad(Context context) {
    }

    default HookResult onEnable(Context context) {
        return HookResult.allow();
    }

    default HookResult onDisable(Context context) {
        return HookResult.allow();
    }

    default void onUnload(Context context) {
    }
}
