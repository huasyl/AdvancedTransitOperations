import fs from "node:fs";
import path from "node:path";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

function copyWorkbenchHtml() {
  return {
    name: "copy-workbench-html",
    closeBundle() {
      const sourcePath = path.resolve(__dirname, "workbench.html");
      const targetPath = path.resolve(__dirname, "workbench-dist", "workbench.html");
      fs.copyFileSync(sourcePath, targetPath);
    }
  };
}

export default defineConfig({
  define: {
    "process.env.NODE_ENV": JSON.stringify("production")
  },
  plugins: [react(), copyWorkbenchHtml()],
  build: {
    outDir: "workbench-dist",
    emptyOutDir: true,
    minify: false,
    cssCodeSplit: false,
    cssMinify: false,
    assetsInlineLimit: 0,
    target: "es2019",
    lib: {
      entry: path.resolve(__dirname, "src/workbench-entry.jsx"),
      name: "RTDispatchWorkbenchNativeSchedule",
      formats: ["iife"],
      fileName: () => "workbench.js",
      cssFileName: "workbench"
    },
    rollupOptions: {
      output: {
        assetFileNames: (assetInfo) => {
          if (assetInfo.name === "workbench.css" || assetInfo.name === "style.css") {
            return "workbench.css";
          }
          return assetInfo.name ?? "[name][extname]";
        }
      }
    }
  }
});
