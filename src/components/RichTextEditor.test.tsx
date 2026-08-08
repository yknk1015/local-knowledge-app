import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { RichTextViewer, stripLinksFromPastedHtml } from "./RichTextEditor";

describe("stripLinksFromPastedHtml", () => {
  it("keeps URL text while removing clickable links from pasted content", () => {
    const result = stripLinksFromPastedHtml(
      '<p><a href="https://localhost:7100">https://localhost:7100</a></p>',
    );

    expect(result).toContain("https://localhost:7100");
    expect(result).not.toContain("<a");
    expect(result).not.toContain("href");
  });
});

describe("RichTextViewer", () => {
  afterEach(() => vi.restoreAllMocks());

  it("stops external navigation until confirmation-based opening is implemented", async () => {
    const alert = vi.spyOn(window, "alert").mockImplementation(() => undefined);
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

    expect(alert).toHaveBeenCalledOnce();
  });
});
