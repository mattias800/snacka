import { useCallback, useEffect, useRef, useState } from "react";
import { Room, RoomEvent, Track, type Participant, type RemoteTrack } from "livekit-client";
import { ok, type Api } from "./api/client";
import { loadDevicePrefs, saveDevicePref, type DeviceKind } from "./devices";

/**
 * Voice channel via LiveKit (#52), using the LiveKit JS SDK. The browser client always uses
 * this; the desktop/Android apps will move to the Rust core's LiveKit SDK (ADR 0001) behind
 * the same component.
 */

type VoiceMember = { identity: string; name: string; speaking: boolean; muted: boolean; local: boolean };

function snapshot(room: Room): VoiceMember[] {
  const all: Participant[] = [room.localParticipant, ...room.remoteParticipants.values()];
  return all.map((p) => ({
    identity: p.identity,
    name: p.name || p.identity,
    speaking: p.isSpeaking,
    muted: !p.isMicrophoneEnabled,
    local: p === room.localParticipant,
  }));
}

export function useVoice(api: Api) {
  const roomRef = useRef<Room | null>(null);
  const audioRef = useRef<HTMLDivElement | null>(null);
  const [channelId, setChannelId] = useState<string | null>(null);
  const [members, setMembers] = useState<VoiceMember[]>([]);
  const [status, setStatus] = useState<"idle" | "connecting" | "connected" | "error">("idle");
  const [error, setError] = useState<string | null>(null);

  const leave = useCallback(async () => {
    const room = roomRef.current;
    roomRef.current = null;
    setChannelId(null);
    setMembers([]);
    setStatus("idle");
    if (room) await room.disconnect();
  }, []);

  const join = useCallback(
    async (id: string) => {
      await leave();
      setError(null);
      setStatus("connecting");
      setChannelId(id);
      try {
        const { url, token } = ok(await api.POST("/api/voice/channels/{channelId}/token", { params: { path: { channelId: id } } }));
        const prefs = loadDevicePrefs();
        const room = new Room({
          adaptiveStream: true,
          dynacast: true,
          audioCaptureDefaults: { echoCancellation: true, noiseSuppression: true, autoGainControl: true, deviceId: prefs.audioinput },
          audioOutput: prefs.audiooutput ? { deviceId: prefs.audiooutput } : undefined,
        });
        roomRef.current = room;
        // Dev/e2e diagnostics only (stripped from production builds).
        if (import.meta.env.DEV) (window as unknown as { __snackaRoom?: Room }).__snackaRoom = room;
        const refresh = () => roomRef.current === room && setMembers(snapshot(room));
        room
          .on(RoomEvent.ParticipantConnected, refresh)
          .on(RoomEvent.ParticipantDisconnected, refresh)
          .on(RoomEvent.ActiveSpeakersChanged, refresh)
          .on(RoomEvent.TrackMuted, refresh)
          .on(RoomEvent.TrackUnmuted, refresh)
          .on(RoomEvent.LocalTrackPublished, refresh)
          .on(RoomEvent.TrackSubscribed, (track: RemoteTrack) => {
            if (track.kind === Track.Kind.Audio) audioRef.current?.appendChild(track.attach());
            refresh();
          })
          .on(RoomEvent.TrackUnsubscribed, (track: RemoteTrack) => track.detach().forEach((el) => el.remove()))
          .on(RoomEvent.Disconnected, () => roomRef.current === room && void leave());
        await room.connect(url, token);
        await room.localParticipant.setMicrophoneEnabled(true);
        setStatus("connected");
        refresh();
      } catch (e) {
        setStatus("error");
        setError((e as Error).message);
        roomRef.current?.disconnect();
        roomRef.current = null;
      }
    },
    [api, leave],
  );

  const toggleMute = useCallback(async () => {
    const room = roomRef.current;
    if (!room) return;
    await room.localParticipant.setMicrophoneEnabled(!room.localParticipant.isMicrophoneEnabled);
    setMembers(snapshot(room));
  }, []);

  /** Remembers the choice and, in a call, switches to it right away. */
  const chooseDevice = useCallback(async (kind: DeviceKind, deviceId: string) => {
    saveDevicePref(kind, deviceId);
    await roomRef.current?.switchActiveDevice(kind, deviceId);
  }, []);

  useEffect(() => () => void roomRef.current?.disconnect(), []);

  return { channelId, members, status, error, join, leave, toggleMute, chooseDevice, audioRef };
}

export type Voice = ReturnType<typeof useVoice>;

/** Shown under a voice channel while connected: who's there, who's speaking, mute/leave. */
export function VoicePanel({ voice }: { voice: Voice }) {
  const me = voice.members.find((m) => m.local);
  return (
    <div className="voice-panel" data-testid="voice-panel" data-status={voice.status}>
      <div ref={voice.audioRef} hidden />
      {voice.status === "connecting" && <p className="muted">Connecting…</p>}
      {voice.error && <p className="error">{voice.error}</p>}
      <ul>
        {voice.members.map((m) => (
          <li key={m.identity} className={m.speaking ? "speaking" : ""} data-testid="voice-member" data-speaking={m.speaking} data-name={m.name}>
            <span className="avatar">{m.name.slice(0, 1).toUpperCase()}</span>
            {m.name}
            {m.muted && <span title="Muted"> 🔇</span>}
          </li>
        ))}
      </ul>
      {voice.status === "connected" && (
        <div className="voice-controls">
          <button onClick={() => void voice.toggleMute()}>{me?.muted ? "Unmute" : "Mute"}</button>
          <button className="danger" onClick={() => void voice.leave()}>Leave</button>
        </div>
      )}
    </div>
  );
}
