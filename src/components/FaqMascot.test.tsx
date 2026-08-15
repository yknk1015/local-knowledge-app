import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { FaqMascot } from "./FaqMascot";

describe("FaqMascot", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("クリック時にランダムな動作を再生して待機へ戻る", async () => {
    vi.spyOn(Math, "random").mockReturnValue(0);
    render(<FaqMascot />);

    const mascot = screen.getByRole("button", { name: "FAQ Owlをクリックしてランダムに動かす" });
    expect(mascot).toHaveAttribute("data-animation", "idle");

    fireEvent.click(mascot);
    expect(mascot).toHaveAttribute("data-animation", "waving");
    expect(screen.getByText("FAQ Owlが手を振っています")).toBeInTheDocument();

    for (const duration of [140, 140, 140, 280]) {
      act(() => vi.advanceTimersByTime(duration));
    }
    expect(mascot).toHaveAttribute("data-animation", "idle");
  });

  it("連続クリックで直前と異なる動作を選ぶ", () => {
    vi.spyOn(Math, "random").mockReturnValue(0);
    render(<FaqMascot />);

    const mascot = screen.getByRole("button");
    fireEvent.click(mascot);
    expect(mascot).toHaveAttribute("data-animation", "waving");

    fireEvent.click(mascot);
    expect(mascot).toHaveAttribute("data-animation", "jumping");
  });
});
