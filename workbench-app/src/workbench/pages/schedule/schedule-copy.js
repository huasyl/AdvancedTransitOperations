import {
  hasMinimumDepartureGapForOrigin,
  MIN_DEPARTURE_INTERVAL_MINUTES
} from "../../../lib/auto-schedule";
import { minutesToTime, timeToMinutes } from "../../../lib/time";

export const MAX_COPY_SHIFT_MINUTES = 30;

const MINUTES_PER_DAY = 24 * 60;
const SKIP_PRIORITY = MAX_COPY_SHIFT_MINUTES * 2 + 2;

// Keep the established deterministic preference: unchanged first, then later,
// then earlier by increasing distance from the source minute.
function buildShiftCandidates(maxShiftMinutes) {
  const limit = Math.min(
    MAX_COPY_SHIFT_MINUTES,
    Math.max(0, Math.trunc(Number(maxShiftMinutes) || 0))
  );
  const shifts = [0];
  for (let step = 1; step <= limit; step += 1) {
    shifts.push(step);
    shifts.push(-step);
  }

  return shifts;
}

function wrapMinute(minute) {
  return ((minute % MINUTES_PER_DAY) + MINUTES_PER_DAY) % MINUTES_PER_DAY;
}

function collectOccupiedDepartureRows(rows) {
  return (Array.isArray(rows) ? rows : [])
    .map((row) => ({
      minute: timeToMinutes(row?.time),
      originStationId: row?.originStationId || ""
    }))
    .filter((row) => row.minute !== null);
}

function sortCopySourceRows(rows) {
  return (Array.isArray(rows) ? rows : [])
    .map((row, index) => ({ row, index }))
    .sort((left, right) => {
      const leftMinute = timeToMinutes(left.row?.time);
      const rightMinute = timeToMinutes(right.row?.time);
      if (leftMinute !== null && rightMinute !== null) {
        return leftMinute - rightMinute;
      }
      if (leftMinute !== null) {
        return -1;
      }
      if (rightMinute !== null) {
        return 1;
      }
      return left.index - right.index;
    })
    .map(({ row }) => row);
}

function buildCandidateOptions(sourceRow, shiftCandidates, targetOriginStationId, occupiedRows) {
  const baseMinute = timeToMinutes(sourceRow?.time);
  if (baseMinute === null) {
    return { baseMinute: null, options: [] };
  }

  const options = shiftCandidates
    .map((shiftMinutes, choiceRank) => {
      const minute = wrapMinute(baseMinute + shiftMinutes);
      const isAvailable = hasMinimumDepartureGapForOrigin(
        minute,
        targetOriginStationId,
        occupiedRows
      );
      return isAvailable
        ? { minute, unwrappedMinute: baseMinute + shiftMinutes, shiftMinutes, choiceRank }
        : null;
    })
    .filter(Boolean);

  return { baseMinute, options };
}

function tryBuildUnchangedPlan(sortedRows, occupiedRows, targetOriginStationId, targetKind) {
  const verifiedRows = [...occupiedRows];
  const rows = [];
  let copiedCount = 0;

  for (let index = 0; index < sortedRows.length; index += 1) {
    const sourceRow = sortedRows[index];
    const sourceTime = sourceRow?.time || "";
    const baseMinute = timeToMinutes(sourceTime);
    const key = `copy-${sourceRow?.id || index}`;
    if (baseMinute === null) {
      rows.push({
        key,
        sourceRowId: sourceRow?.id || "",
        sourceTime,
        time: sourceTime,
        kind: targetKind,
        shiftMinutes: 0,
        skipped: true,
        skipReason: "invalid"
      });
      continue;
    }

    if (!hasMinimumDepartureGapForOrigin(baseMinute, targetOriginStationId, verifiedRows)) {
      return null;
    }

    verifiedRows.push({ minute: baseMinute, originStationId: targetOriginStationId });
    copiedCount += 1;
    rows.push({
      key,
      sourceRowId: sourceRow?.id || "",
      sourceTime,
      time: sourceTime,
      kind: targetKind,
      shiftMinutes: 0,
      skipped: false,
      skipReason: ""
    });
  }

  return {
    rows,
    adjustedCount: 0,
    skippedCount: rows.length - copiedCount,
    copiedCount,
    totalShiftMinutes: 0
  };
}

function buildBoundaryMasks() {
  const boundaryWindow = MIN_DEPARTURE_INTERVAL_MINUTES - 1;
  const boundaryValues = Array.from(
    { length: (boundaryWindow * 2) + 1 },
    (_, index) => index - boundaryWindow
  );
  const masks = [0];
  const limit = 1 << boundaryValues.length;
  for (let mask = 1; mask < limit; mask += 1) {
    const values = boundaryValues.filter((_, index) => (mask & (1 << index)) !== 0);
    let valid = true;
    for (let index = 1; index < values.length; index += 1) {
      if (values[index] - values[index - 1] < MIN_DEPARTURE_INTERVAL_MINUTES) {
        valid = false;
        break;
      }
    }
    if (valid) {
      masks.push(mask);
    }
  }

  return { boundaryValues, masks };
}

