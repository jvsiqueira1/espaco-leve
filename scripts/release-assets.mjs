import { createHash } from "node:crypto";
import { readdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";

export const runtimes = ["win-x64", "osx-x64", "osx-arm64", "linux-x64"];
export function assetNames(version) {
  if (!/^\d+\.\d+\.\d+$/.test(version)) throw new Error("Versão inválida: " + version);
  return runtimes.map(rid => "EspacoLeve-" + version + "-" + rid + (rid === "linux-x64" ? ".tar.gz" : ".zip"));
}
export function prepare(_config, { nextRelease }) {
  if (process.env.EXPECTED_VERSION !== nextRelease.version)
    throw new Error("A versão mudou entre o planejamento e a publicação.");
  const directory = path.resolve("artifacts/release");
  const expected = assetNames(nextRelease.version);
  const present = readdirSync(directory).filter(name => name.endsWith(".zip") || name.endsWith(".tar.gz"));
  if (expected.some(name => !present.includes(name)) || present.some(name => !expected.includes(name)))
    throw new Error("Os quatro pacotes verificados são obrigatórios para publicar.");
  const sums = expected.map(name => createHash("sha256").update(readFileSync(path.join(directory, name))).digest("hex") + "  " + name);
  writeFileSync(path.join(directory, "SHA256SUMS.txt"), sums.join("\n") + "\n");
}
