import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { knowledgeApi } from "../api/knowledgeApi";
import { AuthProvider } from "../app/AuthContext";
import { LoginPage } from "./LoginPage";

describe("LoginPage", () => {
  afterEach(() => vi.restoreAllMocks());

  it("allows the initial user to submit an empty password", async () => {
    vi.spyOn(knowledgeApi, "getCurrentUser").mockResolvedValue(null);
    const login = vi.spyOn(knowledgeApi, "login").mockResolvedValue({
      id: "initial-admin",
      loginId: "0000",
      displayName: "初期管理者",
      role: "admin",
    });

    render(
      <AuthProvider>
        <MemoryRouter initialEntries={["/login"]}>
          <Routes>
            <Route path="/login" element={<LoginPage />} />
            <Route path="/search" element={<div>検索画面</div>} />
          </Routes>
        </MemoryRouter>
      </AuthProvider>,
    );

    fireEvent.click(await screen.findByRole("button", { name: "ログイン" }));
    await waitFor(() => expect(login).toHaveBeenCalledWith("0000", ""));
    expect(await screen.findByText("検索画面")).toBeInTheDocument();
  });
});
