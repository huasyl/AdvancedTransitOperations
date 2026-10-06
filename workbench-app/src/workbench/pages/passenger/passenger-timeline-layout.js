export function selectionFrame(left, right, height, unit = 1, showStart = true, showEnd = true) {
  const center = height / 2, shoulder = 24 * unit, neck = 14 * unit;
  const half = 8 * unit, bulge = 6 * unit;
  return `M${left} 0 H${right}`
    + (showEnd ? ` V${center - shoulder}`
      + ` C${right} ${center - neck} ${right + bulge} ${center - neck} ${right + bulge} ${center - half}`
      + ` V${center + half} C${right + bulge} ${center + neck} ${right} ${center + neck} ${right} ${center + shoulder}` : "")
    + ` V${height} H${left}`
    + (showStart ? ` V${center + shoulder}`
      + ` C${left} ${center + neck} ${left - bulge} ${center + neck} ${left - bulge} ${center + half}`
      + ` V${center - half} C${left - bulge} ${center - neck} ${left} ${center - neck} ${left} ${center - shoulder}` : "")
    + " Z";
}

export function timeRangeAt(time, presets, start, end) {
  const period = presets.find((item) => item.from <= time && time < item.to);
  if (period) return [period.from, period.to];
  const before = presets.filter((item) => item.to <= time).map((item) => item.to);
  const after = presets.filter((item) => item.from > time).map((item) => item.from);
  return [Math.max(start, ...before), Math.min(end, ...after)];
}
