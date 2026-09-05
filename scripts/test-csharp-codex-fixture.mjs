import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { lstatSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { getSchema } from "@tiptap/core";
import StarterKit from "@tiptap/starter-kit";

// Build RichCompatibilityCheck first. This test accepts no file/data paths:
// only the freshly generated synthetic JSON from the fixed test executable.
if (process.argv.length !== 2) throw new Error("No arguments are accepted.");
const repository = fileURLToPath(new URL("../", import.meta.url));
const generated = spawnSync("dotnet", ["run", "--project", "src-csharp/KnowledgeApp.RichCompatibilityCheck",
  "--configuration", "Release", "--no-build", "--no-restore", "--", "--write-codex-depth-fixture"], {
  cwd: repository, encoding: "utf8", windowsHide: true, maxBuffer: 1024 * 1024,
});
if (generated.error || generated.status !== 0) {
  process.stderr.write(generated.stderr ?? "");
  throw new Error("Synthetic fixture generator/checks failed.");
}
const fixturePath = generated.stdout.match(/^SYNTHETIC_MANUAL_JSON=(.+)$/m)?.[1].trim();
const expectedHash = generated.stdout.match(/^SHA256=([a-f0-9]{64})/m)?.[1];
if (!fixturePath || !expectedHash) throw new Error("No verified synthetic output reported.");
const absolute = path.resolve(fixturePath);
const parent = path.dirname(absolute);
if (path.basename(absolute) !== "Synthetic_codex_deep_document.knowledge-export.json" ||
    !/^knowledgeapp-data-check-[a-f0-9-]{36}$/.test(path.basename(parent)) ||
    path.resolve(path.dirname(parent)).toLowerCase() !== path.resolve(tmpdir()).toLowerCase()) {
  throw new Error("Fixture is not in an owned temporary output directory.");
}
const directoryStat = lstatSync(parent);
const stat = lstatSync(absolute);
if (!directoryStat.isDirectory() || directoryStat.isSymbolicLink() || !stat.isFile() || stat.isSymbolicLink() || stat.size > 1024 * 1024) {
  throw new Error("Unsafe synthetic fixture output.");
}
const bytes = readFileSync(absolute);
if (createHash("sha256").update(bytes).digest("hex") !== expectedHash) throw new Error("Fixture hash mismatch.");
const fixture = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
if (fixture.articles.length !== 1 || fixture.categories.length !== 1) throw new Error("Unexpected synthetic counts.");
const body = fixture.articles[0].bodyDoc;
const document = getSchema([StarterKit]).nodeFromJSON(body);
document.check();
if (!document.textContent.includes("実プラグインへは送信しません。")) throw new Error("Synthetic marker missing.");
console.log("PASS: C# generated manual fixture also validates against the actual Tiptap/ProseMirror schema.");
console.log(`SYNTHETIC_MANUAL_JSON=${absolute}`);
console.log(`SHA256=${expectedHash}`);
console.log("The output is synthetic only and retained outside the repository for manual handoff.");
