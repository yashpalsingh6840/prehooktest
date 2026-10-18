/** A green check when a package is clean, an amber dot when it needs review. */
export default function ValidationStatusBadge({ kind }: { kind: "clean" | "review" }) {
  if (kind === "review") {
    return (
      <span
        role="img"
        aria-label="Needs review"
        title="Needs review"
        className="ml-1.5 inline-block h-2 w-2 shrink-0 rounded-full bg-amber-500 align-middle"
      />
    );
  }
  return (
    <span
      role="img"
      aria-label="All checks passed"
      title="All checks passed"
      className="ml-1.5 inline-flex h-3.5 w-3.5 shrink-0 items-center justify-center rounded-full bg-green-100 align-middle text-green-700"
    >
      <svg viewBox="0 0 20 20" fill="currentColor" className="h-2.5 w-2.5" aria-hidden="true">
        <path
          fillRule="evenodd"
          d="M16.7 5.3a1 1 0 0 1 0 1.4l-7.5 7.5a1 1 0 0 1-1.4 0L3.3 9.7a1 1 0 1 1 1.4-1.4l4 4 6.8-6.8a1 1 0 0 1 1.4 0z"
          clipRule="evenodd"
        />
      </svg>
    </span>
  );
}
