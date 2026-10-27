// Types for the Node binding in hosihikari.mjs. Keep this file in step with the binding.

interface RpcPluginContext {
  readonly name: string;
  readonly dataDirectory: string;
  readonly packageDirectory: string;
  config(key: string): string | null;
  readonly configObject: Record<string, string>;
  logInfo(message: unknown): void;
  logWarn(message: unknown): void;
  logError(message: unknown): void;
  writeData(file: string, text: string): void;
  publish(topic: string, json?: string | null): Promise<unknown>;
  subscribe(
    topic: string,
    handler: (json: string | null) => void | Promise<void>,
    options?: { sync?: boolean },
  ): () => void;
  scheduleOnce(milliseconds: number, callback: () => void): ReturnType<typeof setTimeout>;
  schedulePeriodic(milliseconds: number, callback: () => void): ReturnType<typeof setTimeout>;
}

type RpcHookResult = void | boolean | { allowed: boolean; reason?: string | null };

interface RpcPlugin {
  onLoad?(ctx: RpcPluginContext): void | Promise<void>;
  onEnable?(ctx: RpcPluginContext): RpcHookResult | Promise<RpcHookResult>;
  onDisable?(ctx: RpcPluginContext): RpcHookResult | Promise<RpcHookResult>;
  onUnload?(ctx: RpcPluginContext): void | Promise<void>;
}
