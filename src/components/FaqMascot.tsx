import {
  useEffect,
  useRef,
  useState,
  type CSSProperties,
  type KeyboardEvent,
  type MouseEvent,
} from "react";
import mascotSpritesheet from "../assets/mascot/faq-owl-spritesheet.webp";
import mugiSpritesheet from "../assets/mascot/faq-owl-mugi-spritesheet.webp";

const SOURCE_CELL_WIDTH = 192;
const SOURCE_CELL_HEIGHT = 208;
const OWL_SPRITE_HEIGHT = 70;
const OWL_SPRITE_WIDTH = 64;
const FRIEND_SPRITE_WIDTH = 88;
const FRIEND_SPRITE_HEIGHT = FRIEND_SPRITE_WIDTH * SOURCE_CELL_HEIGHT / SOURCE_CELL_WIDTH;

type MascotAnimation =
  | "still"
  | "waving"
  | "jumping"
  | "waiting"
  | "running"
  | "review"
  | "sleeping"
  | "still-with-mugi"
  | "friend-arrives"
  | "tea-together"
  | "high-five"
  | "nuzzle"
  | "play-together"
  | "read-together"
  | "friend-leaves";

type RestingAnimation = "still" | "still-with-mugi";
type ActiveAnimation = Exclude<MascotAnimation, RestingAnimation>;

interface AnimationSpec {
  sheet: "owl" | "mugi";
  row: number;
  durations: readonly number[] | null;
  settle: RestingAnimation;
  companionPresent: boolean;
}

const animations: Record<MascotAnimation, AnimationSpec> = {
  still: { sheet: "owl", row: 0, durations: null, settle: "still", companionPresent: false },
  waving: { sheet: "owl", row: 3, durations: [140, 140, 140, 280], settle: "still", companionPresent: false },
  jumping: { sheet: "owl", row: 4, durations: [140, 140, 140, 140, 280], settle: "still", companionPresent: false },
  waiting: { sheet: "owl", row: 6, durations: [150, 150, 150, 150, 150, 260], settle: "still", companionPresent: false },
  running: { sheet: "owl", row: 7, durations: [120, 120, 120, 120, 120, 220], settle: "still", companionPresent: false },
  review: { sheet: "owl", row: 8, durations: [150, 150, 150, 150, 150, 280], settle: "still", companionPresent: false },
  sleeping: { sheet: "mugi", row: 5, durations: [220, 220, 280, 520, 520, 320, 240, 260], settle: "still", companionPresent: false },
  "still-with-mugi": { sheet: "mugi", row: 0, durations: null, settle: "still-with-mugi", companionPresent: true },
  "friend-arrives": { sheet: "mugi", row: 1, durations: [150, 150, 150, 150, 150, 150, 150, 280], settle: "still-with-mugi", companionPresent: true },
  "tea-together": { sheet: "mugi", row: 3, durations: [260, 320, 320, 420], settle: "still-with-mugi", companionPresent: true },
  "high-five": { sheet: "mugi", row: 4, durations: [170, 170, 220, 170, 320], settle: "still-with-mugi", companionPresent: true },
  nuzzle: { sheet: "mugi", row: 6, durations: [210, 210, 300, 300, 210, 360], settle: "still-with-mugi", companionPresent: true },
  "play-together": { sheet: "mugi", row: 7, durations: [170, 170, 190, 190, 170, 340], settle: "still-with-mugi", companionPresent: true },
  "read-together": { sheet: "mugi", row: 8, durations: [240, 240, 300, 300, 240, 420], settle: "still-with-mugi", companionPresent: true },
  "friend-leaves": { sheet: "mugi", row: 2, durations: [150, 150, 150, 150, 150, 150, 150, 280], settle: "still", companionPresent: true },
};

const soloClickAnimations: readonly ActiveAnimation[] = [
  "waving",
  "jumping",
  "waiting",
  "running",
  "review",
  "sleeping",
  "friend-arrives",
];
const friendClickAnimations: readonly ActiveAnimation[] = [
  "tea-together",
  "high-five",
  "nuzzle",
  "play-together",
  "read-together",
  "friend-leaves",
];

