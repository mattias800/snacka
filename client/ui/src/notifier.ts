/**
 * Desktop notifications behind a small interface, so the native shells can supply their own (the
 * browser one: the Web Notifications API). The user can turn them off in Settings; that choice is
 * remembered per device.
 */
export type NotifyPermission = "granted" | "denied" | "default" | "unsupported";

export interface Notifier {
  permission(): NotifyPermission;
  /** Asks the user (browsers only allow this from a click). */
  request(): Promise<NotifyPermission>;
  show(n: { title: string; body: string; tag: string; onClick: () => void }): void;
}

export const browserNotifier: Notifier = {
  permission: () => (typeof Notification === "undefined" ? "unsupported" : Notification.permission),
  request: async () => (typeof Notification === "undefined" ? "unsupported" : await Notification.requestPermission()),
  show({ title, body, tag, onClick }) {
    if (typeof Notification === "undefined" || Notification.permission !== "granted") return;
    const n = new Notification(title, { body, tag });
    n.onclick = () => {
      window.focus();
      onClick();
      n.close();
    };
  },
};

const OFF_KEY = "snacka.notifications.off";

/** Whether the user turned notifications off on this device (they're on once permission is granted). */
export function notificationsMuted(): boolean {
  try {
    return localStorage.getItem(OFF_KEY) === "1";
  } catch {
    return false;
  }
}

export function setNotificationsMuted(muted: boolean): void {
  try {
    if (muted) localStorage.setItem(OFF_KEY, "1");
    else localStorage.removeItem(OFF_KEY);
  } catch {
    /* storage unavailable: the choice lasts this session */
  }
}

/** The notifier this build uses. Native shells without the Web Notifications API plug theirs in here. */
export const notifier: Notifier = browserNotifier;
