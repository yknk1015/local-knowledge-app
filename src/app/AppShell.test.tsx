import { render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { knowledgeApi } from "../api/knowledgeApi";
import { AppShell } from "./AppShell";
import { ColorThemeProvider } from "./ColorTheme";
import { AuthProvider } from "./AuthContext";

describe("AppShell", () => {
  afterEach(() => vi.restoreAllMocks());

  it("uses a concise business-oriented brand subtitle", async () => {
    vi.spyOn(knowledgeApi, "getSettings").mockResolvedValue({
      colorTheme: "blue",
      showTopCategoryInTitle: true,
      showMascot: false,
    });
    vi.spyOn(knowledgeApi, "saveSettings").mockImplementation(async (settings) => settings);
    vi.spyOn(knowledgeApi, "getCurrentUser").mockResolvedValue({
      id: "user-1",
      loginId: "0000",
      displayName: "初期管理者",
      role: "admin",
    });
    vi.spyOn(knowledgeApi, "logout").mockResolvedValue();

    render(
      <AuthProvider>
        <ColorThemeProvider>
          <MemoryRouter initialEntries={["/search"]}>
            <Routes>
              <Route element={<AppShell />}>
                <Route path="/search" element={<div>検索画面</div>} />
              </Route>
            </Routes>
          </MemoryRouter>
        </ColorThemeProvider>
      </AuthProvider>,
    );

    expect(await screen.findByText("Knowledge Base")).toBeInTheDocument();
    expect(screen.queryByText("自分の知識を、すぐ見つかる形に")).not.toBeInTheDocument();
  });
});
