import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const maximumBytes = 16 * 1024 * 1024;
const requestId = "00000000-0000-4000-8000-000000000016";
type Request = { id: string; command: string; args: Record<string, unknown> };

describe("C# bridge preflight limits", () => {
  let invokeCSharp: typeof import("./csharpBridge").invokeCSharp;
  let listener: ((event: MessageEvent) => void) | undefined;
  const postMessage = vi.fn<(message: unknown) => void>();
  const addEventListener = vi.fn((_name: string, next: (event: MessageEvent) => void) => { listener = next; });

  beforeEach(async () => {
    vi.resetModules();
    vi.useFakeTimers();
    listener = undefined;
    postMessage.mockReset();
    addEventListener.mockClear();
    vi.spyOn(crypto, "randomUUID").mockReturnValue(requestId);
    vi.stubGlobal("chrome", { webview: { postMessage, addEventListener } });
    vi.stubGlobal("__KNOWLEDGE_CSHARP_BRIDGE__", true);
    ({ invokeCSharp } = await import("./csharpBridge"));
  });

  afterEach(() => {
    vi.clearAllTimers();
    vi.useRealTimers();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    vi.resetModules();
  });

  function completeLast() {
    const request = postMessage.mock.lastCall?.[0] as Request;
    listener?.(new MessageEvent("message", { data: { id: request.id, ok: true, result: "saved" } }));
  }

  function sizedText(bytes: number, unit = "a", command = "save_article") {
    const overhead = new TextEncoder().encode(JSON.stringify({ id: requestId, command, args: { text: "" } })).byteLength;
    const unitBytes = new TextEncoder().encode(JSON.stringify(unit).slice(1, -1)).byteLength;
    const available = bytes - overhead;
    return unit.repeat(Math.floor(available / unitBytes)) + "a".repeat(available % unitBytes);
  }

  async function expectRejected(command: string, args: Record<string, unknown>, code: string) {
    await expect(invokeCSharp(command, args)).rejects.toMatchObject({ code });
    expect(postMessage).not.toHaveBeenCalled();
    expect(addEventListener).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);
    await vi.advanceTimersByTimeAsync(30_001);
    expect(vi.getTimerCount()).toBe(0);
  }

  it.each([maximumBytes - 1, maximumBytes])("accepts an ASCII envelope of exactly %i bytes", async (bytes) => {
    const promise = invokeCSharp("save_article", { text: sizedText(bytes) });
    expect(postMessage).toHaveBeenCalledTimes(1);
    expect(new TextEncoder().encode(JSON.stringify(postMessage.mock.lastCall?.[0])).byteLength).toBe(bytes);
    completeLast();
    await expect(promise).resolves.toBe("saved");
    expect(vi.getTimerCount()).toBe(0);
  });

  it.each(["a", "日", "😀", "\n"])("rejects the first UTF-8 byte above the envelope limit: %j", async (unit) => {
    const args = { text: sizedText(maximumBytes + 1, unit) };
    expect(new TextEncoder().encode(JSON.stringify({ id: requestId, command: "save_article", args })).byteLength)
      .toBe(maximumBytes + 1);
    await expectRejected("save_article", args, "SYS-002");
  });

  it.each(["日", "😀"])("accepts the UTF-8 boundary including multibyte text: %j", async (unit) => {
    const promise = invokeCSharp("save_article", { text: sizedText(maximumBytes, unit) });
    expect(postMessage).toHaveBeenCalledTimes(1);
    completeLast();
    await expect(promise).resolves.toBe("saved");
  });

  it("also rejects an oversized no-timeout command without leaving an infinite pending request", async () => {
    await expectRejected("import_json", { text: sizedText(maximumBytes + 1, "a", "import_json") }, "SYS-002");
  });

  it("allows a 10 MiB image represented as Base64 within the request envelope", async () => {
    const base64 = "A".repeat(4 * Math.ceil(10 * 1024 * 1024 / 3));
    const promise = invokeCSharp("stage_article_image_bytes", { originalName: "synthetic.png", base64 });
    expect(postMessage).toHaveBeenCalledTimes(1);
    completeLast();
    await expect(promise).resolves.toBe("saved");
  });

  it.each([144, 145])("checks overall object/array depth %i including the envelope", async (depth) => {
    let input: unknown = "leaf";
    for (let index = 0; index < depth - 2; index += 1) input = index % 2 ? { child: input } : [input];
    if (depth > 144) {
      await expectRejected("save_article", { input }, "SYS-003");
    } else {
      const promise = invokeCSharp("save_article", { input });
      expect(postMessage).toHaveBeenCalledTimes(1);
      completeLast();
      await expect(promise).resolves.toBe("saved");
    }
  });

  it("does not count quoted brackets, escaped quotes or backslashes as nesting", async () => {
    const text = ('{["\\'.repeat(200)) + "}]}";
    const promise = invokeCSharp("save_article", { text });
    expect(postMessage).toHaveBeenCalledWith({ id: requestId, command: "save_article", args: { text } });
    completeLast();
    await expect(promise).resolves.toBe("saved");
  });

  it("posts only the checked JSON snapshot and does not mutate the editor object", async () => {
    const toJSON = vi.fn().mockReturnValueOnce({ text: "checked" }).mockReturnValue({ text: "unchecked" });
    const args = Object.freeze({ input: Object.freeze({ toJSON }) });
    const promise = invokeCSharp("save_article", args);
    expect(toJSON).toHaveBeenCalledTimes(1);
    expect(postMessage).toHaveBeenCalledWith({ id: requestId, command: "save_article", args: { input: { text: "checked" } } });
    expect(args.input.toJSON).toBe(toJSON);
    completeLast();
    await expect(promise).resolves.toBe("saved");
  });

  it.each(["cyclic", "bigint", "throwing"])("returns a fixed error before registering a non-serializable %s request", async (kind) => {
    const args: Record<string, unknown> = {};
    if (kind === "cyclic") args.input = args;
    if (kind === "bigint") args.input = 1n;
    if (kind === "throwing") args.input = { toJSON: () => { throw new Error("private synthetic detail"); } };
    await expectRejected("save_article", args, "SYS-001");
  });

  it("accepts a corrected retry after a local rejection and does not retain a pending entry", async () => {
    await expectRejected("save_article", { text: "a".repeat(maximumBytes) }, "SYS-002");
    const promise = invokeCSharp("save_article", { text: "修正した合成本文" });
    expect(postMessage).toHaveBeenCalledTimes(1);
    expect(vi.getTimerCount()).toBe(1);
    completeLast();
    await expect(promise).resolves.toBe("saved");
    expect(vi.getTimerCount()).toBe(0);
  });
});
