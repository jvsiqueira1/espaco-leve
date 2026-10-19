import test from "node:test";
import assert from "node:assert/strict";
import { analyzeCommits } from "@semantic-release/commit-analyzer";
import config from "../release.config.mjs";
import { assetNames, createManifest } from "./release-assets.mjs";
import { mkdirSync, writeFileSync, readFileSync } from "node:fs";
import { randomUUID, createHash } from "node:crypto";
import path from "node:path";
const logger = { log() {} };
for (const [message, expected] of [
  ["fix: corrige detecção de cache", "patch"],
  ["feat: identifica cache uv", "minor"],
  ["feat: altera configuração\n\nBREAKING CHANGE: novo formato obrigatório", "major"],
  ["docs: atualiza instruções", null]
]) test(message.split("\n")[0], async () => {
  assert.equal(await analyzeCommits({}, { commits: [{ hash: "fixture", message }], logger }), expected);
});
test("publica apenas no main e sem npm ou commits automáticos", () => {
  assert.deepEqual(config.branches, ["main"]);
  assert(!config.plugins.some(p => /@semantic-release\/(?:npm|git)$/.test(Array.isArray(p) ? p[0] : p)));
});
test("exige pacotes para todos os sistemas e valida a versão", () => {
  assert.equal(assetNames("1.2.3").length, 5);
  assert(assetNames("1.2.3").includes("EspacoLeve-1.2.3-osx-arm64.zip"));
  assert(assetNames("1.2.3").includes("EspacoLeve-1.2.3-win-x64.exe"));
  assert.throws(() => assetNames("../invalid"));
});
test("bloqueia release sem executável e inclui sua soma de integridade", () => {
  const directory = path.resolve("work/release-tests", randomUUID());
  mkdirSync(directory, { recursive: true });
  for (const name of assetNames("1.2.3").filter(name => !name.endsWith(".exe")))
    writeFileSync(path.join(directory, name), "fixture");
  assert.throws(() => createManifest(directory, "1.2.3"), /executável Windows/);
  const executable = "EspacoLeve-1.2.3-win-x64.exe";
  writeFileSync(path.join(directory, executable), "standalone-fixture");
  createManifest(directory, "1.2.3");
  const expected = createHash("sha256").update("standalone-fixture").digest("hex") + "  " + executable;
  assert(readFileSync(path.join(directory, "SHA256SUMS.txt"), "utf8").split("\n").includes(expected));
  writeFileSync(path.join(directory, "unexpected.exe"), "fixture");
  assert.throws(() => createManifest(directory, "1.2.3"), /executável Windows/);
});
