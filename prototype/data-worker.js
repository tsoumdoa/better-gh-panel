// The real plug-in would keep this query engine in .NET, close to Grasshopper data.
// Here a worker keeps generation, sorting, and aggregation off the browser UI thread.
let data = [],
  view = [],
  currentVersion = 0;
const types = ["Number", "Point", "Text", "Null", "Boolean"];
function generate(count) {
  data = Array.from({ length: count }, (_, id) => {
    const branch = Math.floor(id / 256),
      index = id % 256;
    const path = `{0;${Math.floor(branch / 16)};${branch % 16}}`;
    const n = ((id * 7919 + 131) % 100000) / 100;
    let type = "Number",
      value = n.toFixed(2),
      numeric = n;
    if (id % 41 === 17) {
      type = "Null";
      value = "<null>";
      numeric = null;
    } else if (id % 11 === 5) {
      type = "Boolean";
      value = id % 2 ? "True" : "False";
      numeric = null;
    } else if (id % 7 === 3) {
      type = "Text";
      value =
        [
          "Facade / north",
          "Facade / south",
          "Panel / glazed",
          "Panel / opaque",
        ][id % 4] + ` / ${index}`;
      numeric = null;
    } else if (id % 5 === 2) {
      type = "Point";
      value = `(${(n / 10).toFixed(2)}, ${(((id * 13) % 2300) / 100).toFixed(2)}, ${(index / 4).toFixed(2)})`;
      numeric = null;
    }
    return { id, branch, path, index, type, value, numeric };
  });
}
function query(options) {
  const started = performance.now();
  const search = options.search.trim().toLowerCase();
  const comparison = search.match(/^(>=|<=|>|<|=)\s*(-?\d+(?:\.\d+)?)$/);
  const counts = Object.fromEntries(types.map((t) => [t, 0]));
  const branches = new Map();
  let min = Infinity,
    max = -Infinity,
    sum = 0,
    numberCount = 0;
  view = [];
  for (const row of data) {
    if (options.branch !== null && row.branch !== options.branch) continue;
    if (options.type !== "all" && row.type !== options.type) continue;
    if (comparison) {
      if (row.numeric === null) continue;
      const target = Number(comparison[2]),
        v = row.numeric;
      if (
        !{
          ">": v > target,
          "<": v < target,
          ">=": v >= target,
          "<=": v <= target,
          "=": v === target,
        }[comparison[1]]
      )
        continue;
    } else if (
      search &&
      !`${row.value} ${row.path} ${row.type}`.toLowerCase().includes(search)
    )
      continue;
    view.push(row.id);
    counts[row.type]++;
    branches.set(row.branch, (branches.get(row.branch) || 0) + 1);
    if (row.numeric !== null) {
      min = Math.min(min, row.numeric);
      max = Math.max(max, row.numeric);
      sum += row.numeric;
      numberCount++;
    }
  }
  if (options.sort !== "source") {
    const direction = options.sort === "asc" ? 1 : -1;
    view.sort((a, b) => {
      const x = data[a],
        y = data[b];
      // Nulls last; numbers sort numerically. Mixed types are grouped, not coerced.
      if (x.type === "Null" || y.type === "Null")
        return x.type === y.type ? a - b : x.type === "Null" ? 1 : -1;
      const order = types.indexOf(x.type) - types.indexOf(y.type);
      if (order) return order;
      const cmp =
        x.numeric !== null
          ? x.numeric - y.numeric
          : x.value.localeCompare(y.value, undefined, { numeric: true });
      return cmp * direction || a - b;
    });
  }
  const histogram = new Array(20).fill(0);
  if (numberCount)
    for (const id of view) {
      const n = data[id].numeric;
      if (n !== null)
        histogram[
          Math.min(19, Math.floor(((n - min) / (max - min || 1)) * 20))
        ]++;
    }
  return {
    counts,
    branches: [...branches].map(([id, count]) => ({
      id,
      count,
      path: `{0;${Math.floor(id / 16)};${id % 16}}`,
    })),
    total: data.length,
    count: view.length,
    numberCount,
    min: numberCount ? min : null,
    max: numberCount ? max : null,
    mean: numberCount ? sum / numberCount : null,
    histogram,
    elapsed: performance.now() - started,
  };
}
function page(offset, limit) {
  return view.slice(offset, offset + limit).map((id) => data[id]);
}
self.onmessage = ({ data: message }) => {
  if (message.kind === "query") {
    if (data.length !== message.options.size) generate(message.options.size);
    currentVersion = message.version;
    const summary = query(message.options);
    self.postMessage({
      kind: "result",
      version: currentVersion,
      summary,
      rows: page(0, message.limit),
      offset: 0,
    });
  } else if (message.kind === "page" && message.version === currentVersion) {
    self.postMessage({
      kind: "page",
      version: currentVersion,
      request: message.request,
      rows: page(message.offset, message.limit),
      offset: message.offset,
    });
  }
};
