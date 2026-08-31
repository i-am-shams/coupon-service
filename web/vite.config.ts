import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

// Every value the app needs at runtime is injected here, never committed. Vite only
// exposes variables prefixed VITE_ to the bundle, which makes the boundary explicit:
// anything without the prefix cannot leak into client code by accident.
//
// All five are public by nature — client IDs, a tenant ID, a gateway URL, and an APIM
// subscription key. The key is discussed at length in docs/decisions.md; it is a client
// identifier and a rate-limit handle, not a credential.
const REQUIRED = [
  'VITE_GATEWAY_URL',
  'VITE_SPA_CLIENT_ID',
  'VITE_TENANT_ID',
  'VITE_API_SCOPE',
  'VITE_SUBSCRIPTION_KEY',
];

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), 'VITE_');

  // Fail the BUILD, not the browser. A missing variable becomes `undefined` in the
  // bundle and surfaces as a sign-in that silently does nothing, or a 401 that reads
  // like a gateway problem. Better to stop the pipeline with the variable's name.
  if (mode === 'production') {
    const missing = REQUIRED.filter((k) => !env[k]);
    if (missing.length > 0) {
      throw new Error(
        `Missing required build variables: ${missing.join(', ')}. ` +
          'These are injected by the pipeline from Bicep outputs and pipeline variables. ' +
          'See web/.env.example.',
      );
    }
  }

  return {
    plugins: [react()],
    server: {
      // Must match the redirect URI registered on coupon-spa. Entra does exact string
      // matching, so a different local port fails sign-in with AADSTS50011.
      port: 5173,
      strictPort: true,
    },
    build: {
      outDir: 'dist',
      sourcemap: false,
    },
  };
});
