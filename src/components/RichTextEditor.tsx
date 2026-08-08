import { useEffect } from "react";
import { EditorContent, useEditor, type JSONContent } from "@tiptap/react";
import StarterKit from "@tiptap/starter-kit";
import { TableKit } from "@tiptap/extension-table";

const EMPTY_DOCUMENT: JSONContent = {
  type: "doc",
  content: [{ type: "paragraph" }],
};

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
];

export function stripLinksFromPastedHtml(html: string): string {
  const pastedDocument = new DOMParser().parseFromString(html, "text/html");
  pastedDocument.querySelectorAll("a").forEach((anchor) => {
    anchor.replaceWith(...Array.from(anchor.childNodes));
  });
  return pastedDocument.body.innerHTML;
}

export function RichTextEditor({
  value,
  onChange,
  disabled = false,
}: {
  value?: Record<string, unknown>;
  onChange: (document: Record<string, unknown>) => void;
  disabled?: boolean;
}) {
  const editor = useEditor({
    extensions,
    content: (value as JSONContent | undefined) ?? EMPTY_DOCUMENT,
    editable: !disabled,
    immediatelyRender: false,
    editorProps: {
      attributes: {
        class: "rich-editor-content",
        "aria-label": "FAQの回答",
      },
      transformPastedHTML: stripLinksFromPastedHtml,
    },
    onUpdate: ({ editor: currentEditor }) => {
      onChange(currentEditor.getJSON() as Record<string, unknown>);
    },
  });

  useEffect(() => {
    if (!editor || !value) return;
    const current = JSON.stringify(editor.getJSON());
    const next = JSON.stringify(value);
    if (current !== next) editor.commands.setContent(value as JSONContent);
  }, [editor, value]);

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

  return (
    <div className="rich-editor">
      <div className="editor-toolbar" aria-label="回答の書式">
        <button type="button" onClick={() => editor.chain().focus().toggleHeading({ level: 2 }).run()} className={editor.isActive("heading", { level: 2 }) ? "active" : ""}>見出し</button>
        <button type="button" onClick={() => editor.chain().focus().toggleBold().run()} className={editor.isActive("bold") ? "active" : ""}><strong>太字</strong></button>
        <button type="button" onClick={() => editor.chain().focus().toggleItalic().run()} className={editor.isActive("italic") ? "active" : ""}><em>斜体</em></button>
        <button type="button" onClick={() => editor.chain().focus().toggleBulletList().run()} className={editor.isActive("bulletList") ? "active" : ""}>箇条書き</button>
        <button type="button" onClick={() => editor.chain().focus().toggleOrderedList().run()} className={editor.isActive("orderedList") ? "active" : ""}>番号付き</button>
        <button type="button" onClick={() => editor.chain().focus().insertTable({ rows: 3, cols: 3, withHeaderRow: true }).run()}>表を追加</button>
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

export function RichTextViewer({ value }: { value: Record<string, unknown> }) {
  const editor = useEditor({
    extensions,
    content: value as JSONContent,
    editable: false,
    immediatelyRender: false,
    editorProps: { attributes: { class: "rich-viewer-content" } },
  });

  useEffect(() => {
    if (editor) editor.commands.setContent(value as JSONContent);
  }, [editor, value]);

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
