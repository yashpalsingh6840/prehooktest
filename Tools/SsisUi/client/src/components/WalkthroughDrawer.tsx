import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { getWalkthrough, type Workspace } from "../api/client";
import EmptyState from "./EmptyState";
import ErrorState from "./ErrorState";
import LoadingState from "./LoadingState";
import MarkdownView from "./MarkdownView";
import { ClaimsPanel } from "./ClaimsPanel";
import TabNav from "./TabNav";
import PackageComponents from "../pages/PackageComponents";

type DrawerTab = "info" | "components";
const DRAWER_TABS: { key: DrawerTab; label: string }[] = [
  { key: "info", label: "Info" },
  { key: "components", label: "Components" },
];

/** Right-hand drawer showing one package's Package Explorer (walkthrough) report
 * without leaving the current page. Shares the walkthrough query the Package
 * Explorer page uses, so opening it costs no extra request once that data is
 * loaded. Closes on Escape, on the backdrop, or on the close button. */
export default function WalkthroughDrawer({
  workspace,
  packageName,
  onClose,
}: {
  workspace: Workspace;
  packageName: string;
  onClose: () => void;
}) {
  const { data, isLoading, error } = useQuery({
    queryKey: ["artifacts", workspace.id, "walkthrough"],
    queryFn: () => getWalkthrough(workspace.id),
  });

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  const pkg = data?.packages.find((p) => p.package === packageName);
  const [tab, setTab] = useState<DrawerTab>("info");

  return (
    <div className="fixed inset-0 z-50 flex justify-end bg-gray-900/40" onMouseDown={onClose}>
      <aside
        role="dialog"
        aria-modal="true"
        aria-label={`Package Explorer: ${packageName}`}
        onMouseDown={(e) => e.stopPropagation()}
        className="flex h-full w-full max-w-3xl flex-col bg-white shadow-xl"
      >
        <header className="flex items-center justify-between gap-3 border-b border-gray-200 px-5 py-3">
          <div className="min-w-0">
            <p className="text-[11px] font-semibold uppercase tracking-wide text-gray-500">Package Explorer</p>
            <h3 className="truncate font-mono text-sm font-semibold text-gray-900">{packageName}</h3>
          </div>
          <div className="flex shrink-0 items-center gap-2">
            {pkg && (
              <Link
                to={`/workspaces/${workspace.id}/walkthrough/${encodeURIComponent(packageName)}`}
                onClick={onClose}
                className="rounded border border-gray-300 px-2.5 py-1 text-xs font-medium text-gray-700 hover:bg-gray-50"
              >
                Open full page
              </Link>
            )}
            <button
              type="button"
              onClick={onClose}
              aria-label="Close"
              className="cursor-pointer rounded px-2 py-0.5 text-lg leading-none text-gray-500 hover:bg-gray-100 hover:text-gray-800"
            >
              ×
            </button>
          </div>
        </header>

        <div className="border-b border-gray-200 px-5">
          <TabNav tabs={DRAWER_TABS} active={tab} onSelect={(key) => setTab(key as DrawerTab)} />
        </div>

        <div className="flex-1 overflow-y-auto px-5 py-4">
          {tab === "info" && (
            <>
              {isLoading && <LoadingState />}
              {error && <ErrorState error={error} />}
              {data && !data.available && (
                <EmptyState message="Nothing here yet -- run the Walkthrough step first." />
              )}
              {data && data.available && !pkg && (
                <EmptyState message={`No walkthrough was generated for ${packageName}.`} />
              )}
              {pkg && (
                <>
                  <div className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm">
                    <MarkdownView markdown={pkg.markdown} />
                  </div>
                  <h3 className="mb-2 mt-8 text-base font-semibold">Rule Reviews</h3>
                  <ClaimsPanel workspace={workspace} packageName={pkg.package} />
                </>
              )}
            </>
          )}
          {tab === "components" && <PackageComponents workspace={workspace} packageName={packageName} showExtractedLink={false} />}
        </div>
      </aside>
    </div>
  );
}
