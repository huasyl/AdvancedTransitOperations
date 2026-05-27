import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  build: {
    rollupOptions: {
      input: "transit-diagram.html"
    }
  },
  server: {
    host: "127.0.0.1",
    port: 5175
  },
  define: {
    "process.env.NODE_ENV": JSON.stringify("development")
  }
});
