import type { AppError } from "../types/domain";

interface CSharpBridgeResponse {
  id: string;
  ok: boolean;
  result?: unknown;
  error?: AppError | null;
}

interface PendingRequest {
  resolve: (value: unknown) => void;
  reject: (reason: AppError) => void;
  timeout: ReturnType<typeof window.setTimeout> | null;
}

interface CSharpBridgeWindow extends Window {
  __KNOWLEDGE_CSHARP_BRIDGE__?: boolean;
  chrome?: {
    webview?: {
      postMessage: (message: unknown) => void;
      addEventListener: (name: "message", listener: (event: MessageEvent) => void) => void;
    };
  };
}

const pending = new Map<string, PendingRequest>();
let listening = false;

// Keep these aligned with HostRequestEnvelope. The host independently rejects
// out-of-range/untrusted input; this preflight only gives our own UI feedback.
const maximumMessageBytes = 16 * 1024 * 1024;
const maximumMessageDepth = 144;

function prepareRequest(id: string, command: string, args: Record<string, unknown>) {
  let json: string;
  try {
    json = JSON.stringify({ id, command, args });
  } catch {
    throw {
      code: "SYS-001",
      message: "送信する内容を処理できませんでした。",
      action: "入力内容は保存されていません。内容を控えてから、構造や入力形式を確認してください。",
    } satisfies AppError;
  }

  if (json.length > maximumMessageBytes || new TextEncoder().encode(json).byteLength > maximumMessageBytes) {
    throw {
      code: "SYS-002",
      message: "送信する内容が1回の処理上限（16 MiB）を超えています。",
      action: "入力内容は保存されていません。画面を閉じずに内容を控え、FAQを分割するなどして量を減らしてから、もう一度実行してください。",
    } satisfies AppError;
  }

  // Scan the serialized JSON without recursive traversal or counting brackets
  // inside text. The root object counts as one container, just as in .NET.
  let depth = 0;
  let inString = false;
  let escaped = false;
  for (let index = 0; index < json.length; index += 1) {
    const character = json[index];
    if (inString) {
      if (escaped) escaped = false;
      else if (character === "\\") escaped = true;
      else if (character === '"') inString = false;
    } else if (character === '"') {
      inString = true;
    } else if (character === "{" || character === "[") {
      depth += 1;
      if (depth > maximumMessageDepth) {
        throw {
          code: "SYS-003",
          message: "送信する内容の入れ子が深すぎます。",
          action: "入力内容は保存されていません。画面を閉じずに内容を控え、箇条書きなどの入れ子を減らしてから、もう一度実行してください。",
        } satisfies AppError;
      }
    } else if (character === "}" || character === "]") {
      depth -= 1;
    }
  }

  // Post the checked snapshot, not the original mutable/toJSON-bearing object.
  // This has the same JSON semantics as WebView2's normal object transport.
  return JSON.parse(json) as { id: string; command: string; args: Record<string, unknown> };
}

// A file picker is waiting for the user, not for a timed background operation.
// File work cannot be cancelled by rejecting this promise: keep the UI busy
// until the host reports the real outcome instead of allowing a false retry.
const hostCompletionCommands = new Set([
  "select_article_image",
  "select_faq_csv_export_path",
  "select_faq_csv_import_path",
  "select_json_export_path",
  "select_json_import_path",
  "select_full_backup_destination",
  "select_restore_backup_source",
  "create_full_backup",
  "inspect_backup",
  "restore_backup",
  "export_faq_csv",
  "import_faq_csv",
  "inspect_faq_csv",
  "export_json",
  "import_json",
  "inspect_json",
  "list_codex_proposals",
  "accept_codex_proposal",
  "reject_codex_proposal",
  "reopen_rejected_codex_proposal",
  "create_codex_delegation",
  "mark_codex_merge_sources",
  "clear_article_merge",
  "save_tag",
  "issue_recovery_key",
  "verify_recovery_key",
  "complete_password_recovery",
  "skip_recovery_setup",
  "save_connection_settings",
  "change_codex_location",
  "select_storage_folder", "save_storage_folder", "check_storage_folder", "set_user_role",
]);

function clearRequestTimeout(request: PendingRequest) {
  if (request.timeout !== null) window.clearTimeout(request.timeout);
}

function bridgeWindow() {
  return window as CSharpBridgeWindow;
}

export function hasCSharpBridge() {
  const current = bridgeWindow();
  return current.__KNOWLEDGE_CSHARP_BRIDGE__ === true && Boolean(current.chrome?.webview);
}

function isBridgeResponse(value: unknown): value is CSharpBridgeResponse {
  if (!value || typeof value !== "object") return false;
  const candidate = value as Record<string, unknown>;
  return typeof candidate.id === "string" && typeof candidate.ok === "boolean";
}

function installListener() {
  if (listening) return;
  const webview = bridgeWindow().chrome?.webview;
  if (!webview) return;
  listening = true;
  webview.addEventListener("message", (event) => {
    let data: unknown = event.data;
    if (typeof data === "string") {
      try { data = JSON.parse(data) as unknown; }
      catch { return; }
    }
    if (!isBridgeResponse(data)) return;
    const request = pending.get(data.id);
    if (!request) return;
    pending.delete(data.id);
    clearRequestTimeout(request);
    if (data.ok) {
      request.resolve(data.result);
    } else {
      request.reject(data.error ?? {
        code: "SYS-001",
        message: "C#処理から応答を受け取れませんでした。",
        action: "アプリを再起動してください。解決しない場合は診断情報を確認してください。",
      });
    }
  });
}

export function invokeCSharp<T>(command: string, args?: Record<string, unknown>): Promise<T> {
  const webview = bridgeWindow().chrome?.webview;
  if (!hasCSharpBridge() || !webview) {
    return Promise.reject({
      code: "SYS-001",
      message: "C#連携を利用できません。",
      action: "C#移行試作版を再起動してください。",
    } satisfies AppError);
  }

  const id = typeof crypto.randomUUID === "function"
    ? crypto.randomUUID()
    : `csharp-${Date.now()}-${Math.random().toString(16).slice(2)}`;
  let message: ReturnType<typeof prepareRequest>;
  try {
    message = prepareRequest(id, command, args ?? {});
  } catch (error) {
    return Promise.reject(error);
  }

  installListener();
  return new Promise<T>((resolve, reject) => {
    const timeout = hostCompletionCommands.has(command) ? null : window.setTimeout(() => {
      pending.delete(id);
      reject({
        code: "SYS-001",
        message: "C#処理が時間内に完了しませんでした。",
        action: "アプリを再起動してください。解決しない場合は診断情報を確認してください。",
      } satisfies AppError);
    }, 30_000);
    pending.set(id, {
      resolve: (value) => resolve(value as T),
      reject,
      timeout,
    });
    try {
      webview.postMessage(message);
    } catch {
      const request = pending.get(id);
      if (!request) return;
      pending.delete(id);
      clearRequestTimeout(request);
      reject({
        code: "SYS-001",
        message: "C#処理へ要求を送信できませんでした。",
        action: "処理結果を確認し、解決しない場合はアプリを再起動してください。",
      } satisfies AppError);
    }
  });
}
