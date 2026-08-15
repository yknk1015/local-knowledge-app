import { useEffect, useRef, useState, type CSSProperties } from "react";
import mascotSpritesheet from "../assets/mascot/faq-owl-spritesheet.webp";

const SPRITE_WIDTH = 96;
const SPRITE_HEIGHT = 104;

const animations = {
  idle: { row: 0, durations: [280, 110, 110, 140, 140, 320] },
  "running-right": { row: 1, durations: [120, 120, 120, 120, 120, 120, 120, 220] },
  "running-left": { row: 2, durations: [120, 120, 120, 120, 120, 120, 120, 220] },
  waving: { row: 3, durations: [140, 140, 140, 280] },
  jumping: { row: 4, durations: [140, 140, 140, 140, 280] },
  failed: { row: 5, durations: [140, 140, 140, 140, 140, 140, 140, 240] },
  waiting: { row: 6, durations: [150, 150, 150, 150, 150, 260] },
  running: { row: 7, durations: [120, 120, 120, 120, 120, 220] },
  review: { row: 8, durations: [150, 150, 150, 150, 150, 280] },
} as const;

type MascotAnimation = keyof typeof animations;
type ClickAnimation = Exclude<MascotAnimation, "idle" | "running-right" | "running-left" | "failed">;

const clickAnimations: readonly ClickAnimation[] = ["waving", "jumping", "waiting", "running", "review"];

const animationLabels: Record<MascotAnimation, string> = {
  idle: "FAQ Owlは待機しています",
  "running-right": "FAQ Owlは右へ走っています",
  "running-left": "FAQ Owlは左へ走っています",
  waving: "FAQ Owlが手を振っています",
  jumping: "FAQ Owlが跳ねています",
  failed: "FAQ Owlがしょんぼりしています",
  waiting: "FAQ Owlが周りを見ています",
  running: "FAQ Owlが忙しく動いています",
  review: "FAQ Owlが考えています",
};

function chooseClickAnimation(previous: ClickAnimation | null): ClickAnimation {
  const candidates = previous === null
    ? clickAnimations
    : clickAnimations.filter((animation) => animation !== previous);
  const index = Math.min(Math.floor(Math.random() * candidates.length), candidates.length - 1);
  return candidates[index] ?? "waving";
}

function usePrefersReducedMotion() {
  const [prefersReducedMotion, setPrefersReducedMotion] = useState(() =>
    typeof window !== "undefined" && typeof window.matchMedia === "function"
      ? window.matchMedia("(prefers-reduced-motion: reduce)").matches
      : false,
  );

  useEffect(() => {
    if (typeof window.matchMedia !== "function") {
      return undefined;
    }

    const mediaQuery = window.matchMedia("(prefers-reduced-motion: reduce)");
    const handleChange = (event: MediaQueryListEvent) => setPrefersReducedMotion(event.matches);
    mediaQuery.addEventListener("change", handleChange);
    return () => mediaQuery.removeEventListener("change", handleChange);
  }, []);

  return prefersReducedMotion;
}

export function FaqMascot() {
  const [animation, setAnimation] = useState<MascotAnimation>("idle");
  const [frameIndex, setFrameIndex] = useState(0);
  const previousClickAnimation = useRef<ClickAnimation | null>(null);
  const prefersReducedMotion = usePrefersReducedMotion();
  const animationSpec = animations[animation];

  useEffect(() => {
    if (prefersReducedMotion) {
      if (animation === "idle") {
        if (frameIndex !== 0) {
          setFrameIndex(0);
        }
        return undefined;
      }

      const reducedMotionTimer = window.setTimeout(() => {
        setFrameIndex(0);
        setAnimation("idle");
      }, 600);
      return () => window.clearTimeout(reducedMotionTimer);
    }

    const frameTimer = window.setTimeout(() => {
      if (frameIndex + 1 < animationSpec.durations.length) {
        setFrameIndex(frameIndex + 1);
        return;
      }

      setFrameIndex(0);
      if (animation !== "idle") {
        setAnimation("idle");
      }
    }, animationSpec.durations[frameIndex] ?? animationSpec.durations[0]);

    return () => window.clearTimeout(frameTimer);
  }, [animation, animationSpec, frameIndex, prefersReducedMotion]);

  const handleClick = () => {
    const nextAnimation = chooseClickAnimation(previousClickAnimation.current);
    previousClickAnimation.current = nextAnimation;
    setFrameIndex(0);
    setAnimation(nextAnimation);
  };

  const spriteStyle: CSSProperties = {
    backgroundImage: `url(${mascotSpritesheet})`,
    backgroundPosition: `${-frameIndex * SPRITE_WIDTH}px ${-animationSpec.row * SPRITE_HEIGHT}px`,
  };

  return (
    <button
      type="button"
      className="faq-mascot"
      data-animation={animation}
      onClick={handleClick}
      aria-label="FAQ Owlをクリックしてランダムに動かす"
      title="クリックするとランダムに動きます"
    >
      <span className="faq-mascot-sprite" style={spriteStyle} aria-hidden="true" />
      <span className="sr-only" aria-live="polite">{animationLabels[animation]}</span>
    </button>
  );
}
