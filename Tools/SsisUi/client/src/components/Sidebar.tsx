import { Fragment, useEffect, useState } from "react";
import { Link, useLocation, useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import {
  getStepStatus,
  getValidation,
  getWalkthrough,
  type StepKey,
  type Workspace,
} from "../api/client";
import NavIcon, { type IconName } from "./NavIcon";
import { isClean } from "../utils/validationStatus";
import ValidationStatusBadge from "./ValidationStatusBadge";


type NavLeaf = {
  key: string;
  label: string;
  to: string;
  /** Validation status shown beside a package's own menu entry. */
  badge?: "clean" | "review";
};
/** A sub-heading inside a top-level item's own children -- one level deeper
 * than a leaf link, with no `to` of its own in most cases (not independently
 * navigable, just a grouping label). Used by "Generate" to separate its two
 * unrelated artifact families (report/digest viewers vs. the gap explorer)
 * without either needing its own top-level nav slot.
 *
 * `to`, when set, is the group's own "bare" route -- the page shown before
 * any specific leaf is picked (e.g. `/reports` defaults to the Report leaf).
 * Omit it when a group's first leaf's own `to` already covers that case
 * (e.g. Gaps's "All" leaf's `to` already *is* the bare `/gaps` route). */
type NavGroup = { key: string; label: string; to?: string; children: NavLeaf[] };
type NavChild = NavLeaf | NavGroup;
type NavItem = {
  key: string;
  label: string;
  icon: IconName;
  to: string;
  children?: NavChild[];
  /** The pipeline step whose output this menu shows. The item is hidden until
   * that step has run. Items with no step (Pipeline) are always shown. */
  step?: StepKey;
  /** Route to navigate to when the menu is opened (not when it's closed). Lets
   * a menu whose own `to` is only a prefix land on its default page. */
  openTo?: string;
  /** Highlight only on this exact route, never on a deeper page under it. */
  exact?: boolean;
};

/** True when `pathname` is `route` itself or a page beneath it -- matching on a
 * segment boundary, so `/validation/Package` never reads as covering
 * `/validation/Package_Advanced`. */
function routeMatches(pathname: string, route: string): boolean {
  return pathname === route || pathname.startsWith(route + "/");
}

function isGroup(child: NavChild): child is NavGroup {
  return "children" in child;
}

/** Every route reachable under an item (leaf links plus each group's own
 * optional bare route), flattened one level through any groups -- used to
 * decide whether the item should read as "active" for the current URL,
 * without hardcoding which keys have groups vs. plain children. */
function leafRoutes(children: NavChild[]): string[] {
  return children.flatMap((c) =>
    isGroup(c) ? [...(c.to ? [c.to] : []), ...c.children.map((l) => l.to)] : [c.to],
  );
}

const STORAGE_KEY = "ssisui.sidebarOpen";

function loadOpen(): boolean {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (raw === null) return true;
    return JSON.parse(raw) === true;
  } catch {
    return true;
  }
}

