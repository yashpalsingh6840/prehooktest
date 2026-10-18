import type { PackageComponent } from "../api/client";

/** Mermaid node ids can't contain spaces/special chars -- sanitize and make
 * sure the result never starts with a digit. */
function sanitizeNodeId(name: string): string {
  const cleaned = name.replace(/[^A-Za-z0-9_]/g, "_");
  return /^[0-9]/.test(cleaned) ? `n${cleaned}` : cleaned || "n";
}

export function buildDataFlowChart(
  components: PackageComponent[],
  edges: { from: string; to: string }[],
): string {
  const idByName = new Map<string, string>();
  const usedIds = new Set<string>();
  for (const c of components) {
    let id = sanitizeNodeId(c.name);
    let suffix = 2;
    while (usedIds.has(id)) {
      id = `${sanitizeNodeId(c.name)}_${suffix++}`;
    }
    usedIds.add(id);
    idByName.set(c.name, id);
  }

  const lines = ["flowchart LR"];
  for (const c of components) {
    const id = idByName.get(c.name)!;
    const label = c.name.replace(/"/g, "'");
    lines.push(`  ${id}["${label}"]`);
  }
  for (const e of edges) {
    const fromId = idByName.get(e.from);
    const toId = idByName.get(e.to);
    if (fromId && toId) lines.push(`  ${fromId} --> ${toId}`);
  }
  return lines.join("\n");
}
