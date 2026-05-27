import React from "react";
import ReactDOM from "react-dom/client";
import TransitDiagramPlayground from "./TransitDiagramPlayground.jsx";
import "./styles/transit-diagram-playground.css";

document.documentElement.lang = "zh-CN";
document.body.style.margin = "0";

ReactDOM.createRoot(document.getElementById("root")).render(
  <React.StrictMode>
    <TransitDiagramPlayground />
  </React.StrictMode>
);
