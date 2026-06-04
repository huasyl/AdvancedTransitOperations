import { forwardRef } from "react";
import WorkbenchInput from "./WorkbenchInput";

const TimeInput = forwardRef(function TimeInput({ value, onChange, onFocus, ...props }, ref) {
  const handleChange = (event) => {
    let nextValue = event.target.value.replace(/[^0-9]/g, "");
    if (nextValue.length >= 3) {
      nextValue = `${nextValue.slice(0, 2)}:${nextValue.slice(2, 4)}`;
    }
    onChange?.({ target: { value: nextValue } });
  };

  const handleFocus = (event) => {
    if (typeof event.target.select === "function") {
      setTimeout(() => event.target.select(), 0);
    }
    onFocus?.(event);
  };

  return (
    <WorkbenchInput
      {...props}
      ref={ref}
      value={value}
      onChange={handleChange}
      onFocus={handleFocus}
      maxLength={5}
    />
  );
});

export default TimeInput;
