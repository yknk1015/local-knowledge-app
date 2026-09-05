import { useEffect, useRef, useState } from "react";
import { convertFileSrc, isTauri } from "@tauri-apps/api/core";
import { writeText } from "@tauri-apps/plugin-clipboard-manager";
import { mergeAttributes, Node, type Editor } from "@tiptap/core";
import Image from "@tiptap/extension-image";
import {
  EditorContent,
  NodeViewWrapper,
  ReactNodeViewRenderer,
  useEditor,
  type JSONContent,
  type NodeViewProps,
} from "@tiptap/react";
import StarterKit from "@tiptap/starter-kit";
import { TableKit } from "@tiptap/extension-table";
import { knowledgeApi, toAppError } from "../api/knowledgeApi";
import { hasCSharpBridge } from "../api/csharpBridge";
import "./RichTextEditor.css";

const EMPTY_DOCUMENT: JSONContent = {
  type: "doc",
  content: [{ type: "paragraph" }],
};

export interface ManagedImageSource {
  id: string;
  assetPath: string;
  altText: string;
}

const CSHARP_MANAGED_IMAGE_URL = /^https:\/\/knowledge-(?:attachments|staged)\.local\//;

export function toManagedImageDisplayUrl(assetPath: string): string {
  return CSHARP_MANAGED_IMAGE_URL.test(assetPath) ? assetPath : convertFileSrc(assetPath);
}

export function findClipboardImageFile(
  clipboardData: Pick<DataTransfer, "files" | "items"> | null,
): File | undefined {
  if (!clipboardData) return undefined;
  const file = Array.from(clipboardData.files).find((item) => item.type.startsWith("image/"));
  if (file) return file;
  return Array.from(clipboardData.items)
    .find((item) => item.kind === "file" && item.type.startsWith("image/"))
    ?.getAsFile() ?? undefined;
}

const ManagedImage = Image.extend({
  addAttributes() {
    return {
      src: {
        default: null,
      },
      alt: {
        default: null,
      },
      title: {
        default: null,
      },
      attachmentId: {
        default: null,
        parseHTML: (element) => element.getAttribute("data-attachment-id"),
        renderHTML: (attributes) =>
          attributes.attachmentId
            ? { "data-attachment-id": attributes.attachmentId as string }
            : {},
      },
    };
  },
}).configure({
  allowBase64: false,
  inline: false,
  HTMLAttributes: { class: "faq-inline-image" },
});

const MAX_COPY_BLOCK_LENGTH = 4_000;

async function writePlainTextToClipboard(text: string): Promise<void> {
  if (hasCSharpBridge()) {
    await knowledgeApi.writeClipboardText(text);
    return;
  }
  if (isTauri()) {
    await writeText(text);
    return;
  }
  if (!navigator.clipboard?.writeText) {
    throw new Error("Clipboard API is unavailable");
  }
  await navigator.clipboard.writeText(text);
}

function CopyBlockNodeView({ editor, node, getPos, selected, updateAttributes }: NodeViewProps) {
  const text = typeof node.attrs.text === "string" ? node.attrs.text : "";
  const [copyState, setCopyState] = useState<"idle" | "copied" | "error">("idle");
  const resetTimer = useRef<number | null>(null);

  useEffect(() => () => {
    if (resetTimer.current !== null) window.clearTimeout(resetTimer.current);
  }, []);

  const showTemporaryState = (state: "copied" | "error") => {
    setCopyState(state);
    if (resetTimer.current !== null) window.clearTimeout(resetTimer.current);
    resetTimer.current = window.setTimeout(() => setCopyState("idle"), 2_500);
  };

  const copy = async () => {
    try {
      await writePlainTextToClipboard(text);
      showTemporaryState("copied");
    } catch {
      showTemporaryState("error");
    }
  };

  const edit = () => {
    const next = window.prompt("コピーする参照先・文字列を編集してください。", text);
    if (next === null || next === text) return;
    if (!next || Array.from(next).length > MAX_COPY_BLOCK_LENGTH) {
      window.alert(`コピー用テキストは1～${MAX_COPY_BLOCK_LENGTH.toLocaleString("ja-JP")}文字で入力してください。`);
      return;
    }
    updateAttributes({ text: next });
  };

  const restoreAsText = () => {
    const position = getPos();
    if (typeof position !== "number") return;
    const paragraphs = text.split("\n").map((line) => ({
      type: "paragraph",
      content: line ? [{ type: "text", text: line }] : undefined,
    }));
    editor.chain().focus().setNodeSelection(position).insertContent(paragraphs).run();
  };

  return (
    <NodeViewWrapper
      className={`faq-copy-block${selected ? " selected" : ""}`}
      data-copy-block=""
      contentEditable={false}
    >
      <div className="copy-block-heading">
        <span><span aria-hidden="true">▣</span> 参照先・コピー用テキスト</span>
        <div className="copy-block-actions">
          {editor.isEditable && (
            <>
              <button type="button" className="copy-block-secondary" onClick={edit}>編集</button>
              <button type="button" className="copy-block-secondary" onClick={restoreAsText}>通常の文章に戻す</button>
            </>
          )}
          <button type="button" className="copy-block-button" onClick={() => void copy()}>
            {copyState === "copied" ? "コピーしました" : "コピー"}
          </button>
        </div>
      </div>
      <code>{text}</code>
      {copyState === "error" && (
        <span className="copy-block-error" role="alert">
          コピーできませんでした。文字列を選択してコピーしてください。
        </span>
      )}
    </NodeViewWrapper>
  );
}

