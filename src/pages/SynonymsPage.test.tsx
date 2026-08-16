import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { knowledgeApi } from "../api/knowledgeApi";
import { SynonymsPage } from "./SynonymsPage";

describe("SynonymsPage", () => {
  afterEach(() => vi.restoreAllMocks());

  it("edits a group and prevents normalized duplicates in the form", async () => {
    const group = {
      id: "synonym-1",
      displayName: "パソコン",
      terms: ["PC"],
      updatedAt: "2026-08-16T00:00:00Z",
    };
    vi.spyOn(knowledgeApi, "listSynonymGroups").mockResolvedValue([group]);
    const save = vi.spyOn(knowledgeApi, "saveSynonymGroup").mockResolvedValue(group);
    render(<SynonymsPage />);

    expect(await screen.findByRole("heading", { name: "グループを編集" })).toBeVisible();
    const synonymInput = screen.getByLabelText("同義語");
    fireEvent.change(synonymInput, { target: { value: "ＰＣ" } });
    fireEvent.click(screen.getByRole("button", { name: "追加" }));
    expect(screen.getByRole("alert")).toHaveTextContent("同じ同義語がすでに登録されています");

    fireEvent.change(synonymInput, { target: { value: "コンピューター" } });
    fireEvent.keyDown(synonymInput, { key: "Enter" });
    fireEvent.click(screen.getByRole("button", { name: "同義語を保存" }));
    await waitFor(() => expect(save).toHaveBeenCalledWith(
      group.id,
      "パソコン",
      ["PC", "コンピューター"],
      false,
    ));
  });

  it("asks before allowing a term that conflicts with another group", async () => {
    vi.spyOn(knowledgeApi, "listSynonymGroups").mockResolvedValue([]);
    const save = vi.spyOn(knowledgeApi, "saveSynonymGroup").mockImplementation(async (
      _id,
      displayName,
      terms,
      allowConflicts,
    ) => {
      if (!allowConflicts) throw {
        code: "SYN-003",
        message: "同じ語が別のグループにも登録されています。",
        action: "意図した重複か確認してください。",
      };
      return { id: "synonym-2", displayName, terms, updatedAt: "2026-08-16T00:00:00Z" };
    });
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    render(<SynonymsPage />);

    fireEvent.change(await screen.findByLabelText(/代表語/), { target: { value: "端末" } });
    const synonymInput = screen.getByLabelText("同義語");
    fireEvent.change(synonymInput, { target: { value: "PC" } });
    fireEvent.keyDown(synonymInput, { key: "Enter" });
    fireEvent.click(screen.getByRole("button", { name: "同義語を保存" }));

    await waitFor(() => expect(confirm).toHaveBeenCalled());
    await waitFor(() => expect(save).toHaveBeenLastCalledWith(undefined, "端末", ["PC"], true));
    expect(await screen.findByText(/次の検索から反映されます/)).toBeVisible();
  });
});
