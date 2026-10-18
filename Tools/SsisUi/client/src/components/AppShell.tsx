import type { ReactNode } from "react";
import { NavLink } from "react-router-dom";
import { useMeasuredHeight } from "../utils/useMeasuredHeight";

const linkClass = ({ isActive }: { isActive: boolean }) =>
  `text-sm transition-colors ${
    isActive ? "font-semibold text-gray-900" : "text-gray-500 hover:text-gray-800"
  }`;

export default function AppShell({ children }: { children: ReactNode }) {
  const headerRef = useMeasuredHeight("--app-header-h");
  return (
    <div className="min-h-screen bg-gray-50 font-sans text-gray-900">
      <header ref={headerRef} className="sticky top-0 z-30 flex items-center gap-6 border-b border-gray-200 bg-white px-6 py-3 shadow-sm">
        <strong className="text-base tracking-tight">SsisUi</strong>
        <nav className="flex gap-4">
          <NavLink to="/" end className={linkClass}>
            Workspaces
          </NavLink>
          <NavLink to="/settings" className={linkClass}>
            Settings
          </NavLink>
        </nav>
        {/* Right side of the bar: the open workspace's name and output path. Pages
            fill it through a portal (see WorkspaceDetail), so it is empty elsewhere. */}
        <div id="app-header-slot" className="ml-auto flex min-w-0 items-center" />
      </header>
      {children}
    </div>
  );
}
