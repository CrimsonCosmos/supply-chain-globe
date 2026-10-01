import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The production build lands in the ASP.NET Core app's wwwroot, so a single
// host serves both the UI and the API.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../src/SupplyChain.Api/wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 2500, // three.js + globe.gl are large by nature
  },
  server: { proxy: { '/api': 'http://localhost:5080' } },
});
