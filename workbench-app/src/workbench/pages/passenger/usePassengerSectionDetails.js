import { useEffect, useRef, useState } from "react";

const MOTION = { exit: 400, headingLag: 120, headings: 360, headingMax: 620, enter: 520, stagger: 55, limit: 280 };
const motionStyle = {
  "--passenger-exit-duration": `${MOTION.exit}ms`,
  "--passenger-enter-duration": `${MOTION.enter}ms`
};

export default function usePassengerSectionDetails({ scrollRef, selectionKey, isActive }) {
  const [detail, setDetail] = useState(false);
  const [phase, setPhase] = useState("idle");
  const columnsRef = useRef(null);
  const dashboardRef = useRef(null);
  const scrollCache = useRef(0);
  const work = useRef({ frames: new Set(), timer: null, locked: false, layer: null, titles: [], nodes: [] });

  function frame(callback) {
    const state = work.current;
    const id = window.requestAnimationFrame(() => { state.frames.delete(id); callback(); });
    state.frames.add(id);
  }

  function settle(callback) { frame(() => frame(callback)); }

  function advance(next, duration) {
    settle(() => {
      work.current.timer = window.setTimeout(() => {
        work.current.timer = null;
        if (next === "enter") work.current.enterDelay = prepareContents(true);
        setPhase(next);
      }, duration);
    });
  }

  function headings() {
    return Array.from(columnsRef.current.querySelectorAll("[data-motion-heading]"));
  }

  function contents() {
    return Array.from(dashboardRef.current.querySelectorAll("[data-motion-content]"));
  }

  function writeTargets() {
    work.current.titles.forEach(({ node, box, rect }) => {
      box.firstChild.textContent = node.dataset.motionDetailTitle;
      box.style.width = `${node.parentElement.getBoundingClientRect().width}px`;
      box.style.height = `${rect.height}px`;
    });
  }

  function readTargets() {
    const state = work.current;
    const columnTop = columnsRef.current.getBoundingClientRect().top;
    const removed = Array.from(dashboardRef.current.querySelectorAll("[data-motion-overview]"))
      .map((node) => node.getBoundingClientRect()).filter((rect) => rect.bottom <= columnTop)
      .reduce((height, rect) => height + rect.height, 0);
    state.targets = state.titles.map(({ node, box, rect }) => {
      const heading = node.parentElement.getBoundingClientRect();
      const title = box.firstChild.getBoundingClientRect();
      const height = Math.max(rect.height, heading.height);
      const target = { left: rect.left, top: heading.top + scrollCache.current - removed + (height - rect.height) / 2,
        width: title.width, height: rect.height };
      return { rect: target, text: box.firstChild.textContent };
    });
  }

  function moveTitles() {
    const state = work.current;
    const distance = Math.max(0, ...state.titles.map(({ rect }, index) => {
      const target = state.targets[index].rect;
      return Math.hypot(target.left - rect.left, target.top - rect.top);
    }));
    const duration = Math.round(Math.min(MOTION.headingMax, MOTION.headings + distance * 0.35));
    state.layer.style.setProperty("--passenger-heading-duration", `${duration}ms`);
    state.titles.forEach(({ box, rect }, index) => {
      const target = state.targets[index].rect;
      box.classList.add("is-moving");
      box.style.transform = `translate(${target.left - rect.left}px, ${target.top - rect.top}px)`;
    });
    return duration;
  }

  function clearNodes() {
    work.current.nodes.forEach((node) => {
      node.classList.remove("is-motion-running");
      node.style.removeProperty("--passenger-motion-delay");
    });
    work.current.nodes = [];
  }

  function removeTitles() {
    const state = work.current;
    state.titles.forEach(({ node, visibility }) => {
      node.style.visibility = visibility;
    });
    if (state.layer?.parentNode) state.layer.parentNode.removeChild(state.layer);
    state.layer = null;
    state.titles = [];
  }

  function cleanup() {
    const state = work.current;
    state.frames.forEach((id) => window.cancelAnimationFrame(id));
    state.frames.clear();
    if (state.timer != null) window.clearTimeout(state.timer);
    state.timer = null;
    clearNodes();
    removeTitles();
    state.locked = false;
  }

  function prepareContents(entering) {
    clearNodes();
    const viewport = scrollRef.current.getBoundingClientRect();
    const top = columnsRef.current.getBoundingClientRect().top;
    const candidates = contents().map((node) => ({ node, rect: node.getBoundingClientRect() }));
    const row = candidates.find(({ node, rect }) => node.dataset.motionContent === "row" && rect.height > 0);
    const rowHeight = row ? row.rect.height : 42;
    let lastDelay = 0;
    candidates.forEach(({ node, rect }) => {
      if (rect.width <= 0 || rect.height <= 0 || rect.bottom <= viewport.top || rect.top >= viewport.bottom) return;
      const delay = entering ? Math.min(MOTION.limit, Math.max(0, Math.floor((rect.top - top) / rowHeight)) * MOTION.stagger) : 0;
      node.style.setProperty("--passenger-motion-delay", `${delay}ms`);
      node.classList.add("is-motion-running");
      work.current.nodes.push(node);
      lastDelay = Math.max(lastDelay, delay);
    });
    return lastDelay;
  }

  function toggle() {
    const state = work.current;
    if (state.locked || !columnsRef.current || !scrollRef.current) return;
    state.locked = true;
    state.nextDetail = !detail;
    if (state.nextDetail) scrollCache.current = scrollRef.current.scrollTop;
    state.targetScroll = state.nextDetail ? 0 : scrollCache.current;
    setPhase("prepare");
  }

  useEffect(() => {
    cleanup();
    setDetail(false);
    setPhase("idle");
    scrollCache.current = 0;
    return cleanup;
  }, [selectionKey, isActive]);

  useEffect(() => {
    const state = work.current;
    if (!state.locked) return;
    if (phase === "prepare") {
      const scroll = scrollRef.current;
      state.origin = scroll.getBoundingClientRect();
      const shell = scroll.parentElement;
      const shellRect = shell.getBoundingClientRect();
      const layer = document.createElement("div");
      layer.className = "rtw-passenger-dashboard passenger-detail-flight";
      layer.setAttribute("aria-hidden", "true");
      Object.assign(layer.style, {
        left: `${state.origin.left - shellRect.left}px`, top: `${state.origin.top - shellRect.top}px`,
        width: `${state.origin.width}px`, height: `${state.origin.height}px`
      });
      Object.entries(motionStyle).forEach(([name, value]) => layer.style.setProperty(name, value));
      state.layer = layer;
      shell.appendChild(layer);
      state.titles = headings().map((node) => {
        const rect = node.getBoundingClientRect();
        const box = document.createElement("div");
        box.className = "passenger-flight-heading section-heading";
        Object.assign(box.style, { left: `${rect.left - state.origin.left}px`, top: `${rect.top - state.origin.top}px`,
          width: `${Math.ceil(rect.width)}px`, height: `${rect.height}px` });
        box.appendChild(node.cloneNode(true));
        layer.appendChild(box);
        return { box, rect, node, visibility: node.style.visibility };
      });
      const takeTitles = () => {
        prepareContents(false);
        settle(() => {
          layer.style.opacity = "1";
          state.titles.forEach(({ node }) => { node.style.visibility = "hidden"; });
          setPhase("exit");
        });
      };
      if (state.nextDetail) {
        writeTargets();
        settle(() => {
          readTargets();
          state.titles.forEach(({ box, node, rect }) => {
            box.firstChild.textContent = node.textContent;
            Object.assign(box.style, { width: `${Math.ceil(rect.width)}px`, height: `${rect.height}px` });
          });
          takeTitles();
        });
      } else takeTitles();
    } else if (phase === "exit") {
      advance(state.nextDetail ? "exit-headings" : "layout", state.nextDetail ? MOTION.headingLag : MOTION.exit);
    } else if (phase === "exit-headings") {
      state.titles.forEach(({ box }, index) => {
        const target = state.targets[index];
        box.firstChild.textContent = target.text;
        Object.assign(box.style, { width: `${Math.ceil(target.rect.width)}px`, height: `${target.rect.height}px` });
      });
      settle(() => {
        const duration = moveTitles();
        advance("layout", Math.max(MOTION.exit - MOTION.headingLag, duration));
      });
    } else if (phase === "layout") {
      clearNodes();
      setDetail(state.nextDetail);
      settle(() => {
        const scroll = scrollRef.current;
        scroll.scrollTop = state.targetScroll;
        scroll.dispatchEvent(new Event("scroll"));
        settle(() => {
          state.targets = headings().map((node) => ({ rect: node.getBoundingClientRect(), text: node.textContent }));
          state.titles.forEach(({ box }, index) => {
            const target = state.targets[index];
            Object.assign(box.style, { width: `${Math.ceil(target.rect.width)}px`, height: `${target.rect.height}px` });
            box.firstChild.textContent = target.text;
          });
          if (state.nextDetail) {
            state.titles.forEach(({ box, rect }, index) => {
              const target = state.targets[index].rect;
              box.classList.remove("is-moving");
              box.style.transform = `translate(${target.left - rect.left}px, ${target.top - rect.top}px)`;
            });
            state.enterDelay = prepareContents(true);
            setPhase("enter");
          } else setPhase("headings");
        });
      });
    } else if (phase === "headings") {
      settle(() => {
        const duration = moveTitles();
        advance("enter", duration);
      });
    } else if (phase === "enter") {
      removeTitles();
      advance("complete", MOTION.enter + state.enterDelay);
    } else if (phase === "complete") {
      cleanup();
      setPhase("idle");
    }
  }, [phase]);

  return { detail, phase, moving: phase !== "idle", overviewVisible: !detail, toggle, columnsRef, dashboardRef, motionStyle };
}
