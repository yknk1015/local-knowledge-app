import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { UsersPage } from "./UsersPage";

const mocks = vi.hoisted(() => ({
  listUsers: vi.fn(),
  getPasswordPolicy: vi.fn(),
  createUser: vi.fn(),
  setUserActive: vi.fn(),
  resetUserPassword: vi.fn(),
}));

vi.mock("../app/AuthContext", () => ({
  useAuth: () => ({
    user: {
      id: "initial-admin",
      loginId: "0000",
      displayName: "初期管理者",
      role: "admin",
      isActive: true,
    },
  }),
}));

vi.mock("../api/knowledgeApi", async (importOriginal) => {
  const original = await importOriginal<typeof import("../api/knowledgeApi")>();
  return {
    ...original,
    knowledgeApi: {
      listUsers: mocks.listUsers,
      getPasswordPolicy: mocks.getPasswordPolicy,
      createUser: mocks.createUser,
      setUserActive: mocks.setUserActive,
      resetUserPassword: mocks.resetUserPassword,
    },
  };
});

const initialAdmin = {
  id: "initial-admin",
  loginId: "0000",
  displayName: "初期管理者",
  role: "admin" as const,
  isActive: true,
  createdAt: "2026-08-16T00:00:00Z",
  updatedAt: "2026-08-16T00:00:00Z",
  lastLoginAt: "2026-08-16T01:00:00Z",
};

describe("UsersPage password policy", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.listUsers.mockResolvedValue([initialAdmin]);
    mocks.getPasswordPolicy.mockResolvedValue({ allowEmptyPasswords: true });
  });

  it("removes the empty-password note from the create form", async () => {
    render(<UsersPage />);

    await screen.findByText("ID: 0000");
    expect(screen.queryByText("現時点では空欄も許可します。")).not.toBeInTheDocument();
    expect(screen.getByLabelText("初期パスワード")).not.toBeRequired();
  });

  it("requires future passwords without changing existing user records", async () => {
    mocks.getPasswordPolicy.mockResolvedValue({ allowEmptyPasswords: false });
    render(<UsersPage />);

    await waitFor(() => expect(screen.getByLabelText("初期パスワード")).toBeRequired());
    vi.spyOn(window, "prompt").mockReturnValue("");
    fireEvent.click(screen.getByRole("button", { name: "パスワード再設定" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("空欄のパスワードは現在許可されていません");
    expect(mocks.resetUserPassword).not.toHaveBeenCalled();
  });
});
