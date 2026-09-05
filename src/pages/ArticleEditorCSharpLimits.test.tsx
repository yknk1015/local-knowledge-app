import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { knowledgeApi } from "../api/knowledgeApi";
import { ArticleEditorPage } from "./ArticleEditorPage";

const editor = vi.hoisted(() => ({ next: {} as Record<string, unknown>, current: {} as Record<string, unknown> }));
vi.mock("../components/RichTextEditor", () => ({
  RichTextEditor: ({ value, onChange, disabled }: {
    value: Record<string, unknown>;
    onChange: (value: Record<string, unknown>) => void;
    disabled: boolean;
  }) => {
    editor.current = value;
    return <button type="button" disabled={disabled} onClick={() => onChange(editor.next)}>合成本文を設定</button>;
  },
}));

describe("FAQ editor with real C# bridge local rejection", () => {
  let listener: ((event: MessageEvent) => void) | undefined;
  const postMessage = vi.fn((message: unknown) => {
    const request = message as { id: string; args: { input: unknown } };
    queueMicrotask(() => listener?.(new MessageEvent("message", {
      data: { id: request.id, ok: true, result: {
        id: "synthetic-saved", status: "draft", updatedAt: "2026-09-05T00:00:00Z",
      } },
    })));
  });

  beforeEach(() => {
    vi.spyOn(knowledgeApi, "listCategories").mockResolvedValue([
      { id: "category-1", parentId: null, name: "合成分類", description: "", depth: 1, sortOrder: 0, articleCount: 0 },
    ]);
    vi.spyOn(knowledgeApi, "listTags").mockResolvedValue([]);
    Object.defineProperty(HTMLElement.prototype, "scrollIntoView", { configurable: true, value: vi.fn() });
    vi.stubGlobal("__KNOWLEDGE_CSHARP_BRIDGE__", true);
    vi.stubGlobal("chrome", { webview: {
      postMessage,
      addEventListener: (_name: string, next: (event: MessageEvent) => void) => { listener = next; },
    } });
    postMessage.mockClear();
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it.each(["size", "depth"])("keeps input, unsaved protection and retry controls after %s rejection", async (kind) => {
    let body: Record<string, unknown> = {
      type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "合成本文" }] }],
    };
    if (kind === "size") {
      body = { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "a".repeat(16 * 1024 * 1024) }] }] };
    } else {
      for (let index = 0; index < 80; index += 1) body = { type: "blockquote", content: [body] };
      body = { type: "doc", content: [body] };
    }
    editor.next = body;
    const router = createMemoryRouter([
      { path: "/articles/new", element: <ArticleEditorPage /> },
      { path: "/articles/:articleId", element: <div>保存完了</div> },
      { path: "/search", element: <div>検索画面</div> },
    ], { initialEntries: ["/articles/new"] });
    render(<RouterProvider router={router} />);
    fireEvent.change(await screen.findByLabelText(/タイトル/), { target: { value: "入力を保持する合成FAQ" } });
    fireEvent.change(screen.getByLabelText(/所属分類/), { target: { value: "category-1" } });
    fireEvent.change(screen.getByLabelText(/概要/), { target: { value: "保存前の概要" } });
    fireEvent.click(screen.getByRole("button", { name: "合成本文を設定" }));
    fireEvent.click(screen.getByRole("button", { name: "下書きを保存" }));

    expect(await screen.findByText(kind === "size" ? "SYS-002" : "SYS-003")).toBeVisible();
    expect(screen.getByText(/入力内容は保存されていません。画面を閉じずに/)).toBeVisible();
    expect(postMessage).not.toHaveBeenCalled();
    expect(router.state.location.pathname).toBe("/articles/new");
    expect(screen.getByLabelText(/タイトル/)).toHaveValue("入力を保持する合成FAQ");
    expect(screen.getByLabelText(/所属分類/)).toHaveValue("category-1");
    expect(screen.getByLabelText(/概要/)).toHaveValue("保存前の概要");
    expect(editor.current).toBe(body);
    expect(screen.getByRole("button", { name: "下書きを保存" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "合成本文を設定" })).toBeEnabled();
    const beforeUnload = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(beforeUnload);
    expect(beforeUnload.defaultPrevented).toBe(true);

    const corrected = { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "分割した本文" }] }] };
    editor.next = corrected;
    fireEvent.click(screen.getByRole("button", { name: "合成本文を設定" }));
    fireEvent.click(screen.getByRole("button", { name: "下書きを保存" }));
    await waitFor(() => expect(postMessage).toHaveBeenCalledTimes(1));
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      command: "save_article", args: { input: expect.objectContaining({
        title: "入力を保持する合成FAQ", summary: "保存前の概要", categoryId: "category-1", bodyDoc: corrected,
      }) },
    }));
    expect(await screen.findByText("保存完了")).toBeVisible();
  });
});
