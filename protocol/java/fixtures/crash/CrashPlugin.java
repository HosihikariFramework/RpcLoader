package crash;

import hosihikari.Context;
import hosihikari.Plugin;

public final class CrashPlugin implements Plugin {
    @Override
    public void onLoad(Context context) {
        context.scheduleOnce(0.05, () -> System.exit(1));
    }
}
