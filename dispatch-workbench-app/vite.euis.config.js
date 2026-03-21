import path from "node:path";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  define: {
    "process.env.NODE_ENV": JSON.stringify("production")
  },
  plugins: [react()],
  build: {
    outDir: "euis-dist",
    emptyOutDir: true,
    minify: false,
    cssCodeSplit: false,
    cssMinify: false,
    assetsInlineLimit: 0,
    target: "es2019",
    lib: {
      entry: path.resolve(__dirname, "src/euis-entry.jsx"),
      formats: ["system"],
      fileName: () => "dispatch-workbench-euis.js",
      cssFileName: "dispatch-workbench-euis"
    },
    rollupOptions: {
      external: ["react", "react-dom"],
      output: {
        assetFileNames: (assetInfo) => {
          if (assetInfo.name === "dispatch-workbench-euis.css" || assetInfo.name === "style.css") {
            return "dispatch-workbench-euis.css";
          }
          return assetInfo.name ?? "[name][extname]";
        }
      }
    }
  }
});
