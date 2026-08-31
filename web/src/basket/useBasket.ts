import { useCallback, useEffect, useState } from 'react';
import type { BasketLine } from '../api/client';

const STORAGE_KEY = 'pizzashop.basket';
const COUPON_KEY = 'pizzashop.couponCode';

/**
 * Mirrors of bounds the server owns. The server remains the authority — it rejects a
 * request that breaks either of these with a 400 whatever the client does — and these
 * exist so the UI cannot construct a request that is guaranteed to fail.
 *
 * Both were reachable from the page before they existed. The + button incremented without
 * limit, so a customer could reach 52 and get a 400 whose text was `ArgumentOutOfRange`'s
 * own "(Parameter 'lines') Actual value was 52.", and the coupon field accepted any
 * length, so a 51-character code produced a 400 from `CouponCodeRules.MaxLength`.
 *
 * They are copies, and a copy can drift. That is accepted here for the same reason the
 * server still validates: the copy governs what the UI lets you build, never what the
 * server accepts. If it drifts low the UI is merely stricter than it needs to be; if it
 * drifts high the server still refuses.
 *
 *   MAX_QUANTITY_PER_LINE  <- PizzaShop.Ordering.Basket.MaxQuantityPerLine
 *   COUPON_CODE_MAX_LENGTH <- PizzaShop.Coupons.CouponCodeRules.MaxLength
 *
 * `Basket.MaxLines` (50) has no mirror because the menu has eight pizzas and one line per
 * pizza, so the UI cannot reach it.
 */
export const MAX_QUANTITY_PER_LINE = 50;
export const COUPON_CODE_MAX_LENGTH = 50;

/**
 * The order in progress — basket and coupon code — persisted across the sign-in redirect.
 *
 * MSAL's redirect flow navigates away to login.microsoftonline.com and back, which
 * reloads the page and discards React state. Without persistence a customer would
 * authenticate in order to buy a basket and return to an empty one — the worst possible
 * moment to lose it.
 *
 * The coupon code is persisted for a sharper reason than the basket, and it was missed
 * on the first pass. An empty basket after sign-in is *visible*: the customer sees it and
 * rebuilds it. A lost coupon code is not. The basket came back, the code did not, and the
 * order was then submitted with couponCode: null — which the server prices at full price
 * with RejectionReason: null, because no coupon was asked about and nothing was rejected.
 * The confirmation only explains a missing discount when there IS a rejection reason, so
 * the customer was charged full price and told nothing at all.
 *
 * sessionStorage rather than localStorage: an order in progress is a per-tab, per-visit
 * thing and should not still be there tomorrow. It survives the redirect because
 * sessionStorage is scoped per origin per tab, and the tab outlives the navigation.
 *
 * It carries quantities and a code only. No price is ever stored client-side, so there is
 * nothing here that could be tampered with and sent back — rule 1 holds by construction
 * on this side too. The code is not a secret either: it is validated server-side on every
 * request, and the server recalculates from scratch at submission.
 */
export function useBasket() {
  const [lines, setLines] = useState<BasketLine[]>(() => {
    try {
      const stored = sessionStorage.getItem(STORAGE_KEY);
      if (!stored) return [];
      const parsed = JSON.parse(stored) as BasketLine[];
      return Array.isArray(parsed) ? parsed.filter((l) => l.quantity > 0) : [];
    } catch {
      // A corrupt or unreadable value must not take the whole app down.
      return [];
    }
  });

  const [couponCode, setCouponCodeState] = useState<string>(() => {
    try {
      return (sessionStorage.getItem(COUPON_KEY) ?? '').slice(0, COUPON_CODE_MAX_LENGTH);
    } catch {
      return '';
    }
  });

  // Truncated here as well as by the input's maxLength, because maxLength does not
  // constrain a paste on every browser and does not constrain sessionStorage at all.
  const setCouponCode = useCallback(
    (code: string) => setCouponCodeState(code.slice(0, COUPON_CODE_MAX_LENGTH)),
    [],
  );

  useEffect(() => {
    try {
      sessionStorage.setItem(STORAGE_KEY, JSON.stringify(lines));
    } catch {
      // Private browsing can refuse writes. Losing persistence is survivable; throwing
      // here would not be.
    }
  }, [lines]);

  useEffect(() => {
    try {
      sessionStorage.setItem(COUPON_KEY, couponCode);
    } catch {
      // As above.
    }
  }, [couponCode]);

  // Clamped here rather than at the call site so every caller gets it — the stepper is
  // the only one today, and the next one should not have to remember.
  const setQuantity = useCallback((pizzaId: number, quantity: number) => {
    const clamped = Math.min(Math.max(0, Math.trunc(quantity)), MAX_QUANTITY_PER_LINE);
    setLines((current) => {
      const others = current.filter((l) => l.pizzaId !== pizzaId);
      return clamped > 0 ? [...others, { pizzaId, quantity: clamped }] : others;
    });
  }, []);

  const quantityOf = useCallback(
    (pizzaId: number) => lines.find((l) => l.pizzaId === pizzaId)?.quantity ?? 0,
    [lines],
  );

  // Clears the whole order in progress, not just the lines. A coupon code left behind
  // after a completed order would be silently reapplied to the next one.
  const clear = useCallback(() => {
    setLines([]);
    setCouponCodeState('');
  }, []);

  const itemCount = lines.reduce((sum, l) => sum + l.quantity, 0);

  return { lines, setQuantity, quantityOf, clear, itemCount, couponCode, setCouponCode };
}
