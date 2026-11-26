import { useEffect, useState } from "react";
import { ok, type Api, type Schemas } from "../api/client";
import type { Session } from "../api/session";
import { Avatar } from "./Avatar";
import type { FileLoader } from "./files";
import { notificationsMuted, notifier, setNotificationsMuted, type NotifyPermission } from "../notifier";
import { canChooseSpeakers, listDevices, loadDevicePrefs, type DeviceKind } from "../devices";

export type Profile = Schemas["UserProfileResponse"];

/** The user's own profile: picture, display name and password. Changes reach others live (UserProfileUpdated). */
export function Settings(props: {
  api: Api;
  session: Session;
  /** Switches the device in an ongoing call too (and remembers it). */
  chooseDevice: (kind: DeviceKind, deviceId: string) => Promise<void>;
  loadFile: FileLoader;
  profile: Profile;
  onChange: (p: Profile) => void;
}) {
  const { api, session, loadFile, profile, onChange } = props;
  const [displayName, setDisplayName] = useState(profile.displayName ?? "");
  const [passwords, setPasswords] = useState({ current: "", next: "" });
  const [status, setStatus] = useState<{ error: boolean; text: string } | null>(null);
  const [busy, setBusy] = useState(false);

  const act = async (f: () => Promise<string>) => {
    setBusy(true);
    try {
      setStatus({ error: false, text: await f() });
    } catch (e) {
      setStatus({ error: true, text: (e as Error).message });
    } finally {
      setBusy(false);
    }
  };

  const reload = async () => onChange(ok(await api.GET("/api/Users/me")));

  // Multipart upload: not expressible in the generated client. The server crops to the centre square.
  const uploadAvatar = (file: File) =>
    act(async () => {
      const form = new FormData();
      form.append("file", file, file.name);
      const res = await session.fetch(`${session.serverUrl}/api/Users/me/avatar`, { method: "PUT", body: form });
      if (!res.ok) {
        const body = (await res.json().catch(() => null)) as { error?: string } | null;
        throw new Error(body?.error ?? `Upload failed: ${res.status}`);
      }
      await reload();
      return "Picture updated.";
    });

  const removeAvatar = () =>
    act(async () => {
      const res = await api.DELETE("/api/Users/me/avatar");
      if (!res.response.ok) throw new Error(`Couldn't remove the picture: ${res.response.status}`);
      await reload();
      return "Picture removed.";
    });

  const saveName = (e: React.FormEvent) => {
    e.preventDefault();
    void act(async () => {
      // Blank clears the display name: others then see the username.
      onChange(ok(await api.PUT("/api/Users/me", { body: { username: null, displayName: displayName.trim(), status: null } })));
      return "Name saved.";
    });
  };

  const changePassword = (e: React.FormEvent) => {
    e.preventDefault();
    void act(async () => {
      const res = await api.PUT("/api/Users/me/password", { body: { currentPassword: passwords.current, newPassword: passwords.next } });
      if (!res.response.ok) throw new Error((res.error as { error?: string } | undefined)?.error ?? `Couldn't change the password: ${res.response.status}`);
      // The change signs out every device, this one included: sign this one straight back in.
      const email = session.current?.email;
      if (email) session.set(ok(await api.POST("/api/Auth/login", { body: { email, password: passwords.next } })));
      setPasswords({ current: "", next: "" });
      return "Password changed. Other devices were signed out.";
    });
  };

  return (
    <div className="panel settings" data-testid="settings">
      <h2>Profile</h2>
      {status && (
        <p className={status.error ? "error" : "muted"} data-testid="settings-status">
          {status.text}
        </p>
      )}
      <div className="row avatar-row">
        <Avatar userId={profile.id} name={profile.effectiveDisplayName} avatar={profile.avatar} loadFile={loadFile} size={80} />
        <label className="button primary">
          Upload picture
          <input
            type="file"
            accept="image/png,image/jpeg,image/gif,image/webp"
            hidden
            data-testid="avatar-input"
            disabled={busy}
            onChange={(e) => {
              const file = e.target.files?.[0];
              e.target.value = "";
              if (file) void uploadAvatar(file);
            }}
          />
        </label>
        {profile.avatar && (
          <button className="link" disabled={busy} onClick={() => void removeAvatar()}>
            Remove
          </button>
        )}
      </div>
      <form className="row" onSubmit={saveName}>
        <input value={displayName} maxLength={32} onChange={(e) => setDisplayName(e.target.value)} placeholder={profile.username} aria-label="Display name" />
        <button className="primary" type="submit" disabled={busy}>Save name</button>
      </form>
      <p className="muted">
        Username <strong>{profile.username}</strong> · {profile.email}
      </p>
      <Notifications />
      <Devices choose={props.chooseDevice} />
      <h2>Password</h2>
      <form className="column" onSubmit={changePassword}>
        <input type="password" autoComplete="current-password" value={passwords.current} onChange={(e) => setPasswords({ ...passwords, current: e.target.value })} placeholder="Current password" aria-label="Current password" />
        <input type="password" autoComplete="new-password" minLength={8} value={passwords.next} onChange={(e) => setPasswords({ ...passwords, next: e.target.value })} placeholder="New password (8+ characters)" aria-label="New password" />
        <button className="primary" type="submit" disabled={busy || !passwords.current || passwords.next.length < 8}>Change password</button>
      </form>
    </div>
  );
}

