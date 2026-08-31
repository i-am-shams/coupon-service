import { useCallback, useEffect, useRef, useState } from 'react';
import { useMsal, useIsAuthenticated } from '@azure/msal-react';
import { InteractionRequiredAuthError } from '@azure/msal-browser';
import { api, ApiError, type MenuItem, type CouponValidationResponse, type OrderResponse } from './api/client';
import { useBasket, MAX_QUANTITY_PER_LINE, COUPON_CODE_MAX_LENGTH } from './basket/useBasket';
import { orderScopes } from './auth/msalConfig';
import { config } from './config';
import { TokenClaims } from './components/TokenClaims';

const money = (n: number) => `€${n.toFixed(2)}`;

/**
 * What to show a customer when a call fails.
 *
 * An `ApiError` came back from the gateway or the backend and its message was written to
 * be read — a `ProblemDetails.detail`, or the gateway's own text. The correlation ID goes
 * with it so a bug report can quote something the logs can be searched for.
 *
 * Anything else never reached the gateway at all: a dropped connection, DNS, an offline
 * browser. `String(e)` for those is "TypeError: Failed to fetch", which is a JavaScript
 * internal and tells a customer nothing — the same objection as the
 * "(Parameter 'lines') Actual value was 52." the API used to return.
 */
const describeFailure = (e: unknown) =>
  e instanceof ApiError
    ? `${e.message}${e.correlationId ? ` (correlation ${e.correlationId})` : ''}`
    : 'Could not reach the service. Check your connection and try again.';

export interface AppProps {
  /**
   * True only when this page load is the return leg of a sign-in redirect — see
   * main.tsx, which gets it from `handleRedirectPromise()` rather than inferring it.
   */
  returnedFromRedirect: boolean;
}

