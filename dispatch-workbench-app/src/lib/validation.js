import { timeToMinutes } from "./time";

export function validateManualRows(rows, t) {
  const sourceRows = Array.isArray(rows) ? rows : [];
  const seen = new Set();

  return sourceRows.map((row, index) => {
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

    if (status === "ok" && index > 0) {
      const previous = timeToMinutes(sourceRows[index - 1].time);
      if (previous !== null && timeMinutes !== null && timeMinutes < previous) {
        status = "error";
        message = t("validation.error.order");
      }
    }

    if (
      status === "ok" &&
      row.offsetMode !== "none" &&
      row.offsetMinutes !== "" &&
      !/^\d+$/.test(String(row.offsetMinutes))
    ) {
      status = "error";
      message = t("validation.error.offsetInteger");
    }

    if (status === "ok" && row.offsetMode === "none" && row.offsetMinutes !== "") {
      status = "warning";
      message = t("validation.warning.offsetIgnored");
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
