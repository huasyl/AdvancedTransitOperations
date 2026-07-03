declare module "cs2/api" {
  export type ValueBinding<T = any> = unknown;

  export function bindValue<T = any>(group: string, name: string): ValueBinding<T>;
  export function useValue<T = any>(binding: ValueBinding<T>): T;
  export function trigger(group: string, action: string, payload?: any): void;
}

declare module "cs2/l10n" {
  export interface LocalizationLike {
    translate?: (key: string, fallback?: string) => string;
    locale?: any;
    activeLocale?: any;
    localeCode?: string;
    language?: string;
    activeLocaleCode?: string;
    activeLanguage?: string;
    id?: string;
    code?: string;
    name?: string;
  }

  export function useLocalization(): LocalizationLike | null;
  export const locale: any;
  export const activeLocale: any;
}

declare module "cs2/ui" {
  import type * as React from "react";

  export const Panel: React.ComponentType<any>;
  export const Scrollable: React.ComponentType<any>;
  export const Button: React.ComponentType<any>;
  export const Tooltip: React.ComponentType<any>;
}

declare module "cs2/modding" {
  export interface ModRegistrar {
    extend(path: string, exportName: string, callback: (input: any) => any): void;
    append(target: string, component: any): void;
  }
}

interface NativeWorkbenchMountHandle {
  refreshData?: () => Promise<void> | void;
  unmount?: () => void;
}

interface NativeWorkbenchBundleApi {
  mount: (mountNode: HTMLElement) => NativeWorkbenchMountHandle | void;
  unmount?: (mountNode: HTMLElement) => void;
}

interface NativeWorkbenchBuildFlavor {
  debugTools?: boolean;
  verboseLogs?: boolean;
}

interface NativeWorkbenchEngine {
  call(method: string, payload?: any): Promise<any>;
  on?(event: string, handler: (...args: any[]) => void): void;
  off?(event: string, handler: (...args: any[]) => void): void;
}

interface Window {
  engine?: NativeWorkbenchEngine;
  __RT_DEBUG_TOOLS__?: boolean;
  __RT_VERBOSE_LOGS__?: boolean;
  __RTWB_TRACE__?: ((eventName: string, details?: Record<string, any>) => void) | undefined;
  __RT_NATIVE_SCHEDULE_LOCALE__?: string;
  __RT_NATIVE_WORKBENCH_CLOSE__?: (() => void) | undefined;
  __RT_WORKBENCH_ACTIVE_TRANSPORT_MODE__?: string;
  __RT_WORKBENCH_ACTIVE_PAGE__?: string;
  __RT_WORKBENCH_SELECTED_LINE_ID__?: string;
  __RT_WORKBENCH_SELECTED_EDIT_LINE__?: string;
  RTDispatchWorkbenchNativeSchedule?: NativeWorkbenchBundleApi;
  __RT_NATIVE_WORKBENCH_PAGE__?: string;
}
