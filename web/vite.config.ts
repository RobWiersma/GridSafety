import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

declare const process: { env: Record<string, string | undefined> }; // Node globals without @types/node

export default defineConfig({
  plugins: [react()],
  build: {
    rolldownOptions: {
      output: {
        // Libraries in their own files: they rarely change, so browsers keep them cached across deploys.
        codeSplitting: {
          groups: [
            { name: 'react', test: /node_modules[\/](react|react-dom|scheduler)[\/]/ },
            { name: 'map', test: /node_modules[\/](leaflet|react-leaflet|@react-leaflet)[\/]/ },
            { name: 'auth', test: /node_modules[\/](oidc-client-ts|react-oidc-context|jwt-decode)[\/]/ },
            { name: 'query', test: /node_modules[\/]@tanstack[\/]/ },
          ],
        },
      },
    },
  },
  server: {
    port: 5173,
    // Same-origin /api in every environment: Vite proxies it locally, CloudFront routes it in AWS. ws: the API's
    // /api/live push socket. GRIDSAFETY_API points the proxy at an API on another port.
    proxy: { '/api': { target: process.env.GRIDSAFETY_API ?? 'http://localhost:5080', ws: true } },
  },
});
