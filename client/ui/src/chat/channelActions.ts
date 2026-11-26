import type { Dispatch, SetStateAction } from "react";
import { ok, type Api } from "../api/client";
import type { MessageActions } from "../MessageList";
import type { FileLoader } from "./files";
import type { PreviewLoader } from "./previews";
import { applyPin, applyReaction, fromChannel, replace, type ChatMessage } from "./model";

/**
 * What the user can do to messages in a channel, applied to one message list: the channel view
 * and the thread pane each keep their own list. Other people's changes arrive as hub events.
 */
export function channelActions(o: {
  api: Api;
  channelId: string;
  myId: string;
  myUsername: string;
  canModerate: boolean;
  loadFile: FileLoader;
  loadPreview?: PreviewLoader;
  run: (f: () => Promise<void>) => Promise<void>;
  setList: Dispatch<SetStateAction<ChatMessage[]>>;
  onReply?: (m: ChatMessage) => void;
  onOpenThread?: (m: ChatMessage) => void;
}): MessageActions {
  const { api, channelId, myId, run, setList } = o;
  const path = (messageId: string) => ({ params: { path: { channelId, messageId } } });
  return {
    myId,
    myUsername: o.myUsername,
    loadFile: o.loadFile,
    loadPreview: o.loadPreview,
    canModerate: o.canModerate,
    onReply: o.onReply,
    onOpenThread: o.onOpenThread,
    onEdit: (id, content) =>
      void run(async () => {
        const m = ok(await api.PUT("/api/channels/{channelId}/messages/{messageId}", { ...path(id), body: { content } }));
        setList((prev) => replace(prev, fromChannel(m)));
      }),
    onDelete: (id) =>
      void run(async () => {
        const res = await api.DELETE("/api/channels/{channelId}/messages/{messageId}", path(id));
        if (!res.response.ok) throw new Error(`Delete failed: ${res.response.status}`);
        setList((prev) => prev.filter((m) => m.id !== id));
      }),
    onReact: (id, emoji, add) =>
      void run(async () => {
        if (add) {
          const e = ok(await api.POST("/api/channels/{channelId}/messages/{messageId}/reactions", { ...path(id), body: { emoji } }));
          setList((prev) => applyReaction(prev, e, myId));
        } else {
          // 204: the new count arrives as a ReactionUpdated event.
          const res = await api.DELETE("/api/channels/{channelId}/messages/{messageId}/reactions/{emoji}", { params: { path: { channelId, messageId: id, emoji } } });
          if (!res.response.ok) throw new Error(`Removing reaction failed: ${res.response.status}`);
        }
      }),
    onPin: (id, pin) =>
      void run(async () => {
        const e = ok(pin ? await api.POST("/api/channels/{channelId}/messages/{messageId}/pin", path(id)) : await api.DELETE("/api/channels/{channelId}/messages/{messageId}/pin", path(id)));
        setList((prev) => applyPin(prev, e));
      }),
  };
}
