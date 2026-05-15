import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

const repoRoot = path.resolve(__dirname, "..");
const defaultSnapshotPath = path.join(
  os.homedir(),
  "AppData",
  "LocalLow",
  "Colossal Order",
  "Cities Skylines II",
  "Logs",
  "RapidTransitMod-planner-input-latest.json"
);
const snapshotPath = process.env.RT_PLANNER_INPUT || defaultSnapshotPath;
const plannerLabProject = path.join(repoRoot, "tools", "PlannerLab", "PlannerLab.csproj");
const plannerLabDll = path.join(repoRoot, "tools", "PlannerLab", "bin", "Debug", "net6.0", "PlannerLab.dll");

function readRequestBody(request) {
  return new Promise((resolve, reject) => {
    let body = "";
    request.setEncoding("utf8");
    request.on("data", (chunk) => {
      body += chunk;
    });
    request.on("end", () => resolve(body || "{}"));
    request.on("error", reject);
  });
}

function writeJson(response, statusCode, value) {
  response.statusCode = statusCode;
  response.setHeader("Content-Type", "application/json; charset=utf-8");
  response.end(JSON.stringify(value));
}

function runPlanner(requestJson) {
  return new Promise((resolve, reject) => {
    if (!fs.existsSync(plannerLabDll)) {
      reject(new Error(`PlannerLab.dll was not found. Run: dotnet build ${plannerLabProject} --no-restore`));
      return;
    }

    const child = spawn("dotnet", [
      plannerLabDll,
      "--snapshot",
      snapshotPath
    ], {
      cwd: repoRoot,
      env: {
        ...process.env,
        DOTNET_CLI_HOME: path.join(repoRoot, "obj", "dotnet-home"),
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE: "1"
      },
      stdio: ["pipe", "pipe", "pipe"]
    });
    let stdout = "";
    let stderr = "";
    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stdout.on("data", (chunk) => {
      stdout += chunk;
    });
    child.stderr.on("data", (chunk) => {
      stderr += chunk;
    });
    child.on("error", reject);
    child.on("close", (code) => {
      if (code === 0) {
        resolve(stdout);
        return;
      }
      reject(new Error(stderr || `Planner runner exited with code ${code}`));
    });
    child.stdin.end(requestJson || "{}");
  });
}

function plannerLabPlugin() {
  return {
    name: "rt-planner-lab-api",
    configureServer(server) {
      server.middlewares.use(async (request, response, next) => {
        if (request.url === "/api/planner-lab/context" && request.method === "GET") {
          try {
            response.statusCode = 200;
            response.setHeader("Content-Type", "application/json; charset=utf-8");
            fs.createReadStream(snapshotPath).pipe(response);
          } catch (error) {
            writeJson(response, 500, { success: false, error: error?.message || "Failed to load planner context." });
          }
          return;
        }

        if (request.url === "/api/planner-lab/run" && request.method === "POST") {
          try {
            const requestJson = await readRequestBody(request);
            const plannerJson = await runPlanner(requestJson);
            response.statusCode = 200;
            response.setHeader("Content-Type", "application/json; charset=utf-8");
            response.end(plannerJson);
          } catch (error) {
            writeJson(response, 500, { success: false, error: error?.message || "Planner runner failed." });
          }
          return;
        }

        next();
      });
    }
  };
}

export default defineConfig({
  plugins: [react(), plannerLabPlugin()],
  server: {
    host: "127.0.0.1",
    port: 5174
  },
  define: {
    "process.env.NODE_ENV": JSON.stringify("development")
  }
});