export function App({ returnedFromRedirect }: AppProps) {
  const { instance, accounts } = useMsal();
  const isAuthenticated = useIsAuthenticated();
  const { lines, setQuantity, quantityOf, clear, itemCount, couponCode, setCouponCode } = useBasket();

  const [menu, setMenu] = useState<MenuItem[]>([]);
  const [menuError, setMenuError] = useState<string | null>(null);
  const [menuLoading, setMenuLoading] = useState(true);
  const [preview, setPreview] = useState<CouponValidationResponse | null>(null);
  const [summary, setSummary] = useState<CouponValidationResponse | null>(null);
  const [summaryFailed, setSummaryFailed] = useState(false);
  const [order, setOrder] = useState<OrderResponse | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [devToken, setDevToken] = useState<string | null>(null);

  // Anonymous. No MSAL interaction on load — nobody signs in to look at a menu.
  //
  // It retries, and that is the point rather than a refinement. The backend is a B1 App
  // Service whose first boot after a deployment was measured at 212 seconds: about 110s
  // of CA certificate rehashing before .NET starts at all, then EF migrations and seeding
  // against a 5 DTU database. A single attempt on mount lands inside that window, sets an
  // error, and then sits there — the customer sees a broken shop and the only way out is
  // a manual refresh they have no reason to think of.
  //
  // The pipeline's smoke test already polls for up to 420 seconds for exactly this
  // reason. The page a customer actually looks at was the one place that did not.
  const loadMenu = useCallback(async () => {
    setMenuLoading(true);
    setMenuError(null);

    // ~55 seconds total. Long enough to cover a warm start and most of a cold one;
    // short enough that a genuinely broken backend does not hold someone indefinitely.
    // After that it hands over to a button rather than telling them to refresh.
    const backoffMs = [1000, 2000, 4000, 8000, 8000, 8000, 8000, 8000, 8000];

    for (let attempt = 0; ; attempt++) {
      try {
        setMenu(await api.getMenu());
        setMenuError(null);
        setMenuLoading(false);
        return;
      } catch (e) {
        if (attempt >= backoffMs.length) {
          const err = e as ApiError;
          setMenuError(
            `${err.message}${err.correlationId ? ` (correlation ${err.correlationId})` : ''}`,
          );
          setMenuLoading(false);
          return;
        }
        await new Promise((resolve) => setTimeout(resolve, backoffMs[attempt]));
      }
    }
  }, []);

  useEffect(() => {
    void loadMenu();
  }, [loadMenu]);

  // ── The running total ──────────────────────────────────────────────────────
  //
  // The brief names "view the calculated total price" as one of four things the customer
  // must be able to do, and until this existed the word "total" did not appear on the
  // page unless you pressed Check — so the only way to see what your basket cost was to
  // ask a question about a coupon you might not have.
  //
  // The figures come from the server, like every other money figure in this app. The
  // client holds quantities and never multiplies anything by a price: rule 1 holds on
  // this side by there being no arithmetic here to get wrong, not by convention.
  // `POST /coupons/validate` with no code is exactly "price this basket", and it is
  // read-only by rule 2 — it consumes no redemption and writes nothing — so calling it
  // whenever the basket changes is free of consequence.
  const quoteSeq = useRef(0);

  useEffect(() => {
    if (lines.length === 0) {
      setSummary(null);
      setSummaryFailed(false);
      return;
    }

    // Sequenced as well as debounced. The debounce collapses a burst of stepper clicks;
    // the sequence number stops a slow earlier response overwriting a newer one, which
    // would leave the customer looking at the total for a basket they have moved on from.
    const seq = ++quoteSeq.current;

    const timer = setTimeout(() => {
      void (async () => {
        try {
          const quote = await api.validateCoupon('', lines);
          if (seq !== quoteSeq.current) return;
          setSummary(quote);
          setSummaryFailed(false);
        } catch {
          if (seq !== quoteSeq.current) return;
          // Deliberately not raised as a page error. The menu's own retry already covers
          // a backend that is down or still warming, and a red banner here would fire on
          // every keystroke-speed basket edit during a cold start.
          setSummaryFailed(true);
        }
      })();
    }, 350);

    return () => clearTimeout(timer);
  }, [lines]);

  // A coupon verdict belongs to the basket it was checked against. Changing the basket
  // makes it stale, and a stale discount displayed beside a fresh subtotal is the same
  // class of failure as the coupon code that went missing across the redirect: the
  // customer is shown a number that is not the number they will be charged.
  useEffect(() => {
    setPreview(null);
  }, [lines]);

  // A preview is a hint, not a promise. It consumes no redemption and the server
  // recalculates from scratch at submission, so the two can legitimately disagree.
  const checkCoupon = useCallback(async () => {
    if (lines.length === 0) return;

    const code = couponCode.trim();

    // An empty field is not a coupon, so there is nothing to check and nothing to
    // reject. The server answers `NotFound`, which is the correct answer to "is '' a
    // valid coupon?" and the wrong thing to put in front of somebody who only wanted
    // their total — it rendered a red rejection panel under an empty input. The total
    // is already on screen from the summary above; this just clears any stale verdict.
    if (code === '') {
      setPreview(null);
      setError(null);
      return;
    }

    setBusy(true);
    setError(null);
    try {
      setPreview(await api.validateCoupon(code, lines));
    } catch (e) {
      // Cleared rather than left on screen. A failed check used to leave the previous
      // verdict and its figures visible next to the new error, so the numbers being
      // shown were for a basket or a code that was no longer the one in the form.
      setPreview(null);
      setError(describeFailure(e));
    } finally {
      setBusy(false);
    }
  }, [couponCode, lines]);

  // Re-run the preview once when a coupon code comes back from sessionStorage after the
  // sign-in redirect. Restoring the code into the input is necessary but not sufficient:
  // without this the customer returns to a filled-in coupon field and no preview beside
  // it, which reads as "the coupon is no longer being applied" at the exact moment they
  // are deciding whether to commit.
  //
  // `returnedFromRedirect` is what makes this fire on the return leg and nowhere else.
  // The previous version had no such signal and tested the state instead — menu loaded,
  // a code present, a non-empty basket — which is also true the instant somebody types
  // the first character of a code. So it fired a preview for "P", got NotFound, and
  // displayed "Not applied: NotFound" underneath a coupon the customer was still typing.
  // The ref then guaranteed it never corrected itself.
  //
  // Safe to do automatically because the preview is read-only. It waits for the menu so
  // it cannot race the initial load.
  const restoredPreviewRan = useRef(false);
  useEffect(() => {
    if (!returnedFromRedirect) return;
    if (restoredPreviewRan.current) return;
    if (menu.length === 0 || !couponCode || lines.length === 0) return;
    restoredPreviewRan.current = true;
    void checkCoupon();
  }, [returnedFromRedirect, menu, couponCode, lines, checkCoupon]);

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

      const placed = await api.placeOrder(couponCode.trim() || null, lines, token);
      setOrder(placed);
      setPreview(null);
      clear();
    } catch (e) {
      setError(describeFailure(e));
    } finally {
      setBusy(false);
    }
  }, [accounts, clear, couponCode, instance, isAuthenticated, lines]);

  // The preview's figures when a coupon has been checked against this basket, the
  // plain summary's otherwise. They cannot disagree: changing the basket clears the
  // preview, so whichever of the two is showing was priced for what is on screen now.
  const figures = preview ?? summary;

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

      {menuLoading && menu.length === 0 && (
        <p className="hint">
          Loading the menu… if the service has been idle this can take up to a minute while it
          starts up.
        </p>
      )}

      {menuError && (
        <p className="error">
          Could not load the menu: {menuError}{' '}
          <button onClick={() => void loadMenu()}>Try again</button>
        </p>
      )}

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
                <button onClick={() => setQuantity(item.id, quantityOf(item.id) - 1)} disabled={quantityOf(item.id) === 0} aria-label={`Remove one ${item.name}`}>-</button>
                <span>{quantityOf(item.id)}</span>
                {/* Capped at the server's own limit. useBasket clamps too, so the cap
                    holds even if this button is reached some other way; disabling it is
                    what makes the limit visible rather than a request that comes back 400. */}
                <button onClick={() => setQuantity(item.id, quantityOf(item.id) + 1)} disabled={quantityOf(item.id) >= MAX_QUANTITY_PER_LINE} aria-label={`Add one ${item.name}`}>+</button>
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
            maxLength={COUPON_CODE_MAX_LENGTH}
            aria-label="Coupon code"
          />
          <button onClick={checkCoupon} disabled={busy || itemCount === 0}>Check</button>
        </div>
      </section>

      {lines.length > 0 && (
        <section>
          <h2>Your basket</h2>
          <div className="basket">
            {preview && (
              <p className={preview.isValid ? 'applied' : 'not-applied'}>
                {preview.isValid
                  ? `${preview.description} — you save ${money(preview.discountAmount)}`
                  : `Coupon not applied: ${preview.rejectionReason}`}
              </p>
            )}
            {/* Every figure below came from the server. */}
            {figures ? (
              <dl>
                <dt>Subtotal</dt><dd>{money(figures.subtotal)}</dd>
                <dt>Discount</dt><dd>{money(figures.discountAmount)}</dd>
                <dt>Total</dt><dd>{money(figures.total)}</dd>
              </dl>
            ) : summaryFailed ? (
              <p className="hint">Could not reach the server for a total — it will try again when you change the basket.</p>
            ) : (
              <p className="hint">Working out your total…</p>
            )}
          </div>
        </section>
      )}

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