const animationLabels: Record<MascotAnimation, string> = {
  still: "FAQ Owlは静止しています",
  waving: "FAQ Owlが手を振っています",
  jumping: "FAQ Owlが跳ねています",
  waiting: "FAQ Owlが周りを見ています",
  running: "FAQ Owlが忙しく動いています",
  review: "FAQ Owlが考えています",
  sleeping: "FAQ Owlが眠っています",
  "still-with-mugi": "FAQ Owlとムギは静止しています",
  "friend-arrives": "友達のムギが遊びに来ました",
  "tea-together": "FAQ Owlとムギがお茶を飲んでいます",
  "high-five": "FAQ Owlとムギがハイタッチしています",
  nuzzle: "ムギがFAQ Owlにすり寄っています",
  "play-together": "FAQ Owlとムギが一緒に遊んでいます",
  "read-together": "FAQ Owlとムギが一緒に本を読んでいます",
  "friend-leaves": "友達のムギが帰っていきます",
};

function chooseClickAnimation(
  source: readonly ActiveAnimation[],
  previous: ActiveAnimation | null,
): ActiveAnimation {
  const candidates = previous === null
    ? source
    : source.filter((animation) => animation !== previous);
  const index = Math.min(Math.floor(Math.random() * candidates.length), candidates.length - 1);
  return candidates[index] ?? source[0] ?? "waving";
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

interface FaqMascotProps {
  visible?: boolean;
  onRequestHide?: () => Promise<void>;
}

export function FaqMascot({ visible = true, onRequestHide }: FaqMascotProps) {
  const [animation, setAnimation] = useState<MascotAnimation>("still");
  const [frameIndex, setFrameIndex] = useState(0);
  const [contextMenuOpen, setContextMenuOpen] = useState(false);
  const [contextMenuPosition, setContextMenuPosition] = useState({ left: 0, top: 0 });
  const [hideSaving, setHideSaving] = useState(false);
  const [hideError, setHideError] = useState(false);
  const previousClickAnimation = useRef<ActiveAnimation | null>(null);
  const mascotButtonRef = useRef<HTMLButtonElement>(null);
  const contextMenuRef = useRef<HTMLDivElement>(null);
  const prefersReducedMotion = usePrefersReducedMotion();
  const animationSpec = animations[animation];
  const animationDurations = animationSpec.durations;

  useEffect(() => {
    if (!visible) return undefined;

    if (animationDurations === null) {
      if (frameIndex !== 0) {
        setFrameIndex(0);
      }
      return undefined;
    }

    if (prefersReducedMotion) {
      const reducedMotionTimer = window.setTimeout(() => {
        setFrameIndex(0);
        setAnimation(animationSpec.settle);
      }, 600);
      return () => window.clearTimeout(reducedMotionTimer);
    }

    const frameTimer = window.setTimeout(() => {
      if (frameIndex + 1 < animationDurations.length) {
        setFrameIndex(frameIndex + 1);
        return;
      }

      setFrameIndex(0);
      setAnimation(animationSpec.settle);
    }, animationDurations[frameIndex] ?? animationDurations[0]);

    return () => window.clearTimeout(frameTimer);
  }, [animation, animationDurations, animationSpec, frameIndex, prefersReducedMotion, visible]);

  useEffect(() => {
    if (!contextMenuOpen) return undefined;

    const closeOnOutsidePointer = (event: PointerEvent) => {
      if (!contextMenuRef.current?.contains(event.target as Node)) {
        setContextMenuOpen(false);
      }
    };
    const closeOnEscape = (event: globalThis.KeyboardEvent) => {
      if (event.key === "Escape") {
        setContextMenuOpen(false);
        mascotButtonRef.current?.focus();
      }
    };

    document.addEventListener("pointerdown", closeOnOutsidePointer);
    document.addEventListener("keydown", closeOnEscape);
    return () => {
      document.removeEventListener("pointerdown", closeOnOutsidePointer);
      document.removeEventListener("keydown", closeOnEscape);
    };
  }, [contextMenuOpen]);

  useEffect(() => {
    if (!visible && !hideSaving) {
      setContextMenuOpen(false);
    }
  }, [hideSaving, visible]);

  const handleClick = () => {
    setContextMenuOpen(false);
    if (animationSpec.durations !== null) return;

    const candidates = animation === "still-with-mugi" ? friendClickAnimations : soloClickAnimations;
    const nextAnimation = chooseClickAnimation(candidates, previousClickAnimation.current);
    previousClickAnimation.current = nextAnimation;
    setFrameIndex(0);
    setAnimation(nextAnimation);
  };

  const openContextMenu = (left: number, top: number) => {
    const menuWidth = 224;
    const menuHeight = 72;
    setHideError(false);
    setContextMenuPosition({
      left: Math.min(Math.max(8, left), Math.max(8, window.innerWidth - menuWidth - 8)),
      top: Math.min(Math.max(8, top), Math.max(8, window.innerHeight - menuHeight - 8)),
    });
    setContextMenuOpen(true);
  };

  const handleContextMenu = (event: MouseEvent<HTMLButtonElement>) => {
    event.preventDefault();
    openContextMenu(event.clientX, event.clientY);
  };

  const handleContextMenuKey = (event: KeyboardEvent<HTMLButtonElement>) => {
    if (event.key !== "ContextMenu" && !(event.shiftKey && event.key === "F10")) return;
    event.preventDefault();
    const bounds = event.currentTarget.getBoundingClientRect();
    openContextMenu(bounds.right, bounds.top);
  };

  const hideMascot = async () => {
    if (!onRequestHide || hideSaving) return;
    setHideSaving(true);
    setHideError(false);
    try {
      await onRequestHide();
      setContextMenuOpen(false);
    } catch {
      setHideError(true);
    } finally {
      setHideSaving(false);
    }
  };

  const isFriendScene = animationSpec.sheet === "mugi" && animationSpec.companionPresent;
  const spriteWidth = isFriendScene ? FRIEND_SPRITE_WIDTH : OWL_SPRITE_WIDTH;
  const spriteHeight = isFriendScene ? FRIEND_SPRITE_HEIGHT : OWL_SPRITE_HEIGHT;
  const spriteStyle: CSSProperties = {
    width: spriteWidth,
    height: spriteHeight,
    backgroundImage: `url(${animationSpec.sheet === "mugi" ? mugiSpritesheet : mascotSpritesheet})`,
    backgroundPosition: `${-frameIndex * spriteWidth}px ${-animationSpec.row * spriteHeight}px`,
    backgroundSize: `${spriteWidth * 8}px ${spriteHeight * 9}px`,
  };

  if (!visible) return null;

  return (
    <>
      <button
        ref={mascotButtonRef}
        type="button"
        className="faq-mascot"
        data-animation={animation}
        data-companion={animationSpec.companionPresent ? "present" : "absent"}
        data-sprite-sheet={animationSpec.sheet}
        data-wide={isFriendScene ? "true" : "false"}
        onClick={handleClick}
        onContextMenu={handleContextMenu}
        onKeyDown={handleContextMenuKey}
        aria-label="FAQ Owlをクリックしてランダムに動かす"
        aria-haspopup="menu"
        aria-expanded={contextMenuOpen}
        title="クリック時だけ動作、右クリックで表示設定"
      >
        <span className="faq-mascot-sprite" style={spriteStyle} aria-hidden="true" />
        <span className="sr-only" aria-live="polite">{animationLabels[animation]}</span>
      </button>
      {contextMenuOpen && (
        <div
          ref={contextMenuRef}
          className="faq-mascot-menu"
          role="menu"
          aria-label="FAQ Owlの表示設定"
          style={contextMenuPosition}
          onContextMenu={(event) => event.preventDefault()}
        >
          <button type="button" role="menuitem" disabled={hideSaving} onClick={() => void hideMascot()}>
            {hideSaving ? "保存しています…" : "マスコットを非表示にする"}
          </button>
          {hideError && <small role="alert">設定を保存できませんでした。もう一度お試しください。</small>}
        </div>
      )}
    </>
  );
}
