import React from 'react';
import ReactDOM from 'react-dom/client';
import { MsalProvider } from '@azure/msal-react';
import { EventType, type AuthenticationResult } from '@azure/msal-browser';
import { msalInstance } from './auth/msalConfig';
import { App } from './App';
import './styles.css';

/**
 * Nothing renders until MSAL has finished processing a redirect.
 *
 * On the way back from login.microsoftonline.com the URL carries the authorization code
 * in its fragment. Rendering before handleRedirectPromise() resolves shows the customer a
 * signed-out UI for a moment, and any component that reads the account list sees none —
 * so the app would flash "Sign in to order" at somebody who has just signed in.
 */
async function bootstrap() {
  await msalInstance.initialize();

  const response = await msalInstance.handleRedirectPromise();

  // Non-null means this load IS the return leg of a sign-in redirect: MSAL found an
  // authorization response in the URL and consumed it. On an ordinary load it is null.
  //
  // App needs to know, because the restored coupon code has to be re-previewed on the
  // way back from sign-in and must NOT be previewed at any other time. Deriving that
  // from state — "there is a code and a basket" — is what produced the defect this
  // signal replaces: the condition is also true the moment somebody types the first
  // character of a code, so the app fired a preview for "P" and displayed
  // "Not applied: NotFound" underneath a perfectly valid coupon.
  const returnedFromRedirect = response !== null;

  if (response?.account) {
    msalInstance.setActiveAccount(response.account);
  } else if (!msalInstance.getActiveAccount() && msalInstance.getAllAccounts().length > 0) {
    msalInstance.setActiveAccount(msalInstance.getAllAccounts()[0]);
  }

  msalInstance.addEventCallback((event) => {
    if (event.eventType === EventType.LOGIN_SUCCESS && event.payload) {
      msalInstance.setActiveAccount((event.payload as AuthenticationResult).account);
    }
  });

  ReactDOM.createRoot(document.getElementById('root')!).render(
    <React.StrictMode>
      <MsalProvider instance={msalInstance}>
        <App returnedFromRedirect={returnedFromRedirect} />
      </MsalProvider>
    </React.StrictMode>,
  );
}

void bootstrap();
