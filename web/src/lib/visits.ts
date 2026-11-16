// Anonymous page-view beacon. The server keeps only coarse daily counters: no cookies, no stored IPs.

let endpoint: string | null = null;
let referrerSent = false;
let last = { key: '', at: 0 };

export function setVisitEndpoint(apiBaseUrl: string) {
  endpoint = `${apiBaseUrl}/public/visit`;
}

/** Only the referring site, and only when it's another site (the server reduces it to a host name). */
function externalReferrer() {
  try {
    const ref = document.referrer ? new URL(document.referrer) : null;
    return ref && ref.host !== location.host ? ref.origin : undefined;
  } catch {
    return undefined;
  }
}

/** Records one view of a page ('ops', 'findings', 'missions', 'mission', 'visitors'). Never throws. */
export function recordVisit(page: string, signedIn: boolean) {
  if (!endpoint || navigator.webdriver) return;
  const now = Date.now();
  if (last.key === page && now - last.at < 2000) return; // React StrictMode / quick re-renders
  last = { key: page, at: now };

  const referrer = referrerSent ? undefined : externalReferrer();
  referrerSent = true;
  fetch(endpoint, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ page, referrer, signedIn }),
    keepalive: true,
  }).catch(() => { /* analytics must never break the app */ });
}
