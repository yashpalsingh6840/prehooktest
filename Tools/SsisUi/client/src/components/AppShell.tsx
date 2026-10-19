import type { ReactNode } from "react";
import { NavLink } from "react-router-dom";
import { useMeasuredHeight } from "../utils/useMeasuredHeight";
import NavIcon from "./NavIcon";

const linkClass = ({ isActive }: { isActive: boolean }) =>
  `text-sm transition-colors ${
    isActive ? "font-semibold text-gray-900" : "text-gray-500 hover:text-gray-800"
  }`;

export default function AppShell({ children }: { children: ReactNode }) {
  const headerRef = useMeasuredHeight("--app-header-h");
  return (
    <div className="min-h-screen bg-gray-50 font-sans text-gray-900">
      <header ref={headerRef} className="sticky top-0 z-30 flex items-center gap-6 border-b border-gray-200 bg-white px-6 py-3 shadow-sm">
        <strong className="text-base tracking-tight">SSIS Rewrite Studio</strong>
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
        <div className="ml-auto flex min-w-0 items-center gap-3">
          <div id="app-header-slot" className="flex min-w-0 items-center" />
          <NavLink
            to="/guide"
            title="Setup and pipeline guide"
            aria-label="Setup and pipeline guide"
            className={({ isActive }) =>
              `flex h-7 w-7 shrink-0 items-center justify-center rounded-full border ${
                isActive
                  ? "border-blue-500 bg-blue-50 text-blue-700"
                  : "border-gray-300 bg-white text-gray-600 hover:bg-gray-50 hover:text-gray-900"
              }`
            }
          >
            <NavIcon name="info" className="h-4 w-4" />
          </NavLink>
        </div>
      </header>
      {children}
    </div>
  );
}