function getBoundaryBit(unwrappedMinute, boundaryValues) {
  const index = boundaryValues.indexOf(unwrappedMinute);
  return index < 0 ? 0 : 1 << index;
}

function hasBoundaryGap(candidate, boundaryMask, boundaryValues, targetOriginStationId) {
  for (let index = 0; index < boundaryValues.length; index += 1) {
    if ((boundaryMask & (1 << index)) === 0) {
      continue;
    }

    if (!hasMinimumDepartureGapForOrigin(
      candidate.minute,
      targetOriginStationId,
      [{ minute: wrapMinute(boundaryValues[index]), originStationId: targetOriginStationId }]
    )) {
      return false;
    }
  }

  return true;
}

const SKIP_TOKEN = String.fromCharCode(SKIP_PRIORITY + 1);

function getStateSignature(state, rowCount) {
  if (state.signature !== undefined) {
    return state.signature;
  }

  const prefix = state.previousState
    ? getStateSignature(state.previousState, rowCount).slice(0, state.lastRowIndex)
    : SKIP_TOKEN.repeat(state.lastRowIndex);
  state.signature = `${prefix}${String.fromCharCode(state.choiceRank + 1)}${SKIP_TOKEN.repeat(
    rowCount - state.lastRowIndex - 1
  )}`;
  return state.signature;
}

function reconstructChoices(finalState, rowCount) {
  const choices = Array.from({ length: rowCount }, () => ({ skipped: true }));
  let state = finalState;
  while (state && state.lastRowIndex >= 0) {
    choices[state.lastRowIndex] = state.choice;
    state = state.previousState;
  }
  return choices;
}

