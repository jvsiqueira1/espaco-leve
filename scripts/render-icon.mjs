// Regenera assets/app.png e assets/app.ico a partir de assets/app.svg. Requer rsvg-convert (librsvg);
// é uma ferramenta local de design, fora do build e do CI, que usam somente os arquivos versionados.
import { spawnSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import path from "node:path";

const assets = path.resolve(import.meta.dirname, "../assets");
const source = readFileSync(path.join(assets, "app.svg"), "utf8");

function render(size, svg) {
  const result = spawnSync("rsvg-convert", ["-w", String(size), "-h", String(size), "-"], { input: svg, maxBuffer: 64 << 20 });
  if (result.error || result.status !== 0) throw new Error("Falha no rsvg-convert: " + (result.error?.message ?? result.stderr));
  return result.stdout;
}

// O .ico serve à janela, à bandeja e ao Windows: recorta a margem do Dock, sem sombra, e omite a nervura onde ela vira ruído.
function compact(size) {
  const hidden = size <= 24 ? "#shadow, #lift, #rib" : size <= 32 ? "#shadow, #lift" : "#shadow";
  return source.replace('viewBox="0 0 1024 1024"', 'viewBox="86 86 852 852"').replace("<defs>", "<style>" + hidden + " { display: none }</style><defs>");
}

writeFileSync(path.join(assets, "app.png"), render(1024, source));
const frames = [16, 24, 32, 48, 64, 128, 256].map(size => ({ size, png: render(size, compact(size)) }));
const header = Buffer.alloc(6 + 16 * frames.length);
header.writeUInt16LE(1, 2);
header.writeUInt16LE(frames.length, 4);
let offset = header.length;
frames.forEach(({ size, png }, i) => {
  const entry = 6 + 16 * i;
  header.writeUInt8(size % 256, entry);
  header.writeUInt8(size % 256, entry + 1);
  header.writeUInt16LE(1, entry + 4);
  header.writeUInt16LE(32, entry + 6);
  header.writeUInt32LE(png.length, entry + 8);
  header.writeUInt32LE(offset, entry + 12);
  offset += png.length;
});
writeFileSync(path.join(assets, "app.ico"), Buffer.concat([header, ...frames.map(frame => frame.png)]));
