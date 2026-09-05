import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  dehydrateManagedImages,
  findClipboardImageFile,
  hydrateManagedImages,
  insertCopyBlockFromSelection,
  RichTextEditor,
  RichTextViewer,
  stripLinksFromPastedHtml,
  toManagedImageDisplayUrl,
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
  it("keeps the C# staged-image URL when inserting a newly selected image", () => {
    const path = "https://knowledge-staged.local/018f0000-0000-7000-8000-000000000001.png";

    expect(toManagedImageDisplayUrl(path)).toBe(path);
  });

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

  it("removes display-only image attributes before saving", () => {
    const displayedDocument = {
      type: "doc",
      content: [{
        type: "image",
        attrs: {
          src: "asset://localhost/C:/safe/setting.png",
          alt: "設定画面",
          title: null,
          attachmentId: "018f0000-0000-7000-8000-000000000001",
          width: 640,
          height: 480,
          style: "width: 640px",
        },
      }],
    };

    expect(dehydrateManagedImages(displayedDocument)).toEqual({
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
    });
  });

  it("keeps the C# virtual-host image URL without passing it through the Tauri converter", () => {
    const id = "018f0000-0000-7000-8000-000000000001";
    const document = {
      type: "doc",
      content: [{
        type: "image",
        attrs: { src: `knowledge-attachment:${id}`, alt: "設定画面", title: null, attachmentId: id },
      }],
    };
    const hydrated = hydrateManagedImages(document, [{
      id,
      assetPath: `https://knowledge-attachments.local/${id}/${id}.png`,
      altText: "設定画面",
    }]);

    expect(((hydrated.content as Array<Record<string, unknown>>)[0]!.attrs as Record<string, unknown>).src)
      .toBe(`https://knowledge-attachments.local/${id}/${id}.png`);
    expect(dehydrateManagedImages(hydrated)).toEqual(document);
  });

  it("emits only the managed image attributes accepted by the Rust save boundary", async () => {
    const onChange = vi.fn();
    const onRequestImage = vi.fn().mockResolvedValue({
      id: "018f0000-0000-7000-8000-000000000001",
      assetPath: "C:/safe/setting.png",
      altText: "setting",
    });
    vi.spyOn(window, "prompt").mockReturnValue("設定画面");

    render(
      <RichTextEditor
        value={{ type: "doc", content: [{ type: "paragraph" }] }}
        onChange={onChange}
        onRequestImage={onRequestImage}
      />,
    );

    fireEvent.click(await screen.findByRole("button", { name: "画像" }));

    await waitFor(() => expect(onChange).toHaveBeenCalled());
    const savedDocument = onChange.mock.calls.at(-1)?.[0] as {
      content: Array<{ type: string; attrs?: Record<string, unknown> }>;
    };
    const image = savedDocument.content.find((node) => node.type === "image");
    expect(image?.attrs).toEqual({
      src: "knowledge-attachment:018f0000-0000-7000-8000-000000000001",
      alt: "設定画面",
      title: null,
      attachmentId: "018f0000-0000-7000-8000-000000000001",
    });
  });

  it("displays a newly selected C# staged image without converting its virtual-host URL", async () => {
    const onRequestImage = vi.fn().mockResolvedValue({
      id: "018f0000-0000-7000-8000-000000000001",
      assetPath: "https://knowledge-staged.local/018f0000-0000-7000-8000-000000000001.png",
      altText: "KnowledgeApp合成アイコン",
    });
    vi.spyOn(window, "prompt").mockReturnValue("KnowledgeApp合成アイコン");

    render(
      <RichTextEditor
        value={{ type: "doc", content: [{ type: "paragraph" }] }}
        onChange={vi.fn()}
        onRequestImage={onRequestImage}
      />,
    );

    fireEvent.click(await screen.findByRole("button", { name: "画像" }));

    const image = await screen.findByRole("img", { name: "KnowledgeApp合成アイコン" });
    expect(image).toHaveAttribute(
      "src",
      "https://knowledge-staged.local/018f0000-0000-7000-8000-000000000001.png",
    );
  });

  it("finds a Windows clipboard image exposed through clipboard items", () => {
    const image = new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], "image.png", {
      type: "image/png",
    });
    const clipboardData = {
      files: [] as unknown as FileList,
      items: [{
        kind: "file",
        type: "image/png",
        getAsFile: () => image,
      }] as unknown as DataTransferItemList,
    };

    expect(findClipboardImageFile(clipboardData)).toBe(image);
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
