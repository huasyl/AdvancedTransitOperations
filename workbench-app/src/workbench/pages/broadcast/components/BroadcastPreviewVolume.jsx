import { useEffect, useRef, useState } from "react";
import { VolumeIcon } from "./BroadcastIcons";

function BroadcastPreviewVolumeControl({ label, value, onCommit }) {
  const [localVolume, setLocalVolume] = useState(value);
  const [isDragging, setIsDragging] = useState(false);
  const trackRef = useRef(null);
  const pendingVolumeRef = useRef(value);
  const isSavingRef = useRef(false);
  const commitTokenRef = useRef(0);

  useEffect(() => {
    if (!isDragging && !isSavingRef.current) {
      pendingVolumeRef.current = value;
      setLocalVolume(value);
    }
  }, [isDragging, value]);

  useEffect(() => {
    if (!isDragging) {
      return undefined;
    }

    function updateVolumeFromClientX(clientX) {
      const track = trackRef.current;
      if (!(track instanceof HTMLElement)) {
        return;
      }

      const rect = track.getBoundingClientRect();
      if (rect.width <= 0) {
        return;
      }

      const progress = Math.max(
        0,
        Math.min(1, (clientX - rect.left) / rect.width),
      );
      const nextVolume = Math.round(progress * 100);
      pendingVolumeRef.current = nextVolume;
      setLocalVolume(nextVolume);
    }

    function handleMouseMove(event) {
      updateVolumeFromClientX(event.clientX);
    }

    function handleMouseUp() {
      setIsDragging(false);
      void submitVolume(pendingVolumeRef.current);
    }

    window.addEventListener("mousemove", handleMouseMove);
    window.addEventListener("mouseup", handleMouseUp);
    return () => {
      window.removeEventListener("mousemove", handleMouseMove);
      window.removeEventListener("mouseup", handleMouseUp);
    };
  }, [isDragging, onCommit]);

  async function submitVolume(nextVolume) {
    const commitToken = commitTokenRef.current + 1;
    commitTokenRef.current = commitToken;
    isSavingRef.current = true;

    try {
      const result = await onCommit(nextVolume);
      if (commitToken !== commitTokenRef.current || !result) {
        return;
      }

      if (Number.isFinite(result.volume)) {
        pendingVolumeRef.current = result.volume;
        setLocalVolume(result.volume);
      }
    } finally {
      if (commitToken === commitTokenRef.current) {
        isSavingRef.current = false;
      }
    }
  }

  function handleMouseDown(event) {
    event.preventDefault();

    const track = trackRef.current;
    if (!(track instanceof HTMLElement)) {
      return;
    }

    const rect = track.getBoundingClientRect();
    if (rect.width <= 0) {
      return;
    }

    const progress = Math.max(
      0,
      Math.min(1, (event.clientX - rect.left) / rect.width),
    );
    const nextVolume = Math.round(progress * 100);
    pendingVolumeRef.current = nextVolume;
    setLocalVolume(nextVolume);
    setIsDragging(true);
  }

  return (
    <div className="dw-bc-preview-volume">
      <span className="dw-bc-preview-volume-label">{label}</span>
      <span className="dw-bc-preview-volume-icon-shell">
        <VolumeIcon className="dw-bc-preview-volume-icon" />
      </span>
      <div
        ref={trackRef}
        className="dw-bc-preview-volume-hitbox"
        onMouseDown={handleMouseDown}
      >
        <div className="dw-bc-preview-volume-track">
          <div
            className="dw-bc-preview-volume-fill"
            style={{ width: `${localVolume}%` }}
          />
          <div
            className="dw-bc-preview-volume-thumb"
            style={{ left: `${localVolume}%` }}
          />
        </div>
      </div>
      <span className="dw-bc-preview-volume-value">{`${localVolume}%`}</span>
    </div>
  );
}

export { BroadcastPreviewVolumeControl };