export const CopyBlock = Node.create({
  name: "copyBlock",
  group: "block",
  atom: true,
  selectable: true,

  addAttributes() {
    return {
      text: {
        default: "",
        parseHTML: (element) => element.getAttribute("data-copy-text") ?? "",
      },
    };
  },

  parseHTML() {
    return [{ tag: "div[data-copy-block]" }];
  },

  renderHTML({ node, HTMLAttributes }) {
    return [
      "div",
      mergeAttributes(HTMLAttributes, {
        "data-copy-block": "",
        "data-copy-text": node.attrs.text as string,
      }),
      ["code", node.attrs.text as string],
    ];
  },

  addNodeView() {
    return ReactNodeViewRenderer(CopyBlockNodeView);
  },
});

const extensions = [
  StarterKit.configure({
    heading: { levels: [2, 3] },
    blockquote: false,
    code: false,
    codeBlock: false,
    horizontalRule: false,
    strike: false,
    underline: false,
    link: {
      openOnClick: false,
      autolink: false,
      defaultProtocol: "https",
      protocols: ["http", "https"],
    },
  }),
  TableKit.configure({ table: { resizable: true } }),
  ManagedImage,
  CopyBlock,
];

export function insertCopyBlockFromSelection(editor: Editor): boolean {
  const { from, to, empty } = editor.state.selection;
  const selectedText = editor.state.doc.textBetween(from, to, "\n", "\n");
  if (empty || !selectedText) {
    window.alert("コピー用にしたい文字列を先に選択してください。");
    return false;
  }
  if (Array.from(selectedText).length > MAX_COPY_BLOCK_LENGTH) {
    window.alert(`コピー用テキストは${MAX_COPY_BLOCK_LENGTH.toLocaleString("ja-JP")}文字以内にしてください。`);
    return false;
  }
  return editor
    .chain()
    .focus()
    .deleteSelection()
    .insertContent({ type: "copyBlock", attrs: { text: selectedText } })
    .run();
}

function transformImages(
  value: Record<string, unknown>,
  transform: (attributes: Record<string, unknown>) => Record<string, unknown>,
): Record<string, unknown> {
  const visit = (node: unknown): unknown => {
    if (Array.isArray(node)) return node.map(visit);
    if (!node || typeof node !== "object") return node;
    const object = node as Record<string, unknown>;
    const copy = Object.fromEntries(Object.entries(object).map(([key, item]) => [key, visit(item)]));
    if (copy.type === "image" && copy.attrs && typeof copy.attrs === "object") {
      copy.attrs = transform(copy.attrs as Record<string, unknown>);
    }
    return copy;
  };
  return visit(value) as Record<string, unknown>;
}

export function hydrateManagedImages(
  value: Record<string, unknown>,
  sources: ManagedImageSource[],
): Record<string, unknown> {
  const paths = new Map(sources.map((source) => [source.id, source.assetPath]));
  return transformImages(value, (attributes) => {
    const id = typeof attributes.attachmentId === "string" ? attributes.attachmentId : "";
    const path = paths.get(id);
    const source = path ? toManagedImageDisplayUrl(path) : null;
    return source ? { ...attributes, src: source } : attributes;
  });
}

export function dehydrateManagedImages(value: Record<string, unknown>): Record<string, unknown> {
  return transformImages(value, (attributes) => {
    const id = typeof attributes.attachmentId === "string" ? attributes.attachmentId : "";
    if (!id) return attributes;
    return {
      src: `knowledge-attachment:${id}`,
      alt: attributes.alt ?? null,
      title: attributes.title ?? null,
      attachmentId: id,
    };
  });
}

