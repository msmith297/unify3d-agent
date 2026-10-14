/**
 * RPC to the SolidWorks add-in over WebView2 messaging (see src/SwAgent/UI/WebBridge.cs).
 * Outside SolidWorks (plain browser during UI development) calls go to an in-memory mock host.
 */
import { mockRpc } from "./mockHost";

interface WebView {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (e: MessageEvent) => void): void;
}

const webview: WebView | undefined = (window as any).chrome?.webview;
export const isHosted = !!webview;

type Listener = (data: any) => void;
const pending = new Map<number, { resolve: (v: any) => void; reject: (e: Error) => void }>();
const listeners = new Map<string, Set<Listener>>();
let seq = 0;

function dispatch(msg: any) {
  if (msg?.kind === "rpc-result") {
    const p = pending.get(msg.id);
    if (!p) return;
    pending.delete(msg.id);
    if (msg.ok) p.resolve(msg.result);
    else p.reject(new Error(msg.error ?? "Host error"));
  } else if (msg?.kind === "event") {
    emitLocal(msg.name, msg.data);
  }
}

if (webview) webview.addEventListener("message", (e) => dispatch(e.data));

export function rpc<T = unknown>(method: string, params?: unknown): Promise<T> {
  if (!webview) return mockRpc(method, params) as Promise<T>;
  const id = ++seq;
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve, reject });
    webview.postMessage({ kind: "rpc", id, method, params: params ?? null });
  });
}

export function on(name: string, listener: Listener): () => void {
  let set = listeners.get(name);
  if (!set) listeners.set(name, (set = new Set()));
  set.add(listener);
  return () => set!.delete(listener);
}

/** Deliver an event to local listeners (used by the mock host). */
export function emitLocal(name: string, data: unknown) {
  listeners.get(name)?.forEach((l) => {
    try {
      l(data);
    } catch (err) {
      console.error(`listener for ${name} failed`, err);
    }
  });
}
