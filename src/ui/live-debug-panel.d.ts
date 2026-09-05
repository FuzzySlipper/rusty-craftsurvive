declare module '@rusty-engine/live-debug' {
  export interface LiveDebugResult {
    readonly succeeded: boolean;
    readonly message: string;
  }

  export interface LiveDebugTransport {
    catalog(signal?: AbortSignal): Promise<unknown>;
    execute(command: string, signal?: AbortSignal): Promise<LiveDebugResult>;
  }

  export interface LiveDebugHttpTransportOptions {
    readonly origin?: string;
    readonly fetch?: typeof globalThis.fetch;
  }

  export function createLiveDebugHttpTransport(
    options?: LiveDebugHttpTransportOptions,
  ): LiveDebugTransport;

  export interface LiveDebugPanelMountOptions {
    readonly enabled: boolean;
    readonly presentation?: 'inline' | 'dock' | 'overlay';
    readonly transport?: LiveDebugTransport;
  }

  export interface LiveDebugPanelMount {
    dispose(): void;
  }

  export interface RendererMetricsWidgetMountOptions {
    readonly initiallyVisible?: boolean;
    readonly transport?: LiveDebugTransport;
  }

  export interface RendererMetricsWidgetMount {
    dispose(): void;
  }

  export function mountLiveDebugPanel(
    host: HTMLElement,
    options: LiveDebugPanelMountOptions,
  ): Promise<LiveDebugPanelMount>;

  export function mountRendererMetricsWidget(
    host: HTMLElement,
    options?: RendererMetricsWidgetMountOptions,
  ): RendererMetricsWidgetMount;
}
