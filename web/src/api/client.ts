import { config } from '../config';

/**
 * Calls to the gateway. Never to the App Service directly — it is unreachable except
 * through API Management, by design.
 *
 * Rule 1 lives here as much as on the server: a request body carries pizzaId and
 * quantity, never a price, subtotal or total. Every money figure the UI displays comes
 * back from a server response.
 */

export interface MenuItem {
  id: number;
  name: string;
  description: string;
  price: number;
}

export interface BasketLine {
  pizzaId: number;
  quantity: number;
}

export interface CouponValidationResponse {
  couponCode: string | null;
  isValid: boolean;
  rejectionReason: string | null;
  subtotal: number;
  discountAmount: number;
  total: number;
  description: string | null;
}

export interface OrderResponse {
  orderId: number;
  subtotal: number;
  discountAmount: number;
  total: number;
  couponApplied: boolean;
  couponCode: string | null;
  rejectionReason: string | null;
}

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    /** Returned by the gateway on every response, including failures. Quote it in a bug report. */
    readonly correlationId: string | null,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

async function request<T>(path: string, init: RequestInit = {}, accessToken?: string): Promise<T> {
  const headers = new Headers(init.headers);
  headers.set('Ocp-Apim-Subscription-Key', config.subscriptionKey);
  if (init.body) headers.set('Content-Type', 'application/json');

  // Only sent when the caller has one. The menu and coupon preview are anonymous, and
  // attaching a token to them would be pointless rather than harmful.
  if (accessToken) headers.set('Authorization', `Bearer ${accessToken}`);

  const response = await fetch(`${config.gatewayUrl}${path}`, { ...init, headers });
  const correlationId = response.headers.get('x-correlation-id');

  if (!response.ok) {
    // The gateway's own errors are text; the backend's are RFC 7807 JSON. Try the
    // structured form first and fall back, so the message shown is the real one rather
    // than "Unexpected token in JSON".
    let detail = `Request failed with ${response.status}`;
    const raw = await response.text();
    try {
      const problem = JSON.parse(raw) as { title?: string; detail?: string; message?: string };
      detail = problem.detail ?? problem.title ?? problem.message ?? raw ?? detail;
    } catch {
      if (raw) detail = raw;
    }
    throw new ApiError(detail, response.status, correlationId);
  }

  return (await response.json()) as T;
}

export const api = {
  getMenu: () => request<MenuItem[]>('/api/v1/menu'),

  validateCoupon: (couponCode: string, items: BasketLine[]) =>
    request<CouponValidationResponse>('/api/v1/coupons/validate', {
      method: 'POST',
      body: JSON.stringify({ couponCode, items }),
    }),

  placeOrder: (couponCode: string | null, items: BasketLine[], accessToken: string) =>
    request<OrderResponse>(
      '/api/v1/orders',
      { method: 'POST', body: JSON.stringify({ couponCode, items }) },
      accessToken,
    ),
};
