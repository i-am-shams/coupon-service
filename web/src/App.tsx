import { useCallback, useEffect, useState } from 'react';
import { useMsal, useIsAuthenticated } from '@azure/msal-react';
import { InteractionRequiredAuthError } from '@azure/msal-browser';
import { api, ApiError, type MenuItem, type CouponValidationResponse, type OrderResponse } from './api/client';
import { useBasket } from './basket/useBasket';
import { orderScopes } from './auth/msalConfig';
import { config } from './config';
import { TokenClaims } from './components/TokenClaims';

const money = (n: number) => `€${n.toFixed(2)}`;

export function App() {
  const { instance, accounts } = useMsal();
  const isAuthenticated = useIsAuthenticated();
  const { lines, setQuantity, quantityOf, clear, itemCount } = useBasket();

  const [menu, setMenu] = useState<MenuItem[]>([]);
  const [menuError, setMenuError] = useState<string | null>(null);
  const [couponCode, setCouponCode] = useState('');
  const [preview, setPreview] = useState<CouponValidationResponse | null>(null);
  const [order, setOrder] = useState<OrderResponse | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [devToken, setDevToken] = useState<string | null>(null);

  // Anonymous. No MSAL interaction on load — nobody signs in to look at a menu.
  useEffect(() => {
    api.getMenu()
      .then(setMenu)
      .catch((e: ApiError) => setMenuError(`${e.message}${e.correlationId ? ` (correlation ${e.correlationId})` : ''}`));
  }, []);

  // A preview is a hint, not a promise. It consumes no redemption and the server
  // recalculates from scratch at submission, so the two can legitimately disagree.
  const checkCoupon = useCallback(async () => {
    if (lines.length === 0) return;
    setBusy(true);
    setError(null);
    try {
      setPreview(await api.validateCoupon(couponCode, lines));
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }, [couponCode, lines]);

  const placeOrder = useCallback(async () => {
    if (lines.length === 0) return;
    setBusy(true);
    setError(null);
    try {
      // Sign-in happens here and nowhere earlier: only this endpoint needs a token.
      // The basket is already in sessionStorage, so the redirect does not lose it.
      if (!isAuthenticated || accounts.length === 0) {
        await instance.loginRedirect({ scopes: orderScopes });
        return; // the page navigates away; nothing after this runs
      }

      let token: string;
      try {
        const result = await instance.acquireTokenSilent({ scopes: orderScopes, account: accounts[0] });
        token = result.accessToken;
      } catch (e) {
        // Silent acquisition fails once the refresh token needs user interaction —
        // consent changes, a revoked session, or simply time. Redirecting is the
        // correct recovery, not an error to show.
        if (e instanceof InteractionRequiredAuthError) {
          await instance.acquireTokenRedirect({ scopes: orderScopes, account: accounts[0] });
          return;
        }
        throw e;
      }

      if (import.meta.env.DEV) setDevToken(token);

      const placed = await api.placeOrder(couponCode || null, lines, token);
      setOrder(placed);
      setPreview(null);
      clear();
    } catch (e) {
      setError(
        e instanceof ApiError
          ? `${e.message}${e.correlationId ? ` (correlation ${e.correlationId})` : ''}`
          : String(e),
      );
    } finally {
      setBusy(false);
    }
  }, [accounts, clear, couponCode, instance, isAuthenticated, lines]);

  return (
    <main>
      <header>
        <h1>Pizza Shop</h1>
        {isAuthenticated ? (
          <span className="who">Signed in as {accounts[0]?.username}</span>
        ) : (
          <span className="who">Browsing anonymously</span>
        )}
      </header>

      {menuError && <p className="error">Could not load the menu: {menuError}</p>}

      <section>
        <h2>Menu</h2>
        <ul className="menu">
          {menu.map((item) => (
            <li key={item.id}>
              <div>
                <strong>{item.name}</strong>
                <p>{item.description}</p>
              </div>
              {/* The price shown is the server's. The client never computes or sends one. */}
              <span className="price">{money(item.price)}</span>
              <div className="stepper">
                <button onClick={() => setQuantity(item.id, Math.max(0, quantityOf(item.id) - 1))} aria-label={`Remove one ${item.name}`}>-</button>
                <span>{quantityOf(item.id)}</span>
                <button onClick={() => setQuantity(item.id, quantityOf(item.id) + 1)} aria-label={`Add one ${item.name}`}>+</button>
              </div>
            </li>
          ))}
        </ul>
      </section>

      <section>
        <h2>Coupon</h2>
        <div className="coupon">
          <input
            value={couponCode}
            onChange={(e) => setCouponCode(e.target.value.toUpperCase())}
            placeholder="PIZZA10"
            aria-label="Coupon code"
          />
          <button onClick={checkCoupon} disabled={busy || itemCount === 0}>Check</button>
        </div>

        {preview && (
          <div className={preview.isValid ? 'preview ok' : 'preview no'}>
            {preview.isValid ? (
              <p>{preview.description} — you save {money(preview.discountAmount)}</p>
            ) : (
              <p>Not applied: {preview.rejectionReason}</p>
            )}
            {/* Every figure below came from the server. */}
            <dl>
              <dt>Subtotal</dt><dd>{money(preview.subtotal)}</dd>
              <dt>Discount</dt><dd>{money(preview.discountAmount)}</dd>
              <dt>Total</dt><dd>{money(preview.total)}</dd>
            </dl>
          </div>
        )}
      </section>

      <section>
        <button className="primary" onClick={placeOrder} disabled={busy || itemCount === 0}>
          {isAuthenticated ? 'Place order' : 'Sign in to order'}
        </button>
        {error && <p className="error">{error}</p>}

        {order && (
          <div className="confirmation">
            <h3>Order #{order.orderId} placed</h3>
            <dl>
              <dt>Subtotal</dt><dd>{money(order.subtotal)}</dd>
              <dt>Discount</dt><dd>{money(order.discountAmount)}</dd>
              <dt>Total</dt><dd>{money(order.total)}</dd>
            </dl>
            {!order.couponApplied && order.rejectionReason && (
              <p>The coupon was not applied: {order.rejectionReason}. The order stands at full price.</p>
            )}
          </div>
        )}
      </section>

      {/* Replaced with `false` at build time, so this branch and the component behind it
          are absent from the production bundle. Verified by grepping the artifact. */}
      {import.meta.env.DEV && (
        <TokenClaims token={devToken} tenantId={config.tenantId} apiClientId={config.apiScope.split('/')[2] ?? ''} />
      )}
    </main>
  );
}
