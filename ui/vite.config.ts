import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// Built files are served by WebView2 from the add-in's wwwroot folder via https://swagent.app/.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  base: "./",
  server: { port: 5173, strictPort: true },
  build: { outDir: "dist", emptyOutDir: true, target: "es2022", chunkSizeWarningLimit: 2000 },
});
