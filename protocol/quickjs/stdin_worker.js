// Reads one line at a time from stdin on its own OS thread. The main thread
// stays free to run the QuickJS event loop (timers, messages, promises).
import * as std from 'qjs:std';
import * as os from 'qjs:os';

const parent = os.Worker.parent;
for (;;) {
  const line = std.in.getline();
  if (line === null) {
    parent.postMessage({ eof: true });
    break;
  }
  parent.postMessage({ line });
}
