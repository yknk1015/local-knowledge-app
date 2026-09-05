import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { AppError } from "../types/domain";

interface BridgeRequest {
  id: string;
  command: string;
  args: Record<string, unknown>;
}

interface HostResponse {
  id: string;
  ok: boolean;
  result?: unknown;
  error?: AppError | null;
}

const dialogCommands = [
  "select_article_image",
  "select_faq_csv_export_path",
  "select_faq_csv_import_path",
  "select_json_export_path",
  "select_json_import_path",
  "select_full_backup_destination",
  "select_restore_backup_source",
] as const;

const longRunningIoCommands = [
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
] as const;

const hostError: AppError = {
  code: "BK-008",
  message: "Git管理フォルダは使用できません。",
  action: "リポジトリ外の保存先を選んでください。",
};

function observe<T>(promise: Promise<T>) {
  const resolved = vi.fn<(value: T) => void>();
  const rejected = vi.fn<(reason: unknown) => void>();
  const completion = promise.then(resolved, rejected);
  return { resolved, rejected, completion };
}

function expectPending(observation: ReturnType<typeof observe>) {
  expect(observation.resolved).not.toHaveBeenCalled();
  expect(observation.rejected).not.toHaveBeenCalled();
}

describe("C# bridge timeout policy", () => {
  let invokeCSharp: typeof import("./csharpBridge").invokeCSharp;
  let listener: ((event: MessageEvent) => void) | undefined;
  let requests: BridgeRequest[];
  let chromeDescriptor: PropertyDescriptor | undefined;
  let flagDescriptor: PropertyDescriptor | undefined;
  const postMessage = vi.fn((message: unknown) => {
    requests.push(message as BridgeRequest);
  });
  const addEventListener = vi.fn((_name: "message", next: (event: MessageEvent) => void) => {
    listener = next;
  });

  function respond(response: HostResponse, serialized = false) {
    expect(listener).toBeDefined();
    listener?.(new MessageEvent("message", {
      data: serialized ? JSON.stringify(response) : response,
    }));
  }

  function requestAt(index = 0) {
    const request = requests[index];
    expect(request).toBeDefined();
    return request!;
  }

  async function waitPastOrdinaryTimeout(observation: ReturnType<typeof observe>) {
    await vi.advanceTimersByTimeAsync(30_001);
    expectPending(observation);
    await vi.advanceTimersByTimeAsync(5 * 60_000);
    expectPending(observation);
    expect(vi.getTimerCount()).toBe(0);
  }

  beforeEach(async () => {
    vi.resetModules();
    vi.useFakeTimers();
    requests = [];
    listener = undefined;
    postMessage.mockReset();
    postMessage.mockImplementation((message: unknown) => {
      requests.push(message as BridgeRequest);
    });
    addEventListener.mockClear();
    chromeDescriptor = Object.getOwnPropertyDescriptor(window, "chrome");
    flagDescriptor = Object.getOwnPropertyDescriptor(window, "__KNOWLEDGE_CSHARP_BRIDGE__");
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: { postMessage, addEventListener } },
    });
    Object.defineProperty(window, "__KNOWLEDGE_CSHARP_BRIDGE__", {
      configurable: true,
      value: true,
    });
    ({ invokeCSharp } = await import("./csharpBridge"));
  });

  afterEach(() => {
    if (chromeDescriptor) Object.defineProperty(window, "chrome", chromeDescriptor);
    else Reflect.deleteProperty(window, "chrome");
    if (flagDescriptor) Object.defineProperty(window, "__KNOWLEDGE_CSHARP_BRIDGE__", flagDescriptor);
    else Reflect.deleteProperty(window, "__KNOWLEDGE_CSHARP_BRIDGE__");
    vi.clearAllTimers();
    vi.useRealTimers();
    vi.restoreAllMocks();
    vi.resetModules();
  });

  it.each(dialogCommands)("waits for a delayed selection from %s", async (command) => {
    const observation = observe(invokeCSharp(command));
    await waitPastOrdinaryTimeout(observation);

    const selectedPath = "C:\\SyntheticTest\\selected-file";
    respond({ id: requestAt().id, ok: true, result: selectedPath }, true);
    await observation.completion;
    expect(observation.resolved).toHaveBeenCalledExactlyOnceWith(selectedPath);
    expect(observation.rejected).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);
  });

  it.each(dialogCommands)("preserves cancellation after a long wait in %s", async (command) => {
    const observation = observe(invokeCSharp(command));
    await waitPastOrdinaryTimeout(observation);

    respond({ id: requestAt().id, ok: true, result: null });
    await observation.completion;
    expect(observation.resolved).toHaveBeenCalledExactlyOnceWith(null);
    expect(observation.rejected).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);
  });

  it.each(dialogCommands)("preserves the host validation error after a long wait in %s", async (command) => {
    const observation = observe(invokeCSharp(command));
    await waitPastOrdinaryTimeout(observation);

    respond({ id: requestAt().id, ok: false, error: hostError });
    await observation.completion;
    expect(observation.resolved).not.toHaveBeenCalled();
    expect(observation.rejected).toHaveBeenCalledExactlyOnceWith(hostError);
    expect(vi.getTimerCount()).toBe(0);
  });

  it.each(longRunningIoCommands)("waits for the actual successful completion of %s", async (command) => {
    const observation = observe(invokeCSharp(command, { input: { path: "C:\\SyntheticTest\\backup.faqbackup" } }));
    await waitPastOrdinaryTimeout(observation);

    const result = { path: "C:\\SyntheticTest\\backup.faqbackup", articleCount: 2 };
    respond({ id: requestAt().id, ok: true, result });
    await observation.completion;
    expect(observation.resolved).toHaveBeenCalledExactlyOnceWith(result);
    expect(observation.rejected).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);
  });

  it.each(longRunningIoCommands)("preserves a delayed I/O error from %s", async (command) => {
    const observation = observe(invokeCSharp(command));
    await waitPastOrdinaryTimeout(observation);

    respond({ id: requestAt().id, ok: false, error: hostError }, true);
    await observation.completion;
    expect(observation.resolved).not.toHaveBeenCalled();
    expect(observation.rejected).toHaveBeenCalledExactlyOnceWith(hostError);
    expect(vi.getTimerCount()).toBe(0);
  });

  it("keeps the exact 30-second deadline for an ordinary request", async () => {
    const observation = observe(invokeCSharp("get_article", { id: "synthetic-article" }));
    expect(vi.getTimerCount()).toBe(1);
    await vi.advanceTimersByTimeAsync(29_999);
    expectPending(observation);
    await vi.advanceTimersByTimeAsync(1);
    await observation.completion;

    expect(observation.rejected).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({
      code: "SYS-001",
      message: "C#処理が時間内に完了しませんでした。",
    }));
    expect(observation.resolved).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);
  });

  it.each(["select_article_image_extra", "create_full_backup_extra"])(
    "does not broaden the timeout exemption to a command prefix: %s",
    async (command) => {
      const observation = observe(invokeCSharp(command));
      await vi.advanceTimersByTimeAsync(30_000);
      await observation.completion;
      expect(observation.rejected).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ code: "SYS-001" }));
      expect(vi.getTimerCount()).toBe(0);
    },
  );

  it("keeps a concurrent dialog independent of an ordinary request timeout", async () => {
    const dialog = observe(invokeCSharp("select_full_backup_destination"));
    const ordinary = observe(invokeCSharp("get_article", { id: "synthetic-article" }));
    expect(requestAt(0).id).not.toBe(requestAt(1).id);
    expect(addEventListener).toHaveBeenCalledTimes(1);
    expect(vi.getTimerCount()).toBe(1);

    await vi.advanceTimersByTimeAsync(60_000);
    expectPending(dialog);
    expect(ordinary.rejected).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ code: "SYS-001" }));
    respond({ id: requestAt(1).id, ok: true, result: "late-ordinary-result" });
    respond({ id: requestAt(0).id, ok: true, result: "selected-backup" });
    await Promise.all([dialog.completion, ordinary.completion]);

    expect(ordinary.resolved).not.toHaveBeenCalled();
    expect(dialog.resolved).toHaveBeenCalledExactlyOnceWith("selected-backup");
    expect(dialog.rejected).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);
  });

  it("matches out-of-order responses by request ID and ignores unknown and duplicate responses", async () => {
    const first = observe(invokeCSharp("select_article_image"));
    const second = observe(invokeCSharp("select_article_image"));
    const third = observe(invokeCSharp("get_article"));
    expect(new Set(requests.map(({ id }) => id)).size).toBe(3);

    respond({ id: "unknown-request", ok: false, error: hostError });
    respond({ id: requestAt(1).id, ok: true, result: "second-image" });
    respond({ id: requestAt(1).id, ok: false, error: hostError });
    await second.completion;
    expectPending(first);
    expectPending(third);
    expect(second.resolved).toHaveBeenCalledExactlyOnceWith("second-image");
    expect(second.rejected).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(1);

    respond({ id: requestAt(2).id, ok: true, result: "article" });
    await third.completion;
    expect(vi.getTimerCount()).toBe(0);
    await vi.advanceTimersByTimeAsync(300_000);
    expectPending(first);
    respond({ id: requestAt(0).id, ok: true, result: "first-image" }, true);
    respond({ id: requestAt(0).id, ok: true, result: "duplicate-image" });
    await first.completion;
    expect(first.resolved).toHaveBeenCalledExactlyOnceWith("first-image");
    expect(first.rejected).not.toHaveBeenCalled();
    expect(third.resolved).toHaveBeenCalledExactlyOnceWith("article");
  });

  it("ignores a timed-out request's late response while a new request is pending", async () => {
    const first = observe(invokeCSharp("get_article"));
    await vi.advanceTimersByTimeAsync(30_000);
    await first.completion;
    const second = observe(invokeCSharp("get_article"));

    respond({ id: requestAt(0).id, ok: true, result: "expired" });
    await Promise.resolve();
    expectPending(second);
    expect(vi.getTimerCount()).toBe(1);
    respond({ id: requestAt(1).id, ok: true, result: "current" });
    await second.completion;
    expect(first.resolved).not.toHaveBeenCalled();
    expect(first.rejected).toHaveBeenCalledTimes(1);
    expect(second.resolved).toHaveBeenCalledExactlyOnceWith("current");
    expect(vi.getTimerCount()).toBe(0);
  });

  it.each([true, false])("cleans up the ordinary timer when the host completes with ok=%s", async (ok) => {
    const observation = observe(invokeCSharp("get_article"));
    await vi.advanceTimersByTimeAsync(10_000);
    respond({ id: requestAt().id, ok, result: "article", error: hostError });
    await observation.completion;
    expect(vi.getTimerCount()).toBe(0);
    await vi.advanceTimersByTimeAsync(300_000);

    if (ok) {
      expect(observation.resolved).toHaveBeenCalledExactlyOnceWith("article");
      expect(observation.rejected).not.toHaveBeenCalled();
    } else {
      expect(observation.resolved).not.toHaveBeenCalled();
      expect(observation.rejected).toHaveBeenCalledExactlyOnceWith(hostError);
    }
  });

  it("returns a structured fallback error and clears the timer when the host sends no error detail", async () => {
    const observation = observe(invokeCSharp("get_article"));
    respond({ id: requestAt().id, ok: false, error: null });
    await observation.completion;
    expect(observation.rejected).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ code: "SYS-001" }));
    expect(vi.getTimerCount()).toBe(0);
  });

  it.each(["get_article", "select_article_image", "restore_backup"])(
    "cleans up and returns SYS-001 if posting %s throws",
    async (command) => {
      postMessage.mockImplementationOnce((message: unknown) => {
        requests.push(message as BridgeRequest);
        throw new Error("synthetic transport failure");
      });
      const failed = observe(invokeCSharp(command));
      await failed.completion;
      expect(failed.resolved).not.toHaveBeenCalled();
      expect(failed.rejected).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ code: "SYS-001" }));
      expect(vi.getTimerCount()).toBe(0);

      const next = observe(invokeCSharp("get_article"));
      respond({ id: requestAt(0).id, ok: true, result: "failed-send-response" });
      await Promise.resolve();
      expectPending(next);
      expect(vi.getTimerCount()).toBe(1);
      respond({ id: requestAt(1).id, ok: true, result: "next-result" });
      await next.completion;
      await vi.advanceTimersByTimeAsync(300_000);
      expect(failed.rejected).toHaveBeenCalledTimes(1);
      expect(failed.resolved).not.toHaveBeenCalled();
      expect(next.resolved).toHaveBeenCalledExactlyOnceWith("next-result");
      expect(next.rejected).not.toHaveBeenCalled();
      expect(vi.getTimerCount()).toBe(0);
    },
  );
});
