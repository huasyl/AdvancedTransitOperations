export function passengerScale(values) {
  const maximum = Math.max(0, ...values.filter((value) => Number.isFinite(value)));
  const target = Math.max(10, maximum * 1.15);
  const magnitude = 10 ** Math.floor(Math.log10(target / 5));
  const choices = [1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10].map((factor) => {
    const step = factor * magnitude;
    const intervals = Math.max(4, Math.ceil(target / step));
    return { step, intervals, top: step * intervals };
  }).filter(({ step, intervals }) => Number.isInteger(step) && intervals <= 6);
  choices.sort((a, b) => a.top - b.top || Math.abs(a.intervals - 5) - Math.abs(b.intervals - 5));
  const { top, step, intervals } = choices[0];
  return { top, ticks: Array.from({ length: intervals + 1 }, (_, index) => top - index * step) };
}
