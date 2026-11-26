/**
 * Which microphone and speakers to use, remembered per device (browser storage). An unset or
 * vanished choice means the system default. The browser only reveals device names after
 * microphone permission has been granted once.
 */
export type DeviceKind = "audioinput" | "audiooutput";
export type DevicePrefs = Partial<Record<DeviceKind, string>>;

const KEY = "snacka.devices";

export function loadDevicePrefs(): DevicePrefs {
  try {
    return JSON.parse(localStorage.getItem(KEY) ?? "{}") as DevicePrefs;
  } catch {
    return {};
  }
}

export function saveDevicePref(kind: DeviceKind, deviceId: string): void {
  try {
    localStorage.setItem(KEY, JSON.stringify({ ...loadDevicePrefs(), [kind]: deviceId }));
  } catch {
    /* storage unavailable: the choice lasts this session */
  }
}

export async function listDevices(kind: DeviceKind): Promise<MediaDeviceInfo[]> {
  if (!navigator.mediaDevices?.enumerateDevices) return [];
  return (await navigator.mediaDevices.enumerateDevices()).filter((d) => d.kind === kind);
}

/** Whether speakers can be chosen at all (setSinkId: Chromium and Firefox, not Safari/WKWebView). */
export const canChooseSpeakers = typeof HTMLMediaElement !== "undefined" && "setSinkId" in HTMLMediaElement.prototype;
