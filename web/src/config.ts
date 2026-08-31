/**
 * Build-time configuration.
 *
 * Every value is injected by the pipeline (see vite.config.ts) and none is committed.
 * They are all public identifiers: client IDs, a tenant ID, a gateway URL, and an APIM
 * subscription key.
 *
 * On the subscription key specifically, because it is the one that looks wrong: it is
 * compiled into this bundle and therefore readable by anyone. That is deliberate. An
 * APIM subscription key is a client identifier and a rate-limit handle, not a
 * credential — it says which client is calling, never who the caller is. The only
 * mutating endpoint, POST /orders, is protected by something that is not the key: an
 * Entra access token carrying Orders.Write, validated at the gateway.
 */
export const config = {
  gatewayUrl: import.meta.env.VITE_GATEWAY_URL as string,
  spaClientId: import.meta.env.VITE_SPA_CLIENT_ID as string,
  tenantId: import.meta.env.VITE_TENANT_ID as string,
  apiScope: import.meta.env.VITE_API_SCOPE as string,
  subscriptionKey: import.meta.env.VITE_SUBSCRIPTION_KEY as string,
} as const;
