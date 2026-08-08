import { useEffect, useRef } from "react";
import { convertFileSrc } from "@tauri-apps/api/core";
import Image from "@tiptap/extension-image";
import { EditorContent, useEditor, type JSONContent } from "@tiptap/react";
import StarterKit from "@tiptap/starter-kit";
import { TableKit } from "@tiptap/extension-table";

const EMPTY_DOCUMENT: JSONContent = {
  type: "doc",
  content: [{ type: "paragraph" }],
};

export interface ManagedImageSource {
  id: string;
  assetPath: string;
  altText: string;
}

const ManagedImage = Image.extend({
  addAttributes() {
    return {
      ...this.parent?.(),
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
];

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
    return path ? { ...attributes, src: convertFileSrc(path) } : attributes;
  });
}

export function dehydrateManagedImages(value: Record<string, unknown>): Record<string, unknown> {
  return transformImages(value, (attributes) => {
    const id = typeof attributes.attachmentId === "string" ? attributes.attachmentId : "";
    return id ? { ...attributes, src: `knowledge-attachment:${id}` } : attributes;
  });
}

export function stripLinksFromPastedHtml(html: string): string {
  const pastedDocument = new DOMParser().parseFromString(html, "text/html");
  pastedDocument.querySelectorAll("a").forEach((anchor) => {
    anchor.replaceWith(...Array.from(anchor.childNodes));
  });
  pastedDocument.querySelectorAll("img").forEach((image) => image.remove());
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
          src: convertFileSrc(image.assetPath),
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
        const imageFile = Array.from(event.clipboardData?.files ?? []).find((file) =>
          file.type.startsWith("image/"),
        );
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

  return (
    <div className="rich-editor">
      <div className="editor-toolbar" aria-label="回答の書式">
        <button type="button" onClick={() => editor.chain().focus().toggleHeading({ level: 2 }).run()} className={editor.isActive("heading", { level: 2 }) ? "active" : ""}>見出し</button>
        <button type="button" onClick={() => editor.chain().focus().toggleBold().run()} className={editor.isActive("bold") ? "active" : ""}><strong>太字</strong></button>
        <button type="button" onClick={() => editor.chain().focus().toggleItalic().run()} className={editor.isActive("italic") ? "active" : ""}><em>斜体</em></button>
        <button type="button" onClick={() => editor.chain().focus().toggleBulletList().run()} className={editor.isActive("bulletList") ? "active" : ""}>箇条書き</button>
        <button type="button" onClick={() => editor.chain().focus().toggleOrderedList().run()} className={editor.isActive("orderedList") ? "active" : ""}>番号付き</button>
        <button type="button" onClick={() => editor.chain().focus().insertTable({ rows: 3, cols: 3, withHeaderRow: true }).run()}>表を追加</button>
        <button type="button" onClick={() => void insertImage()} disabled={!onRequestImage}>画像を追加</button>
        {editor.isActive("image") && <button type="button" onClick={editImageAlt}>画像の説明</button>}
        {editor.isActive("image") && <button type="button" onClick={() => editor.chain().focus().deleteSelection().run()}>画像を削除</button>}
        <button type="button" onClick={setLink} className={editor.isActive("link") ? "active" : ""}>参考URL</button>
        <button type="button" onClick={() => editor.chain().focus().extendMarkRange("link").unsetLink().run()}>リンク解除</button>
        <span className="toolbar-spacer" />
        <button type="button" aria-label="元に戻す" onClick={() => editor.chain().focus().undo().run()} disabled={!editor.can().undo()}>↶</button>
        <button type="button" aria-label="やり直す" onClick={() => editor.chain().focus().redo().run()} disabled={!editor.can().redo()}>↷</button>
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

  const stopExternalNavigation = (target: EventTarget | null) => {
    if (target instanceof Element && target.closest("a")) {
      window.alert("参考URLを既定ブラウザーで開く機能は、次の開発版で利用できるようになります。");
      return true;
    }
    return false;
  };

  return (
    <div
      className="rich-viewer"
      onClickCapture={(event) => {
        if (stopExternalNavigation(event.target)) {
          event.preventDefault();
          event.stopPropagation();
        }
      }}
      onKeyDownCapture={(event) => {
        if (event.key === "Enter" && stopExternalNavigation(event.target)) {
          event.preventDefault();
          event.stopPropagation();
        }
      }}
    >
      <EditorContent editor={editor} />
    </div>
  );
}
