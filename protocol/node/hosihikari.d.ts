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
  scheduleOnce(seconds: number, callback: () => void): ReturnType<typeof setTimeout>;
  schedulePeriodic(seconds: number, callback: () => void): ReturnType<typeof setTimeout>;
}

/** 线上诊断（ADR-0059）：code 由插件定义，severity 是 Info / Warning / Error。 */
interface RpcDiagnostic {
  code?: string;
  severity?: 'Info' | 'Warning' | 'Error';
  message?: string;
}

type RpcHookResult =
  | void
  | boolean
  | { allowed: boolean; reason?: string | null; diagnostic?: RpcDiagnostic };

interface RpcPlugin {
  onLoad?(ctx: RpcPluginContext): void | Promise<void>;
  onEnable?(ctx: RpcPluginContext): RpcHookResult | Promise<RpcHookResult>;
  onDisable?(ctx: RpcPluginContext): RpcHookResult | Promise<RpcHookResult>;
  onUnload?(ctx: RpcPluginContext): void | Promise<void>;
}
