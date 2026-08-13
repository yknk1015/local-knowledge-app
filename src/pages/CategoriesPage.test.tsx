import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { CategoriesPage } from "./CategoriesPage";

const mocks = vi.hoisted(() => ({
  listCategories: vi.fn(),
  createCategory: vi.fn(),
  updateCategory: vi.fn(),
  deleteCategory: vi.fn(),
}));

vi.mock("../api/knowledgeApi", async (importOriginal) => {
  const original = await importOriginal<typeof import("../api/knowledgeApi")>();
  return { ...original, knowledgeApi: mocks };
});

const categories = [
  { id: "root-a", parentId: null, name: "PC", description: "PC全般", depth: 1, sortOrder: 0, articleCount: 1 },
  { id: "child", parentId: "root-a", name: "Windows", description: "", depth: 2, sortOrder: 0, articleCount: 0 },
  { id: "root-b", parentId: null, name: "ネットワーク", description: "", depth: 1, sortOrder: 1, articleCount: 0 },
];

describe("CategoriesPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.listCategories.mockResolvedValue(categories);
    vi.spyOn(window, "confirm").mockReturnValue(true);
  });

  it("renames and moves a category", async () => {
    mocks.updateCategory.mockResolvedValue({
      ...categories[1],
      name: "Windows設定",
      parentId: "root-b",
    });
    render(<CategoriesPage />);

    await screen.findAllByText("Windows");
    const editButtons = screen.getAllByRole("button", { name: "編集" });
    fireEvent.click(editButtons[1]!);
    fireEvent.change(screen.getByLabelText(/分類名/), { target: { value: "Windows設定" } });
    fireEvent.change(screen.getByLabelText("移動先"), { target: { value: "root-b" } });
    fireEvent.click(screen.getByRole("button", { name: "変更を保存" }));

    await waitFor(() => {
      expect(mocks.updateCategory).toHaveBeenCalledWith("child", "Windows設定", "", "root-b");
    });
  });

  it("shows why a non-empty category cannot be deleted", async () => {
    mocks.deleteCategory.mockRejectedValue({
      code: "CAT-005",
      message: "配下分類またはFAQが残っているため、この分類は削除できません。",
      action: "配下分類とFAQを別の分類へ移動するか、先に削除してください。",
    });
    render(<CategoriesPage />);

    await screen.findAllByText("PC");
    fireEvent.click(screen.getAllByRole("button", { name: "削除" })[0]!);

    expect(await screen.findByRole("alert")).toHaveTextContent("CAT-005");
    expect(screen.getByRole("alert")).toHaveTextContent("配下分類またはFAQが残っている");
  });
});
