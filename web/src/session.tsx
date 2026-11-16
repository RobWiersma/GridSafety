import { createContext, useContext, useMemo, useState, type ReactNode } from 'react';
import { AuthProvider, useAuth } from 'react-oidc-context';
import type { AppConfig } from './config';
import { usesCognito } from './config';

export type DevRole = 'Inspector' | 'Supervisor' | 'Guest';

export interface Session {
  displayName: string;
  authHeaders(): Record<string, string>;
  signOut(): void;
  devMode: boolean;
  /** Signed out: read-only view through /api/public. */
  guest: boolean;
  signIn?(): void;
  devRole?: DevRole;
  setDevRole?(role: DevRole): void;
}

const SessionContext = createContext<Session | null>(null);

export function useSession(): Session {
  const s = useContext(SessionContext);
  if (!s) throw new Error('useSession must be used inside <SessionProvider>');
  return s;
}

export function SessionProvider({ config, children }: { config: AppConfig; children: ReactNode }) {
  if (!usesCognito(config)) return <DevSession>{children}</DevSession>;

  const redirectUri = config.redirectUri ?? `${window.location.origin}/`;
  return (
    <AuthProvider
      authority={config.authority!}
      client_id={config.clientId!}
      redirect_uri={redirectUri}
      scope="openid email profile"
      onSigninCallback={() => window.history.replaceState({}, document.title, window.location.pathname)}
    >
      <CognitoSession config={config} redirectUri={redirectUri}>
        {children}
      </CognitoSession>
    </AuthProvider>
  );
}

function CognitoSession({ config, redirectUri, children }: { config: AppConfig; redirectUri: string; children: ReactNode }) {
  const auth = useAuth();

  const session = useMemo<Session | null>(() => {
    if (!auth.isAuthenticated || !auth.user) return null;
    const user = auth.user;
    return {
      displayName: (user.profile.email as string | undefined) ?? 'Signed in',
      devMode: false,
      guest: false,
      // ID token: API Gateway's JWT authorizer validates it, and it carries email + cognito:groups.
      authHeaders: () => ({ Authorization: `Bearer ${user.id_token}` }),
      signOut: () => {
        void auth.removeUser();
        if (config.cognitoDomain) {
          const params = new URLSearchParams({ client_id: config.clientId!, logout_uri: redirectUri });
          window.location.href = `${config.cognitoDomain}/logout?${params}`;
        }
      },
    };
  }, [auth, config, redirectUri]);

  // Signed-out visitors browse read-only instead of hitting a sign-in wall.
  const guest = useMemo<Session>(() => ({
    displayName: 'Guest',
    devMode: false,
    guest: true,
    authHeaders: () => ({}),
    signIn: () => void auth.signinRedirect(),
    signOut: () => undefined,
  }), [auth]);

  if (auth.isLoading) return <div className="center-screen"><div className="spinner" aria-label="Loading" /></div>;
  if (auth.error) return <Landing error={auth.error.message} onSignIn={() => void auth.signinRedirect()} />;
  return <SessionContext.Provider value={session ?? guest}>{children}</SessionContext.Provider>;
}

const readLocal = (key: string, fallback: string) => {
  try {
    return localStorage.getItem(key) ?? fallback;
  } catch {
    return fallback;
  }
};

function DevSession({ children }: { children: ReactNode }) {
  const [role, setRole] = useState<DevRole>(() => (readLocal('gridsafety.devRole', 'Supervisor') as DevRole));
  const session = useMemo<Session>(
    () => ({
      displayName: role === 'Guest' ? 'Guest (dev)' : role === 'Supervisor' ? 'dana.supervisor (dev)' : 'alex.inspector (dev)',
      devMode: true,
      guest: role === 'Guest',
      devRole: role,
      setDevRole: (r) => {
        setRole(r);
        try {
          localStorage.setItem('gridsafety.devRole', r);
        } catch {
          /* ignore */
        }
      },
      authHeaders: (): Record<string, string> => role === 'Guest' ? {} : ({
        'X-Dev-User': role === 'Supervisor' ? 'dana.supervisor' : 'alex.inspector',
        'X-Dev-Role': role,
      }),
      signOut: () => alert('Dev mode: there is no sign-in. Deploy with CDK to use Cognito.'),
    }),
    [role],
  );
  return <SessionContext.Provider value={session}>{children}</SessionContext.Provider>;
}

function Landing({ onSignIn, error }: { onSignIn: () => void; error?: string }) {
  return (
    <main className="landing">
      <div className="landing-card">
        <Logo size={52} />
        <h1>GridSafety</h1>
        <p>Drone inspection operations for power lines: live flights, findings and SLA-driven triage.</p>
        {error && <p className="error-text">{error}</p>}
        <button className="btn btn-primary btn-lg" onClick={onSignIn}>Sign in</button>
      </div>
    </main>
  );
}

export function Logo({ size = 28 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 32 32" aria-hidden="true">
      <rect width="32" height="32" rx="8" fill="var(--accent)" />
      <path d="M17.5 5 9 18h6l-1.5 9L23 14h-6l.5-9z" fill="#fff" />
    </svg>
  );
}
