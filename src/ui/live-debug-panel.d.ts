declare module '@rusty-engine/live-debug' {
  export interface LiveDebugPanelMountOptions {
    readonly enabled: boolean;
    readonly presentation?: 'inline' | 'dock' | 'overlay';
  }

  export interface LiveDebugPanelMount {
    dispose(): void;
  }

  export interface RendererMetricsWidgetMountOptions {
    readonly initiallyVisible?: boolean;
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
