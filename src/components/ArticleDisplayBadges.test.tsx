import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { ArticleDisplayBadges, isBadgeActive, localDateKey } from "./ArticleDisplayBadges";

describe("ArticleDisplayBadges", () => {
  it("keeps a badge active through the selected end date", () => {
    const noon = new Date(2026, 7, 10, 12, 0, 0);
    expect(localDateKey(noon)).toBe("2026-08-10");
    expect(isBadgeActive("2026-08-10", noon)).toBe(true);
    expect(isBadgeActive("2026-08-09", noon)).toBe(false);
  });

  it("shows active and management-only badges", () => {
    render(
      <ArticleDisplayBadges
        newBadgeUntil="9999-12-31"
        updatedBadgeUntil={null}
        isHidden
        showHidden
      />,
    );
    expect(screen.getByText("新着")).toBeInTheDocument();
    expect(screen.getByText("非表示")).toBeInTheDocument();
  });
});
