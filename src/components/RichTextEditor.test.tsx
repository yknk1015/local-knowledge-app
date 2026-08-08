import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  dehydrateManagedImages,
  hydrateManagedImages,
  RichTextViewer,
  stripLinksFromPastedHtml,
} from "./RichTextEditor";
import { knowledgeApi } from "../api/knowledgeApi";

vi.mock("@tauri-apps/api/core", () => ({
  convertFileSrc: (path: string) => `asset://localhost/${path}`,
  invoke: vi.fn(),
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

describe("RichTextViewer", () => {
  afterEach(() => vi.restoreAllMocks());

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
    fireEvent.click(screen.getByRole("button", { name: "既定ブラウザーで開く" }));
    await waitFor(() => expect(openExternalUrl).toHaveBeenCalledWith("https://example.com/"));
  });
});
