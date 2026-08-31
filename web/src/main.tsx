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
        <App />
      </MsalProvider>
    </React.StrictMode>,
  );
}

void bootstrap();
