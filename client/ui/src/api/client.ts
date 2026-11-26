import createClient from "openapi-fetch";
import type { components, paths } from "./schema";

export type Schemas = components["schemas"];
export type Auth = Schemas["AuthResponse"];

/**
 * Typed REST client for one Snacka server. Regenerate types with `pnpm gen:api`. Pass the
 * session's fetch to send requests signed in (see Session).
 */
export function createApi(serverUrl: string, fetchFn: (input: Request) => Promise<Response> = (r) => fetch(r)) {
  return createClient<paths>({ baseUrl: serverUrl, fetch: fetchFn });
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
