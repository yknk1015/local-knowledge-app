import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { FaqMascot } from "./FaqMascot";

function advanceAnimation(durations: readonly number[]) {
  for (const duration of durations) {
    act(() => vi.advanceTimersByTime(duration));
  }
}

describe("FaqMascot", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it("クリックされるまでは静止画のまま動かない", () => {
    render(<FaqMascot />);

    const mascot = screen.getByRole("button", { name: "FAQ Owlをクリックしてランダムに動かす" });
    expect(mascot).toHaveAttribute("data-animation", "still");

    act(() => vi.advanceTimersByTime(10_000));
    expect(mascot).toHaveAttribute("data-animation", "still");
  });

  it("クリック時にランダムな動作を再生して静止画へ戻る", () => {
    vi.spyOn(Math, "random").mockReturnValue(0);
    render(<FaqMascot />);

    const mascot = screen.getByRole("button", { name: "FAQ Owlをクリックしてランダムに動かす" });
    expect(mascot).toHaveAttribute("data-animation", "still");

    fireEvent.click(mascot);
    expect(mascot).toHaveAttribute("data-animation", "waving");
    expect(screen.getByText("FAQ Owlが手を振っています")).toBeInTheDocument();

    advanceAnimation([140, 140, 140, 280]);
    expect(mascot).toHaveAttribute("data-animation", "still");
  });

  it("連続クリックで直前と異なる動作を選ぶ", () => {
    vi.spyOn(Math, "random").mockReturnValue(0);
    render(<FaqMascot />);

    const mascot = screen.getByRole("button");
    fireEvent.click(mascot);
    expect(mascot).toHaveAttribute("data-animation", "waving");

    advanceAnimation([140, 140, 140, 280]);
    expect(mascot).toHaveAttribute("data-animation", "still");

    fireEvent.click(mascot);
    expect(mascot).toHaveAttribute("data-animation", "jumping");
  });

  it("ムギが遊びに来てお茶を飲み、帰った後はフクロウだけの静止画へ戻る", () => {
    vi.spyOn(Math, "random")
      .mockReturnValueOnce(0.999)
      .mockReturnValueOnce(0)
      .mockReturnValueOnce(0.999);
    render(<FaqMascot />);

    const mascot = screen.getByRole("button");
    fireEvent.click(mascot);
    expect(mascot).toHaveAttribute("data-animation", "friend-arrives");
    expect(screen.getByText("友達のムギが遊びに来ました")).toBeInTheDocument();

    advanceAnimation([150, 150, 150, 150, 150, 150, 150, 280]);
    expect(mascot).toHaveAttribute("data-animation", "still-with-mugi");
    expect(mascot).toHaveAttribute("data-companion", "present");
    expect(mascot).toHaveAttribute("data-wide", "true");

    const friendSprite = mascot.querySelector<HTMLElement>(".faq-mascot-sprite");
    expect(friendSprite).not.toBeNull();
    expect(friendSprite).toHaveStyle({ width: "88px", height: `${88 * 208 / 192}px` });
    expect(friendSprite?.style.backgroundSize).toBe("704px 858px");

    act(() => vi.advanceTimersByTime(10_000));
    expect(mascot).toHaveAttribute("data-animation", "still-with-mugi");

    fireEvent.click(mascot);
    expect(mascot).toHaveAttribute("data-animation", "tea-together");
    expect(screen.getByText("FAQ Owlとムギがお茶を飲んでいます")).toBeInTheDocument();

    advanceAnimation([260, 320, 320, 420]);
    expect(mascot).toHaveAttribute("data-animation", "still-with-mugi");

    fireEvent.click(mascot);
    expect(mascot).toHaveAttribute("data-animation", "friend-leaves");
    expect(screen.getByText("友達のムギが帰っていきます")).toBeInTheDocument();

    advanceAnimation([150, 150, 150, 150, 150, 150, 150, 280]);
    expect(mascot).toHaveAttribute("data-animation", "still");
    expect(mascot).toHaveAttribute("data-companion", "absent");
  });

  it("フクロウの眠るモーションを再生して静止画へ戻る", () => {
    vi.spyOn(Math, "random").mockReturnValue(0.75);
    render(<FaqMascot />);

    const mascot = screen.getByRole("button");
    fireEvent.click(mascot);
    expect(mascot).toHaveAttribute("data-animation", "sleeping");
    expect(screen.getByText("FAQ Owlが眠っています")).toBeInTheDocument();

    advanceAnimation([220, 220, 280, 520, 520, 320, 240, 260]);
    expect(mascot).toHaveAttribute("data-animation", "still");
  });

  it("動きを減らす設定では長い連続モーションを600msで静止させる", () => {
    const mediaQuery = {
      matches: true,
      media: "(prefers-reduced-motion: reduce)",
      onchange: null,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      addListener: vi.fn(),
      removeListener: vi.fn(),
      dispatchEvent: vi.fn(),
    } as unknown as MediaQueryList;
    vi.stubGlobal("matchMedia", vi.fn(() => mediaQuery));
    vi.spyOn(Math, "random").mockReturnValue(0.999);
    render(<FaqMascot />);

    const mascot = screen.getByRole("button");
    fireEvent.click(mascot);
    expect(mascot).toHaveAttribute("data-animation", "friend-arrives");

    act(() => vi.advanceTimersByTime(599));
    expect(mascot).toHaveAttribute("data-animation", "friend-arrives");
    act(() => vi.advanceTimersByTime(1));
    expect(mascot).toHaveAttribute("data-animation", "still-with-mugi");
  });

  it("右クリックメニューから非表示を依頼できる", async () => {
    const requestHide = vi.fn().mockResolvedValue(undefined);
    render(<FaqMascot onRequestHide={requestHide} />);

    fireEvent.contextMenu(screen.getByRole("button", { name: "FAQ Owlをクリックしてランダムに動かす" }), {
      clientX: 80,
      clientY: 560,
    });
    fireEvent.click(screen.getByRole("menuitem", { name: "マスコットを非表示にする" }));

    await act(async () => Promise.resolve());
    expect(requestHide).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole("menu")).not.toBeInTheDocument();
  });

  it("表示設定がOFFの間はマスコットを描画しない", () => {
    const { rerender } = render(<FaqMascot visible={false} />);
    expect(screen.queryByRole("button", { name: "FAQ Owlをクリックしてランダムに動かす" })).not.toBeInTheDocument();

    rerender(<FaqMascot visible />);
    expect(screen.getByRole("button", { name: "FAQ Owlをクリックしてランダムに動かす" })).toBeInTheDocument();
  });
});
