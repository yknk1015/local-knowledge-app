import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { HashRouter } from "react-router-dom";
import { App } from "./app/App";
import { ColorThemeProvider } from "./app/ColorTheme";
import { AuthProvider } from "./app/AuthContext";
import "./styles/global.css";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <HashRouter>
      <AuthProvider>
        <ColorThemeProvider>
          <App />
        </ColorThemeProvider>
      </AuthProvider>
    </HashRouter>
  </StrictMode>,
);
