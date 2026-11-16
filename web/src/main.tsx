import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { StrictMode, useEffect, useMemo, useRef, type ReactNode } from 'react';
import { createRoot } from 'react-dom/client';
import 'leaflet/dist/leaflet.css';
import { App } from './App';
import { createFetcher, FetcherContext } from './api';
import { loadConfig, type AppConfig } from './config';
import { SessionProvider, useSession } from './session';
import './styles.css';

const queryClient = new QueryClient({
  defaultOptions: { queries: { staleTime: 1_000, retry: 1, refetchOnWindowFocus: true } },
});

function ApiProvider({ config, children }: { config: AppConfig; children: ReactNode }) {
  const session = useSession();
  // Guests read through the public, read-only API.
  const base = session.guest ? `${config.apiBaseUrl}/public` : config.apiBaseUrl;
  const fetcher = useMemo(() => createFetcher(base, session.authHeaders), [base, session]);
  // Guest and signed-in responses differ (names, allowed actions), so never reuse one for the other.
  const wasGuest = useRef(session.guest);
  useEffect(() => {
    if (wasGuest.current !== session.guest) queryClient.clear();
    wasGuest.current = session.guest;
  }, [session.guest]);
  return <FetcherContext.Provider value={fetcher}>{children}</FetcherContext.Provider>;
}

void loadConfig().then((config) => {
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <QueryClientProvider client={queryClient}>
        <SessionProvider config={config}>
          <ApiProvider config={config}>
            <App />
          </ApiProvider>
        </SessionProvider>
      </QueryClientProvider>
    </StrictMode>,
  );
});
