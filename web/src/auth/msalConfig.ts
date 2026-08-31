import { PublicClientApplication, type Configuration, LogLevel } from '@azure/msal-browser';
import { config } from '../config';

/**
 * Authorization code with PKCE, redirect flow.
 *
 * Redirect rather than popup, decided rather than defaulted. Popups are blocked by
 * default under some enterprise policies and behave poorly on mobile Safari, and a
 * blocked popup looks like a sign-in button that does nothing. Redirect works
 * everywhere.
 *
 * The cost of redirect is that the page reloads, so anything in React state is gone by
 * the time the user comes back — which would mean authenticating to buy a basket and
 * returning to an empty one. The basket is therefore persisted in sessionStorage
 * (see ../basket/useBasket.ts). That is worth doing regardless: a refresh mid-order
 * should not lose the cart either.
 */
const msalConfiguration: Configuration = {
  auth: {
    clientId: config.spaClientId,
    authority: `https://login.microsoftonline.com/${config.tenantId}`,
    // Must match a redirect URI registered on coupon-spa, character for character.
    // Entra does exact string matching with no wildcards.
    redirectUri: window.location.origin,
    postLogoutRedirectUri: window.location.origin,
    navigateToLoginRequestUrl: true,
  },
  cache: {
    // sessionStorage, not localStorage: tokens live no longer than the tab. It also
    // survives the redirect to login.microsoftonline.com and back, because
    // sessionStorage is per-origin per-tab and the tab outlives the navigation.
    cacheLocation: 'sessionStorage',
    storeAuthStateInCookie: false,
  },
  system: {
    loggerOptions: {
      logLevel: LogLevel.Error,
      piiLoggingEnabled: false,
      loggerCallback: (level, message, containsPii) => {
        if (containsPii) return;
        if (level === LogLevel.Error) console.error('[msal]', message);
      },
    },
  },
};

export const msalInstance = new PublicClientApplication(msalConfiguration);

/** The scope that POST /orders requires. validate-jwt checks for it in `scp`. */
export const orderScopes = [config.apiScope];