// Allocate all source rows together. The dynamic program maximizes copied
// rows first, then minimizes total absolute adjustment, and finally compares
// the deterministic choice ranks (0, +1, -1, +2, -2, ...). Candidate times
// are kept unwrapped for source-order constraints and wrapped for all rule
// validation, so midnight remains circular.
export function buildCopyPlan({
  sourceRows = [],
  occupiedRows = [],
  targetOriginStationId = "",
  targetKind = "local",
  maxShiftMinutes = MAX_COPY_SHIFT_MINUTES
} = {}) {
  const shiftCandidates = buildShiftCandidates(maxShiftMinutes);
  const occupied = collectOccupiedDepartureRows(occupiedRows);
  const sortedRows = sortCopySourceRows(sourceRows);
  const candidateSets = sortedRows.map((sourceRow) => buildCandidateOptions(
    sourceRow,
    shiftCandidates,
    targetOriginStationId,
    occupied
  ));
  const unchangedPlan = tryBuildUnchangedPlan(
    sortedRows,
    occupied,
    targetOriginStationId,
    targetKind
  );
  if (unchangedPlan) {
    return unchangedPlan;
  }

  const { boundaryValues, masks } = buildBoundaryMasks();
  const maskIndexes = new Map(masks.map((mask, index) => [mask, index]));
  const domainMin = -MAX_COPY_SHIFT_MINUTES;
  const domainMax = (MINUTES_PER_DAY - 1) + MAX_COPY_SHIFT_MINUTES;
  const domainSize = domainMax - domainMin + 1;
  const treeSize = 1 << Math.ceil(Math.log2(domainSize));
  const stateTrees = masks.map(() => Array(treeSize * 2).fill(null));
  const emptyState = {
    copiedCount: 0,
    totalShiftMinutes: 0,
    signature: SKIP_TOKEN.repeat(sortedRows.length),
    lastRowIndex: -1,
    previousState: null,
    boundaryMask: 0
  };

  const isBetterStoredState = (candidate, current) => {
    if (!candidate) {
      return false;
    }
    if (!current) {
      return true;
    }
    if (candidate.copiedCount !== current.copiedCount) {
      return candidate.copiedCount > current.copiedCount;
    }
    if (candidate.totalShiftMinutes !== current.totalShiftMinutes) {
      return candidate.totalShiftMinutes < current.totalShiftMinutes;
    }
    return getStateSignature(candidate, sortedRows.length) < getStateSignature(current, sortedRows.length);
  };

  const updateTree = (tree, minute, state) => {
    let index = treeSize + minute;
    if (!isBetterStoredState(state, tree[index])) {
      return;
    }

    tree[index] = state;
    index = Math.floor(index / 2);
    while (index > 0) {
      const next = isBetterStoredState(tree[index * 2], tree[index * 2 + 1])
        ? tree[index * 2]
        : tree[index * 2 + 1];
      if (tree[index] === next) {
        break;
      }
      tree[index] = next;
      index = Math.floor(index / 2);
    }
  };

  const queryTree = (tree, rightMinute) => {
    if (rightMinute < 0) {
      return null;
    }

    let left = treeSize;
    let right = treeSize + Math.min(domainSize - 1, rightMinute) + 1;
    let best = null;
    while (left < right) {
      if (left % 2 === 1) {
        if (isBetterStoredState(tree[left], best)) {
          best = tree[left];
        }
        left += 1;
      }
      if (right % 2 === 1) {
        right -= 1;
        if (isBetterStoredState(tree[right], best)) {
          best = tree[right];
        }
      }
      left = Math.floor(left / 2);
      right = Math.floor(right / 2);
    }
    return best;
  };

  candidateSets.forEach(({ baseMinute, options }, rowIndex) => {
    const pending = new Map();
    const addPending = (groupIndex, minute, previousState, candidate) => {
      if (groupIndex === undefined || groupIndex === null) {
        return;
      }
      const nextState = {
        copiedCount: previousState.copiedCount + 1,
        totalShiftMinutes: previousState.totalShiftMinutes + Math.abs(candidate.shiftMinutes),
        lastRowIndex: rowIndex,
        choiceRank: candidate.choiceRank,
        signature: undefined,
        choice: { minute: candidate.minute, shiftMinutes: candidate.shiftMinutes },
        previousState,
        boundaryMask: previousState.boundaryMask | getBoundaryBit(candidate.unwrappedMinute, boundaryValues)
      };
      const key = `${groupIndex}:${minute}`;
      const current = pending.get(key);
      if (isBetterStoredState(nextState, current)) {
        pending.set(key, nextState);
      }
    };

    (baseMinute === null ? [] : options).forEach((option) => {
      const optionIndex = option.unwrappedMinute - domainMin;
      const prefixLimit = optionIndex - MIN_DEPARTURE_INTERVAL_MINUTES;
      const emptyBoundaryBit = getBoundaryBit(option.unwrappedMinute, boundaryValues);
      addPending(maskIndexes.get(emptyBoundaryBit), optionIndex, emptyState, option);

      stateTrees.forEach((tree, groupIndex) => {
        const previousState = queryTree(tree, prefixLimit);
        if (!previousState || !hasBoundaryGap(option, previousState.boundaryMask, boundaryValues, targetOriginStationId)) {
          return;
        }

        const nextMask = previousState.boundaryMask | emptyBoundaryBit;
        addPending(maskIndexes.get(nextMask), optionIndex, previousState, option);
      });
    });

    pending.forEach((state, key) => {
      const [groupIndex, minute] = key.split(":").map(Number);
      updateTree(stateTrees[groupIndex], minute, state);
    });
  });

  let finalState = emptyState;
  stateTrees.forEach((tree) => {
    if (isBetterStoredState(tree[1], finalState)) {
      finalState = tree[1];
    }
  });

  const choices = reconstructChoices(finalState, sortedRows.length);
  const verifiedRows = [...occupied];
  let copiedCount = 0;
  let adjustedCount = 0;
  let totalShiftMinutes = 0;
  const rows = sortedRows.map((sourceRow, index) => {
    const key = `copy-${sourceRow?.id || index}`;
    const sourceTime = sourceRow?.time || "";
    const baseMinute = timeToMinutes(sourceTime);
    const choice = choices[index];
    const hasCandidate = baseMinute !== null && choice && !choice.skipped;
    if (!hasCandidate) {
      return {
        key,
        sourceRowId: sourceRow?.id || "",
        sourceTime,
        time: sourceTime,
        kind: targetKind,
        shiftMinutes: 0,
        skipped: true,
        skipReason: baseMinute === null ? "invalid" : "gap"
      };
    }

    const resolvedMinute = wrapMinute(baseMinute + choice.shiftMinutes);
    const isValid = hasMinimumDepartureGapForOrigin(
      resolvedMinute,
      targetOriginStationId,
      verifiedRows
    );
    if (!isValid) {
      // The optimized state already passed this helper. Keep the solved
      // quantity unchanged if a future refactor makes this assertion fail.
      console.assert(false, "Copy plan verification failed");
    }

    verifiedRows.push({ minute: resolvedMinute, originStationId: targetOriginStationId });
    copiedCount += 1;
    adjustedCount += choice.shiftMinutes === 0 ? 0 : 1;
    totalShiftMinutes += Math.abs(choice.shiftMinutes);
    return {
      key,
      sourceRowId: sourceRow?.id || "",
      sourceTime,
      time: minutesToTime(resolvedMinute),
      kind: targetKind,
      shiftMinutes: choice.shiftMinutes,
      skipped: false,
      skipReason: ""
    };
  });

  return {
    rows,
    adjustedCount,
    skippedCount: rows.length - copiedCount,
    copiedCount,
    totalShiftMinutes
  };
}