/** Desktop notifications for DMs, mentions and replies: browser permission plus an off switch. */
function Notifications() {
  const [permission, setPermission] = useState<NotifyPermission>(notifier.permission());
  const [muted, setMuted] = useState(notificationsMuted());
  const on = permission === "granted" && !muted;
  const turnOn = async () => {
    setNotificationsMuted(false);
    setMuted(false);
    if (permission !== "granted") setPermission(await notifier.request());
  };
  const turnOff = () => {
    setNotificationsMuted(true);
    setMuted(true);
  };
  return (
    <>
      <h2>Notifications</h2>
      <div className="row" data-testid="notification-settings">
        <span className="grow">
          {permission === "unsupported"
            ? "This app can't show desktop notifications yet."
            : permission === "denied"
              ? "Blocked: allow notifications for this site in the browser's settings."
              : on
                ? "On for direct messages, mentions and replies to you."
                : "Off."}
        </span>
        {permission !== "unsupported" && permission !== "denied" && (
          <button className={on ? "" : "primary"} onClick={() => void (on ? turnOff() : turnOn())}>
            {on ? "Turn off" : "Turn on"}
          </button>
        )}
      </div>
    </>
  );
}

/** Microphone and speakers for voice. */
function Devices({ choose }: { choose: (kind: DeviceKind, deviceId: string) => Promise<void> }) {
  const [devices, setDevices] = useState<Record<DeviceKind, MediaDeviceInfo[]>>({ audioinput: [], audiooutput: [] });
  const [prefs, setPrefs] = useState(loadDevicePrefs);
  const [needsPermission, setNeedsPermission] = useState(false);

  const refresh = async () => {
    const [inputs, outputs] = await Promise.all([listDevices("audioinput"), listDevices("audiooutput")]);
    setDevices({ audioinput: inputs, audiooutput: outputs });
    setNeedsPermission(inputs.length > 0 && inputs.every((d) => !d.label));
  };
  useEffect(() => {
    void refresh();
    navigator.mediaDevices?.addEventListener("devicechange", refresh);
    return () => navigator.mediaDevices?.removeEventListener("devicechange", refresh);
  }, []);

  // Device names are hidden until the page has had microphone access once.
  const allow = async () => {
    const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    stream.getTracks().forEach((t) => t.stop());
    await refresh();
  };

  const select = (kind: DeviceKind, label: string) => (
    <label className="device">
      {label}
      <select
        aria-label={label}
        value={prefs[kind] ?? "default"}
        onChange={(e) => {
          setPrefs({ ...prefs, [kind]: e.target.value });
          void choose(kind, e.target.value);
        }}
      >
        {devices[kind].map((d, i) => (
          <option key={d.deviceId || i} value={d.deviceId}>
            {d.label || `${label} ${i + 1}`}
          </option>
        ))}
      </select>
    </label>
  );

  return (
    <>
      <h2>Voice</h2>
      {devices.audioinput.length === 0 ? (
        <p className="muted">No microphone found.</p>
      ) : (
        <div className="column" data-testid="device-settings">
          {select("audioinput", "Microphone")}
          {canChooseSpeakers && devices.audiooutput.length > 0 && select("audiooutput", "Speakers")}
          {needsPermission && (
            <button className="link" onClick={() => void allow()}>
              Show device names (asks for microphone access)
            </button>
          )}
        </div>
      )}
    </>
  );
}
