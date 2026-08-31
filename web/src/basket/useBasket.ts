import { useCallback, useEffect, useState } from 'react';
import type { BasketLine } from '../api/client';

const STORAGE_KEY = 'pizzashop.basket';

/**
 * The basket, persisted across the sign-in redirect.
 *
 * MSAL's redirect flow navigates away to login.microsoftonline.com and back, which
 * reloads the page and discards React state. Without persistence a customer would
 * authenticate in order to buy a basket and return to an empty one — the worst possible
 * moment to lose it.
 *
 * sessionStorage rather than localStorage: a basket is a per-tab, per-visit thing and
 * should not still be there tomorrow. It survives the redirect because sessionStorage is
 * scoped per origin per tab, and the tab outlives the navigation.
 *
 * It carries quantities only. No price is ever stored client-side, so there is nothing
 * here that could be tampered with and sent back — rule 1 holds by construction on this
 * side too.
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

  useEffect(() => {
    try {
      sessionStorage.setItem(STORAGE_KEY, JSON.stringify(lines));
    } catch {
      // Private browsing can refuse writes. Losing persistence is survivable; throwing
      // here would not be.
    }
  }, [lines]);

  const setQuantity = useCallback((pizzaId: number, quantity: number) => {
    setLines((current) => {
      const others = current.filter((l) => l.pizzaId !== pizzaId);
      return quantity > 0 ? [...others, { pizzaId, quantity }] : others;
    });
  }, []);

  const quantityOf = useCallback(
    (pizzaId: number) => lines.find((l) => l.pizzaId === pizzaId)?.quantity ?? 0,
    [lines],
  );

  const clear = useCallback(() => setLines([]), []);

  const itemCount = lines.reduce((sum, l) => sum + l.quantity, 0);

  return { lines, setQuantity, quantityOf, clear, itemCount };
}