export default function Sidebar({ workspace }: { workspace: Workspace }) {
  const location = useLocation();
  const navigate = useNavigate();
  const base = `/workspaces/${workspace.id}`;

  const [open, setOpen] = useState(loadOpen);
  useEffect(() => {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(open));
    } catch {
      // ignore -- localStorage unavailable (private mode, etc.)
    }
  }, [open]);

  const walkthroughQuery = useQuery({
    queryKey: ["artifacts", workspace.id, "walkthrough"],
    queryFn: () => getWalkthrough(workspace.id),
  });

  const walkthroughChildren: NavChild[] | undefined = walkthroughQuery.data
    ?.available
    ? [...walkthroughQuery.data.packages]
        .sort((a, b) => a.package.localeCompare(b.package))
        .map((p) => ({
          key: p.package,
          label: p.package,
          to: `${base}/walkthrough/${encodeURIComponent(p.package)}`,
        }))
    : undefined;

  // Re-checked every few seconds so a menu appears as soon as its step finishes.
  const stepStatusQuery = useQuery({
    queryKey: ["step-status", workspace.id],
    queryFn: () => getStepStatus(workspace.id),
    refetchInterval: 5000,
  });

  const validationQuery = useQuery({
    queryKey: ["artifacts", workspace.id, "validation"],
    queryFn: () => getValidation(workspace.id),
    refetchInterval: 5000,
  });

  // One leaf per package that has gate reports, same shape as the
  // Package Explorer children above.
  const validationChildren: NavChild[] | undefined = validationQuery.data?.length
    ? [...validationQuery.data]
        .sort((a, b) => a.package.localeCompare(b.package))
        .map((p) => ({
          key: p.package,
          label: p.package,
          to: `${base}/validation/${encodeURIComponent(p.package)}`,
          badge: isClean(p) ? "clean" : "review",
        }))
    : undefined;

  const items: NavItem[] = [
    { key: "pipeline", label: "Pipeline", icon: "pipeline", to: base },
    {
      key: "dashboard",
      label: "Dashboard",
      icon: "dashboard",
      to: `${base}/dashboard`,
      step: "extract",
    },
    {
      key: "extracted",
      label: "Extracted Data",
      icon: "extracted",
      to: `${base}/extracted`,
      step: "extract",
    },
    {
      // Both groups below only have anything to show once Generate has
      // actually run (report/digest files and gaps.json are its own
      // output) -- combined under one "Generate" menu rather than two
      // separate top-level items for that reason.
      key: "reports",
      label: "Generate",
      icon: "reports",
      to: `${base}/reports`,
      openTo: `${base}/reports/generate`,
      step: "generate",
      children: [
        { key: "generate", label: "Report", to: `${base}/reports/generate` },
        { key: "gaps", label: "Gaps", to: `${base}/gaps` },
      ],
    },
    {
      key: "fills",
      label: "Applied Fills",
      icon: "fills",
      to: `${base}/fills`,
      step: "applyFills",
    },
    {
      key: "tests",
      label: "Applied Tests",
      icon: "fills",
      to: `${base}/tests`,
      // Hidden until apply-tests has actually run (tests-applied.json exists).
      step: "applyTests",
    },
    {
      key: "coverage",
      label: "Coverage",
      icon: "coverage",
      to: `${base}/coverage`,
      step: "coverage",
    },
    {
      key: "sampledata",
      label: "Sample Data",
      icon: "sampledata",
      to: `${base}/sampledata`,
      step: "sampledata",
    },
    {
      key: "walkthrough",
      label: "Package Explorer",
      icon: "walkthrough",
      to: `${base}/walkthrough`,
      step: "walkthrough",
      // Opening the menu lands on its first package, so it isn't just a list
      // of packages with nothing shown until one is picked.
      openTo: walkthroughChildren?.[0]?.to,
      children: walkthroughChildren,
    },
    {
      // The summary of every package at a glance. First menu in Phase 2.
      key: "validation-dashboard",
      label: "Dashboard",
      icon: "dashboard",
      to: `${base}/validation`,
      step: "validate",
      exact: true,
    },
    {
      key: "validation",
      label: "Report",
      icon: "validation",
      to: validationChildren?.[0]?.to ?? `${base}/validation`,
      step: "validate",
      // Opening the menu lands on its first package; the dashboard is its own menu above.
      openTo: validationChildren?.[0]?.to ?? `${base}/validation`,
      children: validationChildren,
    },
  ];

  /** Which pipeline phase a menu belongs to. Pipeline itself sits above both. */
  const PHASE_TITLE: Record<"Phase 1" | "Phase 2", string> = {
    "Phase 1": "Phase 1 - Extract/Generate",
    "Phase 2": "Phase 2 - Validation",
  };
  const PHASE_OF: Record<string, "Phase 1" | "Phase 2"> = {
    "validation-dashboard": "Phase 2",
    validation: "Phase 2",
  };
  const phaseOf = (item: NavItem) =>
    item.key === "pipeline" ? undefined : (PHASE_OF[item.key] ?? "Phase 1");
  /** A heading above the first menu of each phase -- shown only where the phase changes. */
  function phaseHeading(item: NavItem, index: number) {
    const phase = phaseOf(item);
    if (!phase) return null;
    const prevPhase = index > 0 ? phaseOf(visibleItems[index - 1]) : undefined;
    if (phase === prevPhase) return null;
    return (
      <div className="px-2.5 pb-1 pt-3 text-[10px] font-semibold uppercase tracking-wide text-gray-400">
        {PHASE_TITLE[phase]}
      </div>
    );
  }

  // An item with a step is hidden until that step has run. While the status
  // is still loading, everything shows so the menu doesn't flicker on open.
  const done = stepStatusQuery.data?.done;
  const visibleItems = items.filter(
    (item) => !item.step || !done || done[item.step],
  );

  // The menus (and sub-groups) that contain the given URL. Used on mount and
  // again on every navigation, since this sidebar stays mounted as you move
  // between pages and its state would otherwise never see the new URL.
  function expandedKeysFor(pathname: string): Set<string> {
    const keys = new Set<string>();
    for (const item of items) {
      if (!item.children) continue;
      if (!leafRoutes(item.children).some((r) => routeMatches(pathname, r))) continue;
      keys.add(item.key);
      // Auto-expand the specific sub-group the current URL falls under too
      // (e.g. landing on /gaps should open Generate -> Gaps, not just Generate).
      for (const child of item.children) {
        if (isGroup(child) && leafRoutes([child]).some((r) => routeMatches(pathname, r))) {
          keys.add(child.key);
        }
      }
    }
    return keys;
  }

  const [expanded, setExpanded] = useState<Set<string>>(() => expandedKeysFor(location.pathname));
  useEffect(() => {
    const keys = expandedKeysFor(location.pathname);
    if (keys.size > 0) setExpanded((prev) => new Set([...prev, ...keys]));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [location.pathname]);

  function toggle(key: string) {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }

  if (!open) {
    return (
      <aside className="sticky top-[calc(var(--app-header-h,3.25rem)+0.75rem)] w-10 shrink-0 self-start">
        <button
          onClick={() => setOpen(true)}
          aria-label="Open navigation"
          title="Show menu"
          className="cursor-pointer rounded border border-gray-200 bg-white p-2 text-gray-500 shadow-sm hover:bg-gray-50"
        >
          <NavIcon name="expand" />
        </button>
      </aside>
    );
  }

  return (
    // sticky + self-start keeps the menu in view while the page on the right
    // scrolls. max-h with its own scroll covers a menu taller than the window.
    <aside className="sticky top-[calc(var(--app-header-h,3.25rem)+0.75rem)] flex max-h-[calc(100vh-var(--app-header-h,3.25rem)-1.5rem)] w-56 shrink-0 flex-col self-start">
      {/* Pipeline stays pinned at the top; the collapse control sits beside it. */}
      <div className="flex shrink-0 items-center gap-1 rounded-lg border border-gray-200 bg-white p-2 shadow-sm">
        {(() => {
          const pipeline = visibleItems.find((i) => i.key === "pipeline");
          if (!pipeline) return null;
          const isActive = location.pathname === base;
          return (
            <Link
              to={pipeline.to}
              className={`flex min-w-0 flex-1 items-center gap-2 rounded px-2.5 py-2 text-sm transition-colors ${
                isActive ? "bg-blue-50 font-semibold text-blue-700" : "text-gray-700 hover:bg-gray-50"
              }`}
            >
              <NavIcon name={pipeline.icon} />
              {pipeline.label}
            </Link>
          );
        })()}
        <button
          onClick={() => setOpen(false)}
          aria-label="Collapse navigation"
          title="Hide menu"
          className="shrink-0 cursor-pointer rounded p-1 text-gray-400 hover:bg-gray-100 hover:text-gray-700"
        >
          <NavIcon name="collapse" className="h-4 w-4" />
        </button>
      </div>

      {/* The rest of the menu scrolls on its own, under the pinned Pipeline row. */}
      <nav className="mt-2 min-h-0 flex-1 space-y-0.5 overflow-y-auto rounded-lg border border-gray-200 bg-white p-2 shadow-sm">
        {visibleItems.map((item, index) => {
          // Pipeline is pinned above, so it is skipped in this scrolling list.
          if (item.key === "pipeline") return null;
          // The index route's own item (`to === base`) is a prefix of every
          // other route under this workspace, so it needs an exact match
          // rather than startsWith -- otherwise "Pipeline" would read as
          // active on every page, not just its own.
          const routes = item.children ? leafRoutes(item.children) : [item.to];
          const isActive =
            item.to === base || item.exact
              ? location.pathname === item.to
              : routes.some((r) => routeMatches(location.pathname, r));
          const isExpanded = expanded.has(item.key);

          if (!item.children) {
            return (
              <Fragment key={item.key}>
                {phaseHeading(item, index)}
                <Link
                  to={item.to}
                  className={`flex items-center gap-2 rounded px-2.5 py-2 text-sm transition-colors ${
                    isActive
                      ? "bg-blue-50 font-semibold text-blue-700"
                      : "text-gray-700 hover:bg-gray-50"
                  }`}
                >
                  <NavIcon name={item.icon} />
                  {item.label}
                </Link>
              </Fragment>
            );
          }

          return (
            <Fragment key={item.key}>
            {phaseHeading(item, index)}
            <div>
              <button
                onClick={() => {
                  // Opening a menu with an openTo lands on its default page.
                  if (!isExpanded && item.openTo) navigate(item.openTo);
                  toggle(item.key);
                }}
                className={`flex w-full cursor-pointer items-center gap-2 rounded px-2.5 py-2 text-left text-sm transition-colors ${
                  isActive
                    ? "bg-blue-50 font-semibold text-blue-700"
                    : "text-gray-700 hover:bg-gray-50"
                }`}
              >
                <NavIcon name={item.icon} />
                <span className="flex-1">{item.label}</span>
                <NavIcon
                  name="chevron"
                  className={`h-3.5 w-3.5 transition-transform ${isExpanded ? "" : "-rotate-90"}`}
                />
              </button>
              {isExpanded && item.children.length > 0 && (
                <div
                  className={`ml-5 mt-0.5 space-y-0.5 border-l border-gray-100 pl-2 ${
                    item.key === "walkthrough" ? "max-h-64 overflow-y-auto" : ""
                  }`}
                >
                  {item.children.map((child) => {
                    if (isGroup(child)) {
                      const groupExpanded = expanded.has(child.key);
                      return (
                        <div key={child.key} className="mt-1.5 first:mt-0">
                          <button
                            onClick={() => toggle(child.key)}
                            className="flex w-full cursor-pointer items-center gap-1 rounded px-2 py-1 text-left text-[10px] font-semibold uppercase tracking-wide text-gray-400 transition-colors hover:bg-gray-50 hover:text-gray-500"
                          >
                            <NavIcon
                              name="chevron"
                              className={`h-3 w-3 shrink-0 transition-transform ${
                                groupExpanded ? "" : "-rotate-90"
                              }`}
                            />
                            <span className="flex-1">{child.label}</span>
                          </button>
                          {groupExpanded && (
                          <div className="space-y-0.5">
                            {child.children.map((leaf, idx) => {
                              const isDefaultLeaf = idx === 0 && child.to !== undefined;
                              const leafIsActive =
                                location.pathname === leaf.to ||
                                (isDefaultLeaf && location.pathname === child.to);
                              return (
                                <Link
                                  key={leaf.key}
                                  to={leaf.to}
                                  className={`block truncate rounded px-2 py-1.5 text-xs transition-colors ${
                                    leafIsActive
                                      ? "bg-blue-50 font-medium text-blue-700"
                                      : "text-gray-600 hover:bg-gray-50"
                                  }`}
                                  title={leaf.label}
                                >
                                  {leaf.label}
                                </Link>
                              );
                            })}
                          </div>
                          )}
                        </div>
                      );
                    }
                    // Segment-aware, so Gaps also reads as active on /gaps/blocking and /gaps/advisory.
                    const childIsActive = routeMatches(location.pathname, child.to);
                    return (
                      <Link
                        key={child.key}
                        to={child.to}
                        className={`block truncate rounded px-2 py-1.5 text-xs transition-colors ${
                          childIsActive
                            ? "bg-blue-50 font-medium text-blue-700"
                            : "text-gray-600 hover:bg-gray-50"
                        }`}
                        title={child.label}
                      >
                        {child.label}
                        {child.badge && <ValidationStatusBadge kind={child.badge} />}
                      </Link>
                    );
                  })}
                </div>
              )}
            </div>
            </Fragment>
          );
        })}
      </nav>
    </aside>
  );
}
