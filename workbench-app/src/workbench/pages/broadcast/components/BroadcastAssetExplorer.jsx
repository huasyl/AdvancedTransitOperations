import { useLayoutEffect, useRef, useState } from "react";
import WorkbenchScrollArea from "../../../shared/WorkbenchScrollArea";
import {
  ArrowLeftIcon,
  CheckSquareIcon,
  FileAudioIcon,
  FolderIcon,
  ReturnUpIcon,
  SquareIcon,
} from "./BroadcastIcons";

function ImportCardName({ children }) {
  const textRef = useRef(null);
  const [displayText, setDisplayText] = useState(String(children || ""));

  useLayoutEffect(() => {
    const node = textRef.current;
    if (!(node instanceof HTMLElement)) {
      return undefined;
    }

    function fitsWithinTwoLines(text, width, style, maxHeight) {
      const measureNode = document.createElement("div");
      measureNode.style.position = "absolute";
      measureNode.style.visibility = "hidden";
      measureNode.style.pointerEvents = "none";
      measureNode.style.left = "-99999px";
      measureNode.style.top = "0";
      measureNode.style.width = `${width}px`;
      measureNode.style.fontFamily = style.fontFamily;
      measureNode.style.fontSize = style.fontSize;
      measureNode.style.fontWeight = style.fontWeight;
      measureNode.style.lineHeight = style.lineHeight;
      measureNode.style.letterSpacing = style.letterSpacing;
      measureNode.style.whiteSpace = "normal";
      measureNode.style.overflowWrap = "anywhere";
      measureNode.style.wordBreak = "break-word";
      measureNode.textContent = text;
      document.body.appendChild(measureNode);
      const fits = measureNode.scrollHeight <= maxHeight + 1;
      document.body.removeChild(measureNode);
      return fits;
    }

    function syncTruncation() {
      const sourceText = String(children || "");
      const computedStyle = window.getComputedStyle(node);
      const availableWidth = Math.max(0, Math.floor(node.clientWidth));
      const maxHeight = Math.max(48, Math.ceil(node.clientHeight || 48));

      if (!sourceText || availableWidth <= 0) {
        setDisplayText(sourceText);
        return;
      }

      if (
        fitsWithinTwoLines(sourceText, availableWidth, computedStyle, maxHeight)
      ) {
        setDisplayText(sourceText);
        return;
      }

      let low = 0;
      let high = sourceText.length;
      let best = "...";

      while (low <= high) {
        const middle = Math.floor((low + high) / 2);
        const candidate = `${sourceText.slice(0, middle).trimEnd()}...`;
        if (
          fitsWithinTwoLines(
            candidate,
            availableWidth,
            computedStyle,
            maxHeight,
          )
        ) {
          best = candidate;
          low = middle + 1;
        } else {
          high = middle - 1;
        }
      }

      setDisplayText(best);
    }

    syncTruncation();

    if (typeof ResizeObserver === "undefined") {
      return undefined;
    }

    const observer = new ResizeObserver(() => {
      syncTruncation();
    });
    observer.observe(node);

    return () => observer.disconnect();
  }, [children]);

  return (
    <span
      ref={textRef}
      className="dw-bc-import-card-name"
      title={String(children || "")}
    >
      {displayText}
    </span>
  );
}

