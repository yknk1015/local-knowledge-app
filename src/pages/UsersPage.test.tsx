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
    mocks.resetUserPassword.mockResolvedValue(initialAdmin);
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
    fireEvent.click(screen.getByRole("button", { name: "パスワード再設定" }));

    expect(await screen.findByRole("dialog")).toBeVisible();
    expect(screen.getByLabelText("新しいパスワード")).toBeRequired();
    expect(screen.getByLabelText("新しいパスワード（確認）")).toBeRequired();
    fireEvent.click(screen.getByRole("button", { name: "再設定する" }));
    expect(mocks.resetUserPassword).not.toHaveBeenCalled();
  });

  it("sends the confirmed password exactly and reports success", async () => {
    render(<UsersPage />);
    await screen.findByText("ID: 0000");
    fireEvent.click(screen.getByRole("button", { name: "パスワード再設定" }));

    fireEvent.change(screen.getByLabelText("新しいパスワード"), {
      target: { value: "Surface-test-password" },
    });
    fireEvent.change(screen.getByLabelText("新しいパスワード（確認）"), {
      target: { value: "Surface-test-password" },
    });
    fireEvent.click(screen.getByRole("button", { name: "再設定する" }));

    await waitFor(() => expect(mocks.resetUserPassword).toHaveBeenCalledWith(
      "initial-admin",
      "Surface-test-password",
    ));
    expect(await screen.findByText("初期管理者のパスワードを再設定しました。")).toBeVisible();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("allows a deliberately confirmed empty password only while the policy permits it", async () => {
    render(<UsersPage />);
    await screen.findByText("ID: 0000");
    fireEvent.click(screen.getByRole("button", { name: "パスワード再設定" }));

    expect(screen.getByLabelText("新しいパスワード")).not.toBeRequired();
    expect(screen.getByLabelText("新しいパスワード（確認）")).not.toBeRequired();
    fireEvent.click(screen.getByRole("button", { name: "再設定する" }));

    await waitFor(() => expect(mocks.resetUserPassword).toHaveBeenCalledWith("initial-admin", ""));
  });

  it("keeps the reset dialog open when the confirmation does not match", async () => {
    render(<UsersPage />);
    await screen.findByText("ID: 0000");
    fireEvent.click(screen.getByRole("button", { name: "パスワード再設定" }));

    fireEvent.change(screen.getByLabelText("新しいパスワード"), {
      target: { value: "first-password" },
    });
    fireEvent.change(screen.getByLabelText("新しいパスワード（確認）"), {
      target: { value: "different-password" },
    });
    fireEvent.click(screen.getByRole("button", { name: "再設定する" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("確認用パスワードが一致しません");
    expect(screen.getByRole("dialog")).toBeVisible();
    expect(mocks.resetUserPassword).not.toHaveBeenCalled();
  });
});
