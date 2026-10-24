import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { resolve, extname, sep } from "node:path";
const root = resolve(import.meta.dirname);
const types = {
  ".html": "text/html",
  ".css": "text/css",
  ".js": "text/javascript",
  ".svg": "image/svg+xml",
  ".md": "text/plain",
};
createServer(async (req, res) => {
  try {
    const pathname = decodeURIComponent(
      new URL(req.url, "http://localhost").pathname,
    );
    const file = resolve(
      root,
      "." + (pathname === "/" ? "/index.html" : pathname),
    );
    if (!file.startsWith(root + sep)) {
      res.writeHead(403).end();
      return;
    }
    const body = await readFile(file);
    res
      .writeHead(200, {
        "Content-Type": `${types[extname(file)] || "application/octet-stream"}; charset=utf-8`,
        "Cache-Control": "no-store",
      })
      .end(body);
  } catch {
    res.writeHead(404).end("Not found");
  }
}).listen(5173, "127.0.0.1", () =>
  console.log("Panel+ prototype: http://localhost:5173"),
);
