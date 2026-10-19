import semanticRelease from "semantic-release";
import { appendFileSync } from "node:fs";
import config from "../release.config.mjs";

const result = await semanticRelease({ ...config, dryRun: true, ci: true });
const version = result ? result.nextRelease.version : "";
if (process.env.GITHUB_OUTPUT)
  appendFileSync(process.env.GITHUB_OUTPUT, "version=" + version + "\n");
console.log(version ? "Versão planejada: " + version : "Nenhuma versão necessária.");
