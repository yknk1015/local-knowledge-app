import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { HashRouter } from "react-router-dom";
import { App } from "./app/App";
import { ColorThemeProvider } from "./app/ColorTheme";
import "./styles/global.css";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <HashRouter>
      <ColorThemeProvider>
        <App />
      </ColorThemeProvider>
    </HashRouter>
  </StrictMode>,
);
