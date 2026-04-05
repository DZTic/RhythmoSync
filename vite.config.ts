import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  // Vite options tailored for Tauri development and only applied in `tauri dev` or `tauri build`
  // 1. prevent vite from obscuring rust errors
  clearScreen: false,
  // 2. tauri expects a fixed port, fail if that port is not available
  server: {
    port: 5173,
    strictPort: true,
    watch: {
      // 3. tell vite to ignore watching `src-tauri`
      ignored: ["**/src-tauri/**"],
    },
  },
  // 3. to make use of `TAURI_PLATFORM`, `TAURI_ARCH`, `TAURI_FAMILY`,
  // `TAURI_PLATFORM_VERSION`, `TAURI_PLATFORM_TYPE` and `TAURI_DEBUG`
  // env variables
  envPrefix: ['VITE_', 'TAURI_'],
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    // Tauri uses Chromium on Windows and WebKit on macOS and Linux
    target: process.env.TAURI_ENV_PLATFORM == 'windows' ? 'chrome105' : 'safari13',
    // don't minify for debug builds
    minify: !process.env.TAURI_ENV_DEBUG ? 'esbuild' : false,
    // produce sourcemaps for debug builds
    sourcemap: !!process.env.TAURI_ENV_DEBUG,
    // PERFORMANCE: Code splitting for better caching and lazy loading
    rollupOptions: {
      output: {
        manualChunks(id) {
          // PERFORMANCE: Code splitting for better caching and lazy loading
          if (id.includes('node_modules/konva') || id.includes('node_modules/react-konva')) {
            return 'konva';
          }
          if (id.includes('node_modules/wavesurfer')) {
            return 'wavesurfer';
          }
          if (id.includes('node_modules/mp4-muxer') || id.includes('node_modules/webm-muxer')) {
            return 'muxer';
          }
          if (id.includes('node_modules/react') || id.includes('node_modules/react-dom') || id.includes('node_modules/zustand') || id.includes('node_modules/scheduler')) {
            return 'vendor';
          }
        },
      },
    },
    // PERFORMANCE: Increase chunk size warning limit
    chunkSizeWarningLimit: 600,
  },
});