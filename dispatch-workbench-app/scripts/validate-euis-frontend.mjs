import fs from "node:fs";
import path from "node:path";

const root = process.cwd();

const forbiddenFiles = [
  "index.html",
  "vite.config.js",
  "src/main.jsx",
  "src/DispatchWorkbenchApp.jsx",
  "src/data/mockData.js"
];

const requiredFiles = [
  "src/euis-entry.jsx",
  "src/DispatchWorkbenchEuisApp.jsx",
  "src/DispatchWorkbenchContent.jsx",
  "src/lib/workbench-api.js",
  "src/lib/workbench-defaults.js",
  "src/styles/dispatch-workbench.css",
  "src/assets/fonts/simhei.ttf",
  "public/locales/en-US.json",
  "public/locales/zh-HANS.json"
];

const forbiddenCssPatterns = [
  { pattern: /display\s*:\s*grid/i, reason: "EUIS 禁止使用 display:grid" },
  { pattern: /grid-template-/i, reason: "EUIS 禁止使用 grid-template-*" },
  { pattern: /(?:^|[;\s{])gap\s*:/im, reason: "EUIS 禁止使用 gap" },
  { pattern: /column-gap\s*:/i, reason: "EUIS 禁止使用 column-gap" },
  { pattern: /row-gap\s*:/i, reason: "EUIS 禁止使用 row-gap" },
  { pattern: /clamp\s*\(/i, reason: "EUIS 禁止使用 clamp(...)" },
  { pattern: /calc\s*\(/i, reason: "EUIS 禁止使用 calc(...)" },
  { pattern: /position\s*:\s*sticky/i, reason: "EUIS 禁止使用 position:sticky" },
  { pattern: /border-style\s*:\s*dashed/i, reason: "EUIS 禁止使用 border-style:dashed" },
  { pattern: /max-height\s*:\s*none/i, reason: "EUIS 禁止使用 max-height:none" },
  { pattern: /word-break\s*:/i, reason: "EUIS 禁止使用 word-break" },
  { pattern: /word-wrap\s*:/i, reason: "EUIS 禁止使用 word-wrap" },
  { pattern: /border-collapse\s*:/i, reason: "EUIS 禁止使用 border-collapse" },
  { pattern: /-webkit-appearance\s*:/i, reason: "EUIS 禁止使用 -webkit-appearance" },
  { pattern: /vector-effect\s*:/i, reason: "EUIS 禁止使用 vector-effect" },
  { pattern: /color\s*:\s*inherit/i, reason: "EUIS 禁止使用 color:inherit" }
];

const controlTextFiles = [
  "src/components/AutoScheduleRuleEditor.jsx",
  "src/components/ChoiceButtons.jsx",
  "src/components/CombinedScheduleTable.jsx",
  "src/components/ManualTimetableEditor.jsx",
  "src/components/SideContextPanel.jsx",
  "src/components/TopTabs.jsx",
  "src/components/ValidationSummary.jsx",
  "src/components/ViewModeForm.jsx",
  "src/pages/OverviewPage.jsx",
  "src/pages/SchedulePage.jsx"
];

const suspiciousJsxTextPattern = />\s*([A-Za-z\u4e00-\u9fff][^<>{}]*)\s*</g;

function readFile(relativePath) {
  return fs.readFileSync(path.join(root, relativePath), "utf8");
}

function fileExists(relativePath) {
  return fs.existsSync(path.join(root, relativePath));
}

function fail(messages) {
  console.error("EUIS frontend validation failed:\n");
  for (const message of messages) {
    console.error(`- ${message}`);
  }
  process.exit(1);
}

const errors = [];

for (const relativePath of forbiddenFiles) {
  if (fileExists(relativePath)) {
    errors.push(`Unexpected browser/mock path still exists: ${relativePath}`);
  }
}

for (const relativePath of requiredFiles) {
  if (!fileExists(relativePath)) {
    errors.push(`Required EUIS file is missing: ${relativePath}`);
  }
}

for (const relativePath of ["public/locales/en-US.json", "public/locales/zh-HANS.json"]) {
  if (!fileExists(relativePath)) {
    continue;
  }

  try {
    JSON.parse(readFile(relativePath));
  } catch (error) {
    errors.push(`Locale resource is not valid JSON: ${relativePath} (${error.message})`);
  }
}

if (fileExists("src/lib/workbench-api.js")) {
  const apiSource = readFile("src/lib/workbench-api.js");
  for (const token of ["rt_mock", "allowMock", "createMockApi", "shouldUseMock", "mockData"]) {
    if (apiSource.includes(token)) {
      errors.push(`workbench-api.js still contains forbidden fallback token: ${token}`);
    }
  }
}

if (fileExists("src/styles/dispatch-workbench.css")) {
  const cssSource = readFile("src/styles/dispatch-workbench.css");
  for (const rule of forbiddenCssPatterns) {
    if (rule.pattern.test(cssSource)) {
      errors.push(`dispatch-workbench.css violates host constraint: ${rule.reason}`);
    }
  }

  if (!cssSource.includes('font-family: "RTM SimHei"')) {
    errors.push('dispatch-workbench.css no longer declares "RTM SimHei" usage');
  }

  if (!cssSource.includes("coui://rapidtransitmod/UI/dispatch-workbench-euis/simhei.ttf")) {
    errors.push("dispatch-workbench.css no longer points to the live simhei.ttf runtime path");
  }
}

for (const relativePath of controlTextFiles) {
  if (!fileExists(relativePath)) {
    continue;
  }

  const source = readFile(relativePath);
  const lines = source.split(/\r?\n/);
  for (const [index, line] of lines.entries()) {
    let match;
    while ((match = suspiciousJsxTextPattern.exec(line)) !== null) {
      const text = match[1].trim();
      if (!text) {
        continue;
      }
      errors.push(`${relativePath}:${index + 1} contains suspicious raw JSX text node: "${text}"`);
    }
  }
}

if (errors.length > 0) {
  fail(errors);
}

console.log("EUIS frontend validation passed.");
