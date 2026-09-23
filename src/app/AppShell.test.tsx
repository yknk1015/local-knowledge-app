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
    vi.spyOn(knowledgeApi, "getRecoveryKeyStatus").mockResolvedValue({ hasKey: false, needsSetup: false, issuedAt: null });
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
    expect(screen.getByRole("link", { name: "利用者の管理" })).toBeInTheDocument();
    expect(screen.queryByText("自分の知識を、すぐ見つかる形に")).not.toBeInTheDocument();
  });

  it("hides administrator-only user management from general users", async () => {
    vi.spyOn(knowledgeApi, "getSettings").mockResolvedValue({
      colorTheme: "green",
      showTopCategoryInTitle: true,
      showMascot: false,
    });
    vi.spyOn(knowledgeApi, "saveSettings").mockImplementation(async (settings) => settings);
    vi.spyOn(knowledgeApi, "getCurrentUser").mockResolvedValue({
      id: "user-2",
      loginId: "1000",
      displayName: "一般利用者",
      role: "editor",
    });
    vi.spyOn(knowledgeApi, "getRecoveryKeyStatus").mockResolvedValue({ hasKey: false, needsSetup: false, issuedAt: null });
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

    expect(await screen.findByText("一般利用者")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "利用者の管理" })).not.toBeInTheDocument();
  });
  it("閲覧のみには編集・管理・Codexの導線を表示しない", async () => {
    vi.spyOn(knowledgeApi, "getSettings").mockResolvedValue({ colorTheme: "green", showTopCategoryInTitle: true, showMascot: false });
    vi.spyOn(knowledgeApi, "getCurrentUser").mockResolvedValue({ id: "viewer", loginId: "view", displayName: "合成閲覧者", role: "viewer" });
    vi.spyOn(knowledgeApi, "getRecoveryKeyStatus").mockResolvedValue({ hasKey: false, needsSetup: false, issuedAt: null });
    render(<AuthProvider><ColorThemeProvider><MemoryRouter initialEntries={["/search"]}><Routes><Route element={<AppShell />}><Route path="/search" element={<div>検索画面</div>} /></Route></Routes></MemoryRouter></ColorThemeProvider></AuthProvider>);
    expect(await screen.findByText("合成閲覧者")).toBeInTheDocument();
    const links = screen.getAllByRole("link").map(link => link.getAttribute("href"));
    expect(links).not.toContain("/articles/new");
    expect(links).not.toContain("/users");
    expect(links).not.toContain("/codex-proposals");
    expect(links).toContain("/search");
  });

});
