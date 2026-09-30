export type FeatureRefreshEvent =
  | "dates-changed"
  | "roster-changed"
  | "session-cleared"
  | "signups-changed";

type Listener = (event: FeatureRefreshEvent) => void;

const listeners = new Set<Listener>();

export function publishFeatureRefresh(event: FeatureRefreshEvent): void {
  for (const listener of listeners) {
    listener(event);
  }
}

export function subscribeFeatureRefresh(listener: Listener): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}
