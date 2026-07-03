function SearchIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-bc-icon dw-bc-tool-icon">
      <circle cx="11" cy="11" r="7" />
      <line x1="20" y1="20" x2="16.65" y2="16.65" />
    </svg>
  );
}

function FilterIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-bc-icon dw-bc-tool-icon">
      <polygon points="22 3 2 3 10 12.46 10 19 14 21 14 12.46 22 3" />
    </svg>
  );
}

function PlusIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-bc-icon dw-bc-action-icon">
      <line x1="12" y1="5" x2="12" y2="19" />
      <line x1="5" y1="12" x2="19" y2="12" />
    </svg>
  );
}

function CloseIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-bc-icon dw-bc-close-icon">
      <line x1="18" y1="6" x2="6" y2="18" />
      <line x1="6" y1="6" x2="18" y2="18" />
    </svg>
  );
}

function TrashIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-bc-icon dw-bc-action-icon">
      <path d="M4 7h16" />
      <path d="M9 7V5h6v2" />
      <path d="M7 7l1 12h8l1-12" />
      <line x1="10" y1="10" x2="10" y2="16" />
      <line x1="14" y1="10" x2="14" y2="16" />
    </svg>
  );
}

function VolumeIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon ${className}`.trim()}>
      <polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5" />
      <path d="M15 9a4 4 0 0 1 0 6" />
      <path d="M18 6a8 8 0 0 1 0 12" />
    </svg>
  );
}

function PlayIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-bc-icon dw-bc-play-icon is-fill">
      <polygon points="8 5 19 12 8 19 8 5" />
    </svg>
  );
}

function PauseIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-bc-icon dw-bc-play-icon is-fill">
      <rect x="7" y="5" width="3.5" height="14" rx="0.8" />
      <rect x="13.5" y="5" width="3.5" height="14" rx="0.8" />
    </svg>
  );
}

function ArrowLeftIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon ${className}`.trim()}>
      <line x1="19" y1="12" x2="5" y2="12" />
      <polyline points="12 19 5 12 12 5" />
    </svg>
  );
}

function FolderIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon ${className}`.trim()}>
      <path d="M3 7h6l2 2h10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7z" />
      <path d="M3 7V6a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v1" />
    </svg>
  );
}

function ReturnUpIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon ${className}`.trim()}>
      <polyline points="9 10 4 15 9 20" />
      <path d="M20 4v8a3 3 0 0 1-3 3H4" />
    </svg>
  );
}

function FileAudioIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon ${className}`.trim()}>
      <path d="M14 2H7a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V7z" />
      <polyline points="14 2 14 7 19 7" />
      <path d="M10 16a2 2 0 1 0 2 2v-5l4-1v4a2 2 0 1 0 2 2v-7l-8 2z" />
    </svg>
  );
}

function SquareIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon ${className}`.trim()}>
      <rect x="4" y="4" width="16" height="16" rx="1.5" ry="1.5" />
    </svg>
  );
}

function CheckSquareIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon ${className}`.trim()}>
      <rect x="4" y="4" width="16" height="16" rx="1.5" ry="1.5" />
      <polyline points="8 12 11 15 16 9" />
    </svg>
  );
}

function DatabaseIcon({ className = "" }) {
  return (
    <svg
      viewBox="0 0 24 24"
      className={`dw-bc-icon dw-bc-node-icon is-variable ${className}`.trim()}
    >
      <ellipse cx="12" cy="5" rx="8" ry="3" />
      <path d="M4 5v6c0 1.7 3.6 3 8 3s8-1.3 8-3V5" />
      <path d="M4 11v8c0 1.7 3.6 3 8 3s8-1.3 8-3v-8" />
    </svg>
  );
}

function SpeakerIcon({ className = "" }) {
  return (
    <svg
      viewBox="0 0 24 24"
      className={`dw-bc-icon dw-bc-node-icon ${className}`.trim()}
    >
      <polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5" />
      <path d="M15.5 8.5a5 5 0 0 1 0 7" />
      <path d="M18.5 5.5a9 9 0 0 1 0 13" />
    </svg>
  );
}

function DelayIcon({ className = "" }) {
  return (
    <svg
      viewBox="0 0 24 24"
      className={`dw-bc-icon dw-bc-node-icon is-delay ${className}`.trim()}
    >
      <circle cx="12" cy="12" r="8" />
      <path d="M12 8v4l2.5 2.5" />
    </svg>
  );
}

export {
  SearchIcon,
  FilterIcon,
  PlusIcon,
  CloseIcon,
  TrashIcon,
  VolumeIcon,
  PlayIcon,
  PauseIcon,
  ArrowLeftIcon,
  FolderIcon,
  ReturnUpIcon,
  FileAudioIcon,
  SquareIcon,
  CheckSquareIcon,
  DatabaseIcon,
  SpeakerIcon,
  DelayIcon,
};
