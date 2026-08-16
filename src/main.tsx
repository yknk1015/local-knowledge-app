import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { createHashRouter, RouterProvider } from "react-router-dom";
import { App } from "./app/App";
import { ColorThemeProvider } from "./app/ColorTheme";
import { AuthProvider } from "./app/AuthContext";
import "./styles/global.css";

const router = createHashRouter([
  {
    path: "*",
    element: (
      <AuthProvider>
        <ColorThemeProvider>
          <App />
        </ColorThemeProvider>
      </AuthProvider>
    ),
  },
]);

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <RouterProvider router={router} />
  </StrictMode>,
);
