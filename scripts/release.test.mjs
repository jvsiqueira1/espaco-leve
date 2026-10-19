import test from "node:test";
import assert from "node:assert/strict";
import { analyzeCommits } from "@semantic-release/commit-analyzer";
import config from "../release.config.mjs";
import { assetNames } from "./release-assets.mjs";
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
  assert.equal(assetNames("1.2.3").length, 4);
  assert(assetNames("1.2.3").includes("EspacoLeve-1.2.3-osx-arm64.zip"));
  assert.throws(() => assetNames("../invalid"));
});
