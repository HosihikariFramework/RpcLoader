package crash;

import hosihikari.Context;
import hosihikari.Plugin;

public final class CrashPlugin implements Plugin {
    @Override
    public void onLoad(Context context) {
        context.runLater(50, () -> System.exit(1));
    }
}
