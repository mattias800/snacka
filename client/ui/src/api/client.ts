import createClient, { type Middleware } from "openapi-fetch";
import type { components, paths } from "./schema";

export type Schemas = components["schemas"];
export type Auth = Schemas["AuthResponse"];

/** Typed REST client for one Snacka server. Regenerate types with `pnpm gen:api`. */
export function createApi(serverUrl: string, getToken: () => string | null) {
  const api = createClient<paths>({ baseUrl: serverUrl });
  const auth: Middleware = {
    onRequest({ request }) {
      const token = getToken();
      if (token) request.headers.set("Authorization", `Bearer ${token}`);
      return request;
    },
  };
  api.use(auth);
  return api;
}

export type Api = ReturnType<typeof createApi>;

/** Unwraps an openapi-fetch result, throwing a readable error on failure. */
export function ok<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (result.error !== undefined || result.data === undefined) {
    const detail =
      typeof result.error === "object" && result.error !== null && "error" in result.error
        ? String((result.error as { error: unknown }).error)
        : `${result.response.status} ${result.response.statusText}`;
    throw new Error(detail);
  }
  return result.data;
}
