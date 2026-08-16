import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  dehydrateManagedImages,
  hydrateManagedImages,
  insertCopyBlockFromSelection,
  RichTextViewer,
  stripLinksFromPastedHtml,
} from "./RichTextEditor";
import { knowledgeApi } from "../api/knowledgeApi";
import type { Editor } from "@tiptap/core";

const writeTextMock = vi.hoisted(() => vi.fn());

vi.mock("@tauri-apps/api/core", () => ({
  convertFileSrc: (path: string) => `asset://localhost/${path}`,
  isTauri: () => true,
  invoke: vi.fn(),
}));

vi.mock("@tauri-apps/plugin-clipboard-manager", () => ({
  writeText: writeTextMock,
}));

describe("stripLinksFromPastedHtml", () => {
  it("keeps URL text while removing clickable links from pasted content", () => {
    const result = stripLinksFromPastedHtml(
      '<p><a href="https://localhost:7100">https://localhost:7100</a></p>',
    );

    expect(result).toContain("https://localhost:7100");
    expect(result).not.toContain("<a");
    expect(result).not.toContain("href");
  });

  it("removes externally pasted image elements", () => {
    expect(stripLinksFromPastedHtml('<p>説明</p><img src="https://example.com/a.png">'))
      .toBe("<p>説明</p>");
  });

  it("removes executable HTML and event or style attributes from pasted content", () => {
    const result = stripLinksFromPastedHtml(
      '<script>alert(1)</script><iframe src="https://example.com"></iframe><p onclick="run()" style="background:url(https://example.com/a.png)">安全な説明</p><svg onload="run()"></svg>',
    );

    expect(result).toBe("<p>安全な説明</p>");
    expect(result).not.toContain("script");
    expect(result).not.toContain("iframe");
    expect(result).not.toContain("onload");
    expect(result).not.toContain("style");
  });
});

describe("managed FAQ images", () => {
  it("uses a local asset URL only for display and keeps an attachment marker for saving", () => {
    const document = {
      type: "doc",
      content: [{
        type: "image",
        attrs: {
          src: "knowledge-attachment:018f0000-0000-7000-8000-000000000001",
          alt: "設定画面",
          title: null,
          attachmentId: "018f0000-0000-7000-8000-000000000001",
        },
      }],
    };

    const hydrated = hydrateManagedImages(document, [{
      id: "018f0000-0000-7000-8000-000000000001",
      assetPath: "C:/safe/setting.png",
      altText: "設定画面",
    }]);
    const image = (hydrated.content as Array<Record<string, unknown>>)[0];
    expect(image).toBeDefined();
    expect((image!.attrs as Record<string, unknown>).src).toBe("asset://localhost/C:/safe/setting.png");
    expect(dehydrateManagedImages(hydrated)).toEqual(document);
  });
});

describe("copy blocks", () => {
  afterEach(() => vi.restoreAllMocks());

  it("turns the current text selection into one copy block without changing the text", () => {
    const selectedText = String.raw`\\192.168.1.250\業務用フォルダ\古いファイル.xlsx`;
    const insertContent = vi.fn();
    const chain = {
      focus: vi.fn(),
      deleteSelection: vi.fn(),
      insertContent,
      run: vi.fn(() => true),
    };
    chain.focus.mockReturnValue(chain);
    chain.deleteSelection.mockReturnValue(chain);
    insertContent.mockReturnValue(chain);
    const editor = {
      state: {
        selection: { from: 1, to: selectedText.length + 1, empty: false },
        doc: { textBetween: vi.fn(() => selectedText) },
      },
      chain: vi.fn(() => chain),
    } as unknown as Editor;

    expect(insertCopyBlockFromSelection(editor)).toBe(true);
    expect(insertContent).toHaveBeenCalledWith({
      type: "copyBlock",
      attrs: { text: selectedText },
    });
  });
});

describe("RichTextViewer", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    writeTextMock.mockReset();
  });

  it("shows the destination before opening a reference URL in the default browser", async () => {
    const openExternalUrl = vi
      .spyOn(knowledgeApi, "openExternalUrl")
      .mockResolvedValue(undefined);
    render(
      <RichTextViewer
        value={{
          type: "doc",
          content: [
            {
              type: "paragraph",
              content: [
                {
                  type: "text",
                  text: "参考サイト",
                  marks: [{ type: "link", attrs: { href: "https://example.com" } }],
                },
              ],
            },
          ],
        }}
      />,
    );

    fireEvent.click(await screen.findByRole("link", { name: "参考サイト" }));

    expect(await screen.findByRole("dialog")).toBeVisible();
    expect(screen.getByText("example.com")).toBeVisible();
    expect(screen.getByText("https://example.com/")).toBeVisible();
    expect(screen.getByText(/KnowledgeAppの外で開きます/)).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "既定ブラウザーで開く" }));
    await waitFor(() => expect(openExternalUrl).toHaveBeenCalledWith("https://example.com/"));
  });

  it("copies copy-block text exactly without opening it", async () => {
    const copyText = String.raw`\\192.168.1.250\業務用フォルダ\情報があり得ないほど詰まった古いファイル.xlsx`;
    const openExternalUrl = vi.spyOn(knowledgeApi, "openExternalUrl").mockResolvedValue(undefined);
    writeTextMock.mockResolvedValue(undefined);
    render(
      <RichTextViewer
        value={{
          type: "doc",
          content: [{ type: "copyBlock", attrs: { text: copyText } }],
        }}
      />,
    );

    fireEvent.click(await screen.findByRole("button", { name: "コピー" }));

    await waitFor(() => expect(writeTextMock).toHaveBeenCalledWith(copyText));
    expect(screen.getByRole("button", { name: "コピーしました" })).toBeVisible();
    expect(screen.getByText("参照先・コピー用テキスト")).toBeVisible();
    expect(openExternalUrl).not.toHaveBeenCalled();
  });
});
