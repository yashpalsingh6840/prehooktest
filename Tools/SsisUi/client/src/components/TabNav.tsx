import { Link } from "react-router-dom";

/** Underline-style tab strip, the same pattern ValidationPage uses for its
 * Rollup/Compare/Tier 2/Files tabs. Pass `hrefFor` for route-backed tabs (each
 * tab is a real link, so the active tab survives a refresh and can be
 * bookmarked), or `onSelect` for tabs that just switch local page state. */
export default function TabNav<K extends string>({
  tabs,
  active,
  hrefFor,
  onSelect,
}: {
  tabs: { key: K; label: string }[];
  active: K;
  hrefFor?: (key: K) => string;
  onSelect?: (key: K) => void;
}) {
  const className = (isActive: boolean) =>
    `-mb-px border-b-2 px-3 py-2 text-sm ${
      isActive
        ? "border-blue-600 font-semibold text-blue-700"
        : "border-transparent text-gray-600 hover:text-gray-900"
    }`;

  return (
    <div className="mb-4 flex gap-1 border-b border-gray-200">
      {tabs.map((t) =>
        onSelect ? (
          <button
            key={t.key}
            type="button"
            onClick={() => onSelect(t.key)}
            className={className(t.key === active)}
          >
            {t.label}
          </button>
        ) : (
          <Link key={t.key} to={hrefFor!(t.key)} className={className(t.key === active)}>
            {t.label}
          </Link>
        ),
      )}
    </div>
  );
}
