import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, expect, it, vi } from "vitest";
import { knowledgeApi } from "../api/knowledgeApi";
import { StorageSettingsPanel } from "./StorageSettingsPanel";
vi.mock("../api/knowledgeApi", () => ({ knowledgeApi: { getStorageFolders: vi.fn(), selectStorageFolder: vi.fn(), saveStorageFolder: vi.fn(), checkStorageFolder: vi.fn() }, toAppError: (e: unknown) => e }));
const folder = { purpose: "backup-export", customPath: "D:/safe/backup", effectivePath: "D:/safe/backup", mode: "custom" };
beforeEach(() => { vi.clearAllMocks(); vi.mocked(knowledgeApi.getStorageFolders).mockResolvedValue([folder]); });
it("標準へ戻す操作は明示的なnullを保存し、表示先を更新する", async () => {
  vi.mocked(knowledgeApi.saveStorageFolder).mockResolvedValue({ ...folder, customPath: null, effectivePath: "D:/standard", mode: "standard" });
  render(<StorageSettingsPanel />);
  fireEvent.click(await screen.findByRole("button", { name: "標準に戻す" }));
  await waitFor(() => expect(knowledgeApi.saveStorageFolder).toHaveBeenCalledWith("backup-export", null, false));
  expect(await screen.findByText("D:/standard")).toBeInTheDocument();
});
it("Git境界などの拒否では元の保存先を残し、原因を表示する", async () => {
  vi.mocked(knowledgeApi.selectStorageFolder).mockResolvedValue("D:/repository");
  vi.mocked(knowledgeApi.saveStorageFolder).mockRejectedValue({ code: "PATH-001", message: "Gitリポジトリ内は保存できません", action: "別の場所を選んでください" });
  render(<StorageSettingsPanel />);
  fireEvent.click(await screen.findByRole("button", { name: "フォルダーを選ぶ" }));
  expect(await screen.findByText("Gitリポジトリ内は保存できません")).toBeInTheDocument();
  expect(screen.getByText("D:/safe/backup")).toBeInTheDocument();
});
it("サーバー用パネルはNAS退避先だけを表示し、端末設定とは別のAPIを使う", async () => {
  vi.mocked(knowledgeApi.getStorageFolders).mockResolvedValue([folder, { ...folder, purpose: "csv-export" }]);
  vi.mocked(knowledgeApi.checkStorageFolder).mockResolvedValue({ path: folder.customPath, writable: true, availableBytes: null });
  render(<StorageSettingsPanel server />);
  fireEvent.click(await screen.findByRole("button", { name: "接続・権限を確認" }));
  await waitFor(() => expect(knowledgeApi.checkStorageFolder).toHaveBeenCalledWith("backup-export", folder.customPath, true));
  expect(screen.queryByText("CSVの書出先")).not.toBeInTheDocument();
});
