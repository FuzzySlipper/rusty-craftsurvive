export interface LiveDebugPanelMountOptions {
  readonly enabled: boolean;
  readonly presentation?: 'inline' | 'dock' | 'overlay';
}

export interface LiveDebugPanelMount {
  dispose(): void;
}

export declare function mountLiveDebugPanel(
  host: HTMLElement,
  options: LiveDebugPanelMountOptions,
): Promise<LiveDebugPanelMount>;
