import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { StrictMode, useMemo, type ReactNode } from 'react';
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
  const fetcher = useMemo(() => createFetcher(config.apiBaseUrl, session.authHeaders), [config, session]);
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
