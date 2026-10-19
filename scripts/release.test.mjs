import test from "node:test";
import assert from "node:assert/strict";
import { analyzeCommits } from "@semantic-release/commit-analyzer";
import config from "../release.config.mjs";
import { assetNames, createManifest } from "./release-assets.mjs";
import { mkdirSync, writeFileSync, readFileSync, rmSync } from "node:fs";
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
test("exige um instalador por sistema e valida a versão", () => {
  assert.deepEqual(assetNames("1.2.3"), [
    "EspacoLeve-1.2.3-win-x64.exe",
    "EspacoLeve-1.2.3-osx-x64.dmg",
    "EspacoLeve-1.2.3-osx-arm64.dmg",
    "EspacoLeve-1.2.3-linux-x64.tar.gz"
  ]);
  assert(!assetNames("1.2.3").some(name => name.endsWith(".zip")));
  assert.throws(() => assetNames("../invalid"));
});
test("publica só instaladores verificados e inclui suas somas de integridade", () => {
  const directory = path.resolve("work/release-tests", randomUUID());
  mkdirSync(directory, { recursive: true });
  for (const name of assetNames("1.2.3").filter(name => name !== "EspacoLeve-1.2.3-osx-arm64.dmg"))
    writeFileSync(path.join(directory, name), "fixture");
  assert.throws(() => createManifest(directory, "1.2.3"), /quatro instaladores/);
  const image = "EspacoLeve-1.2.3-osx-arm64.dmg";
  writeFileSync(path.join(directory, image), "disk-image-fixture");
  createManifest(directory, "1.2.3");
  const sums = readFileSync(path.join(directory, "SHA256SUMS.txt"), "utf8").split("\n");
  assert(sums.includes(createHash("sha256").update("disk-image-fixture").digest("hex") + "  " + image));
  assert.equal(sums.filter(Boolean).length, 4);
  createManifest(directory, "1.2.3");
  for (const extra of ["EspacoLeve-1.2.3-win-x64.zip", "EspacoLeve-1.2.3-osx-arm64.zip", "unexpected.exe", "unexpected.dmg", "unexpected.tar.gz"]) {
    writeFileSync(path.join(directory, extra), "fixture");
    assert.throws(() => createManifest(directory, "1.2.3"), /sem outros anexos/);
    rmSync(path.join(directory, extra));
  }
});
