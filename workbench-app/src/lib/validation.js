import { timeToMinutes } from "./time";

export function validateManualRows(rows, t) {
  const sourceRows = Array.isArray(rows) ? rows : [];
  const seen = new Set();

  return sourceRows.map((row) => {
    let status = "ok";
    let message = t("validation.ok");
    const timeMinutes = timeToMinutes(row.time);
    const duplicateKey = `${row.kind}|${row.time}`;

    if (timeMinutes === null) {
      status = "error";
      message = t("validation.error.timeFormat");
    }

    if (status === "ok" && seen.has(duplicateKey)) {
      status = "error";
      message = t("validation.error.duplicate");
    } else if (status === "ok") {
      seen.add(duplicateKey);
    }

    return {
      ...row,
      validation: { status, message }
    };
  });
}

export function buildValidationIssues(validatedRows, t) {
  return validatedRows
    .filter((row) => row.validation.status !== "ok")
    .map((row) => ({
      rowId: row.id,
      severity: row.validation.status === "error" ? "error" : "warning",
      message: `${row.time || t("validation.issue.noTime")} ${
        row.kind === "local" ? t("validation.issue.local") : t("validation.issue.express")
      }: ${row.validation.message}`
    }));
}
