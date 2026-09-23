import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { knowledgeApi } from "../api/knowledgeApi";
import { PasswordRecovery } from "./PasswordRecovery";
import { RecoveryKeyPanel, RecoverySetupGate } from "./RecoveryKeyPanel";

const auth = vi.hoisted(() => ({ user: { role: "admin" } }));
vi.mock("../app/AuthContext", () => ({ useAuth: () => auth }));

// Explicit synthetic fixture, never an application-issued or user-provided key.
const syntheticKey = "2345-6789-ABCD-EFGH";
beforeEach(() => {
  vi.restoreAllMocks();
  auth.user = { role: "admin" };
  vi.spyOn(knowledgeApi, "cancelPasswordRecovery").mockResolvedValue(true);
  vi.spyOn(knowledgeApi, "getRecoveryKeyStatus").mockResolvedValue({ hasKey: false, needsSetup: false, issuedAt: null });
});

describe("password recovery screens", () => {
  it("requires matching new passwords, displays the replacement once and returns to login only after acknowledgement", async () => {
    const back = vi.fn();
    const verify = vi.spyOn(knowledgeApi, "verifyRecoveryKey").mockResolvedValue({ token: "synthetic-token", expiresAt: "synthetic-expiry" });
    const complete = vi.spyOn(knowledgeApi, "completePasswordRecovery").mockResolvedValue({ key: syntheticKey });
    const { unmount } = render(<PasswordRecovery onBack={back} />);
    expect(screen.getByText(/一般利用者は、管理者/)).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("復旧キー"), { target: { value: syntheticKey } });
    fireEvent.click(screen.getByRole("button", { name: "復旧キーを確認する" }));
    const password = await screen.findByLabelText("新しいパスワード");
    expect(verify).toHaveBeenCalledWith("0000", syntheticKey);
    expect(password).toHaveFocus();
    expect(screen.queryByDisplayValue(syntheticKey)).not.toBeInTheDocument();
    fireEvent.change(password, { target: { value: "Synthetic-password!" } });
    fireEvent.change(screen.getByLabelText("新しいパスワード（確認）"), { target: { value: "different" } });
    fireEvent.click(screen.getByRole("button", { name: "新しいパスワードを設定する" }));
    expect(complete).not.toHaveBeenCalled();
    expect(screen.getByRole("alert")).toHaveTextContent("同じパスワード");
    fireEvent.change(screen.getByLabelText("新しいパスワード（確認）"), { target: { value: "Synthetic-password!" } });
    fireEvent.click(screen.getByRole("button", { name: "新しいパスワードを設定する" }));
    expect(await screen.findByDisplayValue(syntheticKey)).toHaveAttribute("readonly");
    expect(complete).toHaveBeenCalledWith("synthetic-token", "Synthetic-password!", "Synthetic-password!");
    expect(screen.queryByLabelText("新しいパスワード")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "保管して閉じる" })).toBeDisabled();
    fireEvent.click(screen.getByRole("checkbox"));
    fireEvent.click(screen.getByRole("button", { name: "保管して閉じる" }));
    expect(back).toHaveBeenCalledOnce();
    unmount();
    expect(knowledgeApi.cancelPasswordRecovery).toHaveBeenCalledOnce();
  });

  it("clears expired authorization and requests the key again", async () => {
    vi.spyOn(knowledgeApi, "verifyRecoveryKey").mockResolvedValue({ token: "synthetic-token", expiresAt: "synthetic-expiry" });
    vi.spyOn(knowledgeApi, "completePasswordRecovery").mockRejectedValue({ code: "REC-004", message: "期限切れです。", action: "復旧キーをもう一度入力してください。" });
    render(<PasswordRecovery onBack={vi.fn()} />);
    fireEvent.change(screen.getByLabelText("復旧キー"), { target: { value: syntheticKey } });
    fireEvent.click(screen.getByRole("button", { name: "復旧キーを確認する" }));
    fireEvent.change(await screen.findByLabelText("新しいパスワード"), { target: { value: "Synthetic!" } });
    fireEvent.change(screen.getByLabelText("新しいパスワード（確認）"), { target: { value: "Synthetic!" } });
    fireEvent.click(screen.getByRole("button", { name: "新しいパスワードを設定する" }));
    expect(await screen.findByLabelText("復旧キー")).toHaveValue("");
    expect(screen.getByRole("alert")).toHaveTextContent("期限切れ");
  });

  it("does not submit duplicate verification while the host is working", async () => {
    let finish!: (value: { token: string; expiresAt: string }) => void;
    const verify = vi.spyOn(knowledgeApi, "verifyRecoveryKey").mockReturnValue(new Promise(resolve => { finish = resolve; }));
    render(<PasswordRecovery onBack={vi.fn()} />);
    fireEvent.change(screen.getByLabelText("復旧キー"), { target: { value: syntheticKey } });
    const button = screen.getByRole("button", { name: "復旧キーを確認する" });
    fireEvent.click(button); fireEvent.click(button);
    expect(verify).toHaveBeenCalledOnce();
    expect(screen.getByRole("button", { name: "ログインへ戻る" })).toBeDisabled();
    finish({ token: "synthetic-token", expiresAt: "synthetic-expiry" });
    await screen.findByLabelText("新しいパスワード");
  });

  it("issues a key after current-password verification and does not redisplay it after closing", async () => {
    const issue = vi.spyOn(knowledgeApi, "issueRecoveryKey").mockResolvedValue({ key: syntheticKey });
    render(<RecoveryKeyPanel />);
    fireEvent.change(await screen.findByLabelText("現在のパスワード（本人確認）"), { target: { value: "Synthetic-current!" } });
    fireEvent.click(screen.getByRole("button", { name: "復旧キーを作成する" }));
    await screen.findByDisplayValue(syntheticKey);
    expect(issue).toHaveBeenCalledWith("Synthetic-current!");
    fireEvent.click(screen.getByRole("checkbox"));
    fireEvent.click(screen.getByRole("button", { name: "保管して閉じる" }));
    expect(screen.queryByDisplayValue(syntheticKey)).not.toBeInTheDocument();
    expect(screen.getByLabelText("現在のパスワード（本人確認）")).toHaveValue("");
    expect(screen.getByRole("button", { name: "復旧キーを再発行する" })).toBeInTheDocument();
  });

  it("shows the first-use prompt and permits an explicit skip with a recovery warning", async () => {
    vi.mocked(knowledgeApi.getRecoveryKeyStatus).mockResolvedValue({ hasKey: false, needsSetup: true, issuedAt: null });
    const skip = vi.spyOn(knowledgeApi, "skipRecoverySetup").mockResolvedValue({ hasKey: false, needsSetup: false, issuedAt: null });
    render(<RecoverySetupGate><p>合成アプリ画面</p></RecoverySetupGate>);
    await screen.findByRole("button", { name: "後で設定する" });
    expect(screen.queryByText("合成アプリ画面")).not.toBeInTheDocument();
    expect(screen.getByText(/アクセスを復旧できなくなる/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "後で設定する" }));
    await screen.findByText("合成アプリ画面");
    expect(skip).toHaveBeenCalledOnce();
  });

  it("never requests recovery setup for a general user", async () => {
    auth.user = { role: "user" };
    render(<RecoverySetupGate><p>合成アプリ画面</p></RecoverySetupGate>);
    await waitFor(() => expect(screen.getByText("合成アプリ画面")).toBeInTheDocument());
    expect(knowledgeApi.getRecoveryKeyStatus).not.toHaveBeenCalled();
  });
});
