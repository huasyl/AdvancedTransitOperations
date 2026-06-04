import { forwardRef } from "react";

const WorkbenchInput = forwardRef(function WorkbenchInput(
  { value, onChange, onFocus, className = "", ...props },
  ref
) {
  return (
    <input
      {...props}
      ref={ref}
      className={`dw-workbench-input${className ? ` ${className}` : ""}`}
      value={value}
      onChange={onChange}
      onFocus={onFocus}
    />
  );
});

export default WorkbenchInput;