export function stripLinksFromPastedHtml(html: string): string {
  const pastedDocument = new DOMParser().parseFromString(html, "text/html");
  pastedDocument
    .querySelectorAll(
      "script, style, iframe, object, embed, link, meta, base, form, input, button, textarea, select, video, audio, source, svg, math",
    )
    .forEach((element) => element.remove());
  pastedDocument.querySelectorAll("a").forEach((anchor) => {
    anchor.replaceWith(...Array.from(anchor.childNodes));
  });
  pastedDocument.querySelectorAll("img").forEach((image) => image.remove());
  pastedDocument.querySelectorAll("*").forEach((element) => {
    for (const attribute of Array.from(element.attributes)) {
      if (
        attribute.name.toLowerCase().startsWith("on")
        || ["style", "srcdoc"].includes(attribute.name.toLowerCase())
      ) {
        element.removeAttribute(attribute.name);
      }
    }
  });
  return pastedDocument.body.innerHTML;
}

export function RichTextEditor({
  value,
  onChange,
  imageSources = [],
  onRequestImage,
  onDiscardImage,
  disabled = false,
}: {
  value?: Record<string, unknown>;
  onChange: (document: Record<string, unknown>) => void;
  imageSources?: ManagedImageSource[];
  onRequestImage?: (file?: File) => Promise<ManagedImageSource | null>;
  onDiscardImage?: (id: string) => Promise<void>;
  disabled?: boolean;
}) {
  const imageRequestRef = useRef(onRequestImage);
  imageRequestRef.current = onRequestImage;
  const sourceRef = useRef(imageSources);
  sourceRef.current = imageSources;

  const insertImage = async (file?: File) => {
    const request = imageRequestRef.current;
    if (!request || !editor) return;
    const image = await request(file);
    if (!image) return;
    const altText = window.prompt(
      "画像の内容を短く説明してください（代替テキスト）",
      image.altText,
    );
    if (altText === null) {
      await onDiscardImage?.(image.id);
      return;
    }
    editor
      .chain()
      .focus()
      .insertContent({
        type: "image",
        attrs: {
          src: toManagedImageDisplayUrl(image.assetPath),
          alt: altText,
          title: null,
          attachmentId: image.id,
        },
      })
      .run();
  };

  const editor = useEditor({
    extensions,
    content: value
      ? (hydrateManagedImages(value, imageSources) as JSONContent)
      : EMPTY_DOCUMENT,
    editable: !disabled,
    immediatelyRender: false,
    editorProps: {
      attributes: {
        class: "rich-editor-content",
        "aria-label": "FAQの回答",
      },
      transformPastedHTML: stripLinksFromPastedHtml,
      handlePaste: (_view, event) => {
        const imageFile = findClipboardImageFile(event.clipboardData);
        if (!imageFile || !imageRequestRef.current) return false;
        void insertImage(imageFile);
        return true;
      },
      handleDrop: (_view, event) => {
        if (event.dataTransfer?.files.length) {
          event.preventDefault();
          return true;
        }
        return false;
      },
    },
    onUpdate: ({ editor: currentEditor }) => {
      onChange(
        dehydrateManagedImages(currentEditor.getJSON() as Record<string, unknown>),
      );
    },
  });

  useEffect(() => {
    if (!editor || !value) return;
    const current = JSON.stringify(
      dehydrateManagedImages(editor.getJSON() as Record<string, unknown>),
    );
    const next = JSON.stringify(value);
    if (current !== next) {
      editor.commands.setContent(hydrateManagedImages(value, sourceRef.current) as JSONContent);
    }
  }, [editor, imageSources, value]);

  useEffect(() => {
    if (editor) editor.setEditable(!disabled);
  }, [disabled, editor]);

  if (!editor) return <div className="rich-editor loading">エディターを準備しています…</div>;

  const setLink = () => {
    const existing = editor.getAttributes("link").href as string | undefined;
    const href = window.prompt("参考URLを入力してください（http:// または https://）", existing ?? "https://");
    if (href === null) return;
    if (href === "") {
      editor.chain().focus().extendMarkRange("link").unsetLink().run();
      return;
    }
    if (!/^https?:\/\//i.test(href)) {
      window.alert("http:// または https:// で始まるURLを入力してください。");
      return;
    }
    editor.chain().focus().extendMarkRange("link").setLink({ href }).run();
  };

  const editImageAlt = () => {
    const current = editor.getAttributes("image").alt as string | undefined;
    const next = window.prompt("画像の内容を短く説明してください（代替テキスト）", current ?? "");
    if (next !== null) editor.chain().focus().updateAttributes("image", { alt: next }).run();
  };

  const blockType = editor.isActive("heading", { level: 2 })
    ? "heading2"
    : editor.isActive("heading", { level: 3 })
      ? "heading3"
      : "paragraph";

  const changeBlockType = (next: string) => {
    if (next === "heading2") editor.chain().focus().setHeading({ level: 2 }).run();
    else if (next === "heading3") editor.chain().focus().setHeading({ level: 3 }).run();
    else editor.chain().focus().setParagraph().run();
  };

  return (
    <div className="rich-editor">
      <div className="editor-toolbar" aria-label="回答の書式">
        <div className="toolbar-group">
          <label className="sr-only" htmlFor="answer-block-type">段落の種類</label>
          <select id="answer-block-type" value={blockType} onChange={(event) => changeBlockType(event.target.value)} disabled={disabled}>
            <option value="paragraph">本文</option>
            <option value="heading2">見出し2</option>
            <option value="heading3">見出し3</option>
          </select>
          <button type="button" aria-label="太字" title="太字" aria-pressed={editor.isActive("bold")} onClick={() => editor.chain().focus().toggleBold().run()} className={editor.isActive("bold") ? "active" : ""} disabled={disabled}><strong>B</strong></button>
          <button type="button" aria-label="斜体" title="斜体" aria-pressed={editor.isActive("italic")} onClick={() => editor.chain().focus().toggleItalic().run()} className={editor.isActive("italic") ? "active" : ""} disabled={disabled}><em>I</em></button>
        </div>
        <div className="toolbar-group">
          <button type="button" aria-label="箇条書き" title="箇条書き" aria-pressed={editor.isActive("bulletList")} onClick={() => editor.chain().focus().toggleBulletList().run()} className={editor.isActive("bulletList") ? "active" : ""} disabled={disabled}><span aria-hidden="true">•☰</span></button>
          <button type="button" aria-label="番号付きリスト" title="番号付きリスト" aria-pressed={editor.isActive("orderedList")} onClick={() => editor.chain().focus().toggleOrderedList().run()} className={editor.isActive("orderedList") ? "active" : ""} disabled={disabled}><span aria-hidden="true">1.☰</span></button>
        </div>
        <div className="toolbar-group">
          <button type="button" className="wide-tool" onClick={() => editor.chain().focus().insertTable({ rows: 3, cols: 3, withHeaderRow: true }).run()} disabled={disabled}><span aria-hidden="true">▦</span> 表</button>
          {editor.isActive("table") && (
            <>
              <button type="button" aria-label="表の行を追加" title="選択位置の下に行を追加" onClick={() => editor.chain().focus().addRowAfter().run()} disabled={disabled}>＋行</button>
              <button type="button" aria-label="表の列を追加" title="選択位置の右に列を追加" onClick={() => editor.chain().focus().addColumnAfter().run()} disabled={disabled}>＋列</button>
              <button type="button" aria-label="表の行を削除" title="選択中の行を削除" onClick={() => editor.chain().focus().deleteRow().run()} disabled={disabled}>－行</button>
              <button type="button" aria-label="表の列を削除" title="選択中の列を削除" onClick={() => editor.chain().focus().deleteColumn().run()} disabled={disabled}>－列</button>
            </>
          )}
          <button type="button" className="wide-tool" onClick={() => void insertImage()} disabled={disabled || !onRequestImage}><span aria-hidden="true">▧</span> 画像</button>
        </div>
        <div className="toolbar-group">
          <button type="button" className={`wide-tool${editor.isActive("link") ? " active" : ""}`} aria-pressed={editor.isActive("link")} onClick={setLink} disabled={disabled}><span aria-hidden="true">↗</span> 参考URL</button>
          <button type="button" aria-label="リンク解除" title="表示文字を残してリンクを解除" onClick={() => editor.chain().focus().extendMarkRange("link").unsetLink().run()} disabled={disabled || !editor.isActive("link")}><span aria-hidden="true">×↗</span></button>
        </div>
        <div className="toolbar-group">
          <button
            type="button"
            className="wide-tool"
            title="選択したファイル・フォルダのパスなどを、自動で開かないコピー専用枠へ変換します"
            onClick={() => insertCopyBlockFromSelection(editor)}
            disabled={disabled}
          >
            <span aria-hidden="true">▣</span> コピー用
          </button>
        </div>
        {editor.isActive("image") && (
          <div className="toolbar-group">
            <button type="button" className="wide-tool" onClick={editImageAlt} disabled={disabled}>画像の説明</button>
            <button type="button" className="wide-tool danger-tool" onClick={() => editor.chain().focus().deleteSelection().run()} disabled={disabled}>画像を削除</button>
          </div>
        )}
        <div className="toolbar-group history">
          <button type="button" aria-label="元に戻す" title="元に戻す" onClick={() => editor.chain().focus().undo().run()} disabled={disabled || !editor.can().undo()}>↶</button>
          <button type="button" aria-label="やり直す" title="やり直す" onClick={() => editor.chain().focus().redo().run()} disabled={disabled || !editor.can().redo()}>↷</button>
        </div>
      </div>
      <EditorContent editor={editor} />
    </div>
  );
}

