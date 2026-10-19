import { createHash } from "node:crypto";
import { readdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";

export const runtimes = ["win-x64", "osx-x64", "osx-arm64", "linux-x64"];
export function assetNames(version) {
  if (!/^\d+\.\d+\.\d+$/.test(version)) throw new Error("Versão inválida: " + version);
  return [
    ...runtimes.map(rid => "EspacoLeve-" + version + "-" + rid + (rid === "linux-x64" ? ".tar.gz" : ".zip")),
    "EspacoLeve-" + version + "-win-x64.exe"
  ];
}
export function prepare(_config, { nextRelease }) {
  if (process.env.EXPECTED_VERSION !== nextRelease.version)
    throw new Error("A versão mudou entre o planejamento e a publicação.");
  createManifest(path.resolve("artifacts/release"), nextRelease.version);
}
export function createManifest(directory, version) {
  const expected = assetNames(version);
  const present = readdirSync(directory).filter(name => name.endsWith(".zip") || name.endsWith(".tar.gz") || name.endsWith(".exe"));
  if (expected.some(name => !present.includes(name)) || present.some(name => !expected.includes(name)))
    throw new Error("Os quatro pacotes e o executável Windows verificados são obrigatórios para publicar.");
  const sums = expected.map(name => createHash("sha256").update(readFileSync(path.join(directory, name))).digest("hex") + "  " + name);
  writeFileSync(path.join(directory, "SHA256SUMS.txt"), sums.join("\n") + "\n");
}
