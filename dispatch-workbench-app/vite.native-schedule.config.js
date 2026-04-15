import fs from "node:fs";
import path from "node:path";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

function copyNativeScheduleHtml() {
  return {
    name: "copy-native-schedule-html",
    closeBundle() {
      const sourcePath = path.resolve(__dirname, "native-schedule.html");
      const targetPath = path.resolve(__dirname, "native-dist", "native-schedule.html");
      fs.copyFileSync(sourcePath, targetPath);
    }
  };
}

export default defineConfig({
  define: {
    "process.env.NODE_ENV": JSON.stringify("production")
  },
  plugins: [react(), copyNativeScheduleHtml()],
  build: {
    outDir: "native-dist",
    emptyOutDir: true,
    minify: false,
    cssCodeSplit: false,
    cssMinify: false,
    assetsInlineLimit: 0,
    target: "es2019",
    lib: {
      entry: path.resolve(__dirname, "src/native-schedule-entry.jsx"),
      name: "RTDispatchWorkbenchNativeSchedule",
      formats: ["iife"],
      fileName: () => "native-schedule.js",
      cssFileName: "native-schedule"
    },
    rollupOptions: {
      output: {
        assetFileNames: (assetInfo) => {
          if (assetInfo.name === "native-schedule.css" || assetInfo.name === "style.css") {
            return "native-schedule.css";
          }
          return assetInfo.name ?? "[name][extname]";
        }
      }
    }
  }
});
