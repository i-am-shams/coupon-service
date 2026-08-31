/**
 * DEVELOPMENT ONLY. This component must never reach a deployed bundle.
 *
 * It renders the decoded claims of a live access token. On a public site that is a
 * screenshot waiting to happen, so it is not merely hidden behind a runtime flag — the
 * single call site is guarded by `import.meta.env.DEV`, which Vite replaces with the
 * literal `false` when building for production. The branch is then dead code, Rollup
 * drops it, and this module has no other importer, so it is tree-shaken out entirely.
 *
 * The marker string below exists to prove that. The pipeline greps the built bundle for
 * it and fails if it is present, so "it should be stripped" is verified rather than
 * assumed.
 *
 * Why it exists at all: phase D pinned validate-jwt to values spike 3 observed on a real
 * token — a bare client-ID audience, the v2 issuer with no trailing slash, Orders.Write
 * in scp, roles absent. Every automated assertion so far is a negative, and a negative
 * passes identically whether the policy is right or reading the wrong token. Checking the
 * claims BEFORE trusting a 201 is what tells those two apart. The token is decoded here,
 * in the browser, rather than pasted into jwt.ms — the same discipline spike 3 used,
 * because that would mean handing a live access token to a third party.
 */

export const DEV_ONLY_MARKER = 'PIZZASHOP_DEV_ONLY_TOKEN_CLAIMS_PANEL';

interface Claims {
  aud?: string;
  iss?: string;
  scp?: string;
  roles?: string[];
  ver?: string;
  exp?: number;
  oid?: string;
  preferred_username?: string;
}

function decodePayload(token: string): Claims | null {
  try {
    const payload = token.split('.')[1];
    if (!payload) return null;
    const base64 = payload.replace(/-/g, '+').replace(/_/g, '/');
    const padded = base64.padEnd(base64.length + ((4 - (base64.length % 4)) % 4), '=');
    return JSON.parse(atob(padded)) as Claims;
  } catch {
    return null;
  }
}

/** What phase D's policy pins. Anything not matching means the policy was never the problem. */
function expectations(claims: Claims, tenantId: string, apiClientId: string) {
  const expectedIssuer = `https://login.microsoftonline.com/${tenantId}/v2.0`;
  return [
    { label: 'aud is the bare client ID', ok: claims.aud === apiClientId, actual: claims.aud },
    { label: 'iss is the v2 issuer, no trailing slash', ok: claims.iss === expectedIssuer, actual: claims.iss },
    { label: 'scp contains Orders.Write', ok: (claims.scp ?? '').split(' ').includes('Orders.Write'), actual: claims.scp },
    { label: 'roles absent (delegated flow)', ok: claims.roles === undefined, actual: claims.roles ? claims.roles.join(',') : '(absent)' },
    { label: 'ver is 2.0', ok: claims.ver === '2.0', actual: claims.ver },
  ];
}

export function TokenClaims({
  token,
  tenantId,
  apiClientId,
}: {
  token: string | null;
  tenantId: string;
  apiClientId: string;
}) {
  if (!token) return null;
  const claims = decodePayload(token);
  if (!claims) return <div className="claims">Could not decode the token.</div>;

  const checks = expectations(claims, tenantId, apiClientId);
  const expiresAt = claims.exp ? new Date(claims.exp * 1000).toISOString() : 'unknown';

  return (
    <div className="claims" data-marker={DEV_ONLY_MARKER}>
      <h3>Token claims (development only)</h3>
      <table>
        <tbody>
          {checks.map((c) => (
            <tr key={c.label}>
              <td>{c.ok ? 'PASS' : 'FAIL'}</td>
              <td>{c.label}</td>
              <td><code>{String(c.actual ?? '(absent)')}</code></td>
            </tr>
          ))}
        </tbody>
      </table>
      <p>
        Expires {expiresAt}. Observed lifetime is 60–90 minutes, randomised — a call that
        worked and now returns 401 is most likely an expired token.
      </p>
    </div>
  );
}