export default function BroadcastAssetExplorer({
  toolbar,
  assets,
  overlay,
  actions,
}) {
  if (!overlay.shouldRenderAssetExplorer) {
    return null;
  }

  const { labels, t } = toolbar;
  const {
    externalAssetBrowser,
    selectedExternalFiles,
    currentExternalPath,
    currentExternalFolders,
    currentExternalFiles,
    currentExternalAllowedExtensions,
  } = assets;
  const { assetExplorerStage } = overlay;

  return (
    <div className={`dw-bc-import-overlay is-${assetExplorerStage}`}>
      <div className="dw-bc-import-head">
        <button
          type="button"
          className="dw-bc-import-back-button"
          onClick={actions.handleCloseAssetExplorer}
        >
          <ArrowLeftIcon className="dw-bc-import-back-icon" />
        </button>
        <div className="dw-bc-import-head-copy">
          <span className="dw-bc-import-head-title">{labels.importAsset}</span>
        </div>
      </div>

      <div className="dw-bc-import-toolbar">
        <button
          type="button"
          className={`dw-bc-import-parent-button ${!externalAssetBrowser?.parentPath ? "is-disabled" : ""}`}
          onClick={actions.handleExternalBack}
        >
          <span className="dw-bc-import-parent-icon-shell">
            <ReturnUpIcon className="dw-bc-import-parent-icon" />
          </span>
          <span>{t("broadcast.import.parent")}</span>
        </button>

        <div className="dw-bc-import-breadcrumbs">
          <span className="dw-bc-import-breadcrumb-icon-shell">
            <FolderIcon className="dw-bc-import-breadcrumb-icon" />
          </span>
          <div className="dw-bc-import-breadcrumb-copy">
            {currentExternalPath
              .split("\\")
              .filter(Boolean)
              .map((part, index, parts) => {
                const buildPath = `${parts.slice(0, index + 1).join("\\")}\\`;
                const isLast = index === parts.length - 1;
                return (
                  <div key={buildPath} className="dw-bc-import-breadcrumb-part">
                    <button
                      type="button"
                      className={`dw-bc-import-breadcrumb-button ${isLast ? "is-current" : ""}`}
                      onClick={() =>
                        actions.handleExternalPathChange(buildPath)
                      }
                    >
                      {part}
                    </button>
                    {isLast ? null : (
                      <span className="dw-bc-import-breadcrumb-sep">\</span>
                    )}
                  </div>
                );
              })}
          </div>
        </div>

        <div className="dw-bc-import-toolbar-spacer" />

        <button
          type="button"
          className="dw-bc-import-select-all"
          onClick={actions.handleToggleAllExternalFiles}
        >
          <span className="dw-bc-import-select-all-icon-shell">
            {currentExternalFiles.length > 0 &&
            currentExternalFiles.every((file) =>
              selectedExternalFiles.includes(file.id),
            ) ? (
              <CheckSquareIcon className="dw-bc-import-select-all-icon is-checked" />
            ) : (
              <SquareIcon className="dw-bc-import-select-all-icon" />
            )}
          </span>
          <span>{t("broadcast.import.selectAllCurrentDirectory")}</span>
        </button>
      </div>

      <WorkbenchScrollArea
        className="dw-bc-import-body"
        metricsKey={`${currentExternalPath}:${currentExternalFolders.length}:${currentExternalFiles.length}`}
      >
        <div key={currentExternalPath} className="dw-bc-import-grid-scene">
          <div className="dw-bc-import-grid">
            {currentExternalFolders.map((folderName) => (
              <button
                key={folderName}
                type="button"
                className="dw-bc-import-card is-folder"
                onClick={() =>
                  actions.handleExternalPathChange(
                    actions.resolveExternalFolderTargetPath(folderName),
                  )
                }
              >
                <span className="dw-bc-import-card-icon-shell">
                  <FolderIcon className="dw-bc-import-card-folder-icon" />
                </span>
                <ImportCardName>{folderName}</ImportCardName>
              </button>
            ))}

            {currentExternalFiles.map((file) => {
              const isSelected = selectedExternalFiles.includes(file.id);
              return (
                <button
                  key={file.id}
                  type="button"
                  className={`dw-bc-import-card is-file ${isSelected ? "is-selected" : ""}`}
                  onClick={() => actions.handleToggleExternalFile(file.id)}
                >
                  <span className="dw-bc-import-card-check-shell">
                    {isSelected ? (
                      <CheckSquareIcon className="dw-bc-import-card-check-icon is-checked" />
                    ) : (
                      <SquareIcon className="dw-bc-import-card-check-icon" />
                    )}
                  </span>
                  <span className="dw-bc-import-card-icon-shell">
                    <FileAudioIcon className="dw-bc-import-card-file-icon" />
                  </span>
                  <ImportCardName>{file.name}</ImportCardName>
                </button>
              );
            })}
          </div>
        </div>
      </WorkbenchScrollArea>

      <div className="dw-bc-import-foot">
        <span className="dw-bc-import-foot-note">
          {t("broadcast.import.supportedFormats", {
            formats: currentExternalAllowedExtensions.join(", "),
          })}
        </span>
        <div className="dw-bc-import-foot-actions">
          <button
            type="button"
            className="dw-bc-import-text-button"
            onClick={actions.handleCloseAssetExplorer}
          >
            {t("broadcast.import.cancel")}
          </button>
          <button
            type="button"
            className="dw-bc-primary-button"
            onClick={actions.handleImportSelectedExternalFiles}
          >
            {t("broadcast.import.confirm")}
            {selectedExternalFiles.length > 0
              ? ` (${selectedExternalFiles.length})`
              : ""}
          </button>
        </div>
      </div>
    </div>
  );
}

export { ImportCardName };
