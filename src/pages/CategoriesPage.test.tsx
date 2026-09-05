import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { CategoriesPage } from "./CategoriesPage";

const mocks = vi.hoisted(() => ({
  listCategories: vi.fn(),
  createCategory: vi.fn(),
  updateCategory: vi.fn(),
  reorderCategory: vi.fn(),
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

  it("moves a category only among siblings", async () => {
    const reordered = [categories[2], categories[0], categories[1]];
    mocks.reorderCategory.mockResolvedValue(reordered);
    render(<CategoriesPage />);

    await screen.findAllByText("PC");
    expect(screen.getByRole("button", { name: "PCを上へ" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Windowsを上へ" })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "ネットワークを上へ" }));

    await waitFor(() => expect(mocks.reorderCategory).toHaveBeenCalledWith("root-b", "up"));
    expect(await screen.findByText(/上へ移動しました/)).toBeVisible();
  });

  it("hides parents that would create a sixth level or a cycle", async () => {
    const constrainedCategories = [
      { id: "level-1", parentId: null, name: "階層1", description: "", depth: 1, sortOrder: 0, articleCount: 0 },
      { id: "level-2", parentId: "level-1", name: "階層2", description: "", depth: 2, sortOrder: 0, articleCount: 0 },
      { id: "level-3", parentId: "level-2", name: "階層3", description: "", depth: 3, sortOrder: 0, articleCount: 0 },
      { id: "level-4", parentId: "level-3", name: "階層4", description: "", depth: 4, sortOrder: 0, articleCount: 0 },
      { id: "level-5", parentId: "level-4", name: "階層5", description: "", depth: 5, sortOrder: 0, articleCount: 0 },
      { id: "safe-root", parentId: null, name: "安全な移動先", description: "", depth: 1, sortOrder: 1, articleCount: 0 },
    ];
    mocks.listCategories.mockResolvedValue(constrainedCategories);
    render(<CategoriesPage />);

    await screen.findAllByText("階層5");
    const createParent = screen.getByLabelText("親となる分類") as HTMLSelectElement;
    expect([...createParent.options].map((option) => option.value)).not.toContain("level-5");
    expect([...createParent.options].map((option) => option.value)).toContain("level-4");

    fireEvent.click(screen.getAllByRole("button", { name: "編集" })[1]!);
    const moveParent = screen.getByLabelText("移動先") as HTMLSelectElement;
    const moveValues = [...moveParent.options].map((option) => option.value);
    expect(moveValues).not.toContain("level-2");
    expect(moveValues).not.toContain("level-3");
    expect(moveValues).not.toContain("level-4");
    expect(moveValues).not.toContain("level-5");
    expect(moveValues).toContain("level-1");
    expect(moveValues).toContain("safe-root");
  });
});
