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

export declare function mountLiveDebugPanel(
  host: HTMLElement,
  options: LiveDebugPanelMountOptions,
): Promise<LiveDebugPanelMount>;

export declare function mountRendererMetricsWidget(
  host: HTMLElement,
  options?: RendererMetricsWidgetMountOptions,
): RendererMetricsWidgetMount;