export function RichTextViewer({
  value,
  imageSources = [],
}: {
  value: Record<string, unknown>;
  imageSources?: ManagedImageSource[];
}) {
  const [pendingUrl, setPendingUrl] = useState<{
    url: string;
    displayText: string;
    host: string;
  } | null>(null);
  const [openingUrl, setOpeningUrl] = useState(false);
  const [urlError, setUrlError] = useState<string | null>(null);
  const editor = useEditor({
    extensions,
    content: hydrateManagedImages(value, imageSources) as JSONContent,
    editable: false,
    immediatelyRender: false,
    editorProps: { attributes: { class: "rich-viewer-content" } },
  });

  useEffect(() => {
    if (editor) {
      editor.commands.setContent(hydrateManagedImages(value, imageSources) as JSONContent);
    }
  }, [editor, imageSources, value]);

  const requestExternalNavigation = (target: EventTarget | null) => {
    const anchor = target instanceof Element ? target.closest("a") : null;
    const href = anchor?.getAttribute("href");
    if (!anchor || !href) return false;
    try {
      const parsed = new URL(href);
      if (!["http:", "https:"].includes(parsed.protocol)) return false;
      setUrlError(null);
      setPendingUrl({
        url: parsed.toString(),
        displayText: anchor.textContent?.trim() || parsed.toString(),
        host: parsed.hostname,
      });
    } catch {
      setUrlError("参考URLの形式を確認できませんでした。FAQを編集してURLを設定し直してください。");
    }
    return true;
  };

  const openPendingUrl = async () => {
    if (!pendingUrl) return;
    setOpeningUrl(true);
    setUrlError(null);
    try {
      await knowledgeApi.openExternalUrl(pendingUrl.url);
      setPendingUrl(null);
    } catch (caught) {
      setUrlError(toAppError(caught).message);
    } finally {
      setOpeningUrl(false);
    }
  };

  return (
    <div
      className="rich-viewer"
      onClickCapture={(event) => {
        if (requestExternalNavigation(event.target)) {
          event.preventDefault();
          event.stopPropagation();
        }
      }}
      onKeyDownCapture={(event) => {
        if (event.key === "Enter" && requestExternalNavigation(event.target)) {
          event.preventDefault();
          event.stopPropagation();
        }
      }}
    >
      <EditorContent editor={editor} />
      {pendingUrl && (
        <div className="modal-backdrop" role="presentation">
          <section className="url-confirm-dialog" role="dialog" aria-modal="true" aria-labelledby="url-confirm-title">
            <span className="eyebrow">外部サイトを開きます</span>
            <h2 id="url-confirm-title">参考URLを既定ブラウザーで開きますか？</h2>
            <dl>
              <div><dt>表示文字</dt><dd>{pendingUrl.displayText}</dd></div>
              <div><dt>接続先</dt><dd>{pendingUrl.host}</dd></div>
              <div><dt>URL</dt><dd className="url-value">{pendingUrl.url}</dd></div>
            </dl>
            <p className="url-open-note">この資料はKnowledgeAppの外で開きます。表示内容、JavaScript、ダウンロードは会社のブラウザーとネットワークのルールに従います。</p>
            {urlError && <p className="url-open-error" role="alert">{urlError}</p>}
            <div className="dialog-actions">
              <button type="button" className="button ghost" onClick={() => setPendingUrl(null)} disabled={openingUrl}>キャンセル</button>
              <button type="button" className="button primary" onClick={() => void openPendingUrl()} disabled={openingUrl}>
                {openingUrl ? "開いています…" : "既定ブラウザーで開く"}
              </button>
            </div>
          </section>
        </div>
      )}
    </div>
  );
}
