package refuse;

import hosihikari.Context;
import hosihikari.HookResult;
import hosihikari.Plugin;

import java.nio.file.Files;
import java.nio.file.Path;

public final class RefusePlugin implements Plugin {
    @Override
    public HookResult onEnable(Context context) {
        return HookResult.refuse("现在不行");
    }

    @Override
    public HookResult onDisable(Context context) {
        return Files.exists(Path.of(context.dataDirectory(), "allow.txt"))
            ? HookResult.allow()
            : HookResult.refuse("还不想停");
    }
}
