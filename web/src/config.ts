/**
 * Runtime config. In AWS, CDK writes /config.json next to the bundle with the Cognito ids,
 * so one build artifact works in every stage. Locally there is no config.json, so we fall
 * back to dev mode: no Cognito, and the API trusts X-Dev-User / X-Dev-Role headers.
 */
export interface AppConfig {
  apiBaseUrl: string;
  authority?: string;
  clientId?: string;
  cognitoDomain?: string;
  redirectUri?: string;
  stage?: string;
}

const defaults: AppConfig = { apiBaseUrl: '/api' };

export async function loadConfig(): Promise<AppConfig> {
  try {
    const res = await fetch('/config.json', { cache: 'no-store' });
    if (res.ok && res.headers.get('content-type')?.includes('json')) {
      return { ...defaults, ...(await res.json()) };
    }
  } catch {
    // fall through to dev defaults
  }
  return defaults;
}

export const usesCognito = (c: AppConfig) => Boolean(c.authority && c.clientId);
