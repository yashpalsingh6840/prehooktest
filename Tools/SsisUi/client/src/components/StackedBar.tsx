export type BarSegment = {
  label: string;
  value: number;
  /** Tailwind background class, e.g. "bg-green-500". */
  className: string;
};

/** A horizontal bar split into proportional segments. Zero-value segments are skipped. */
export default function StackedBar({
  segments,
  height = "h-2.5",
}: {
  segments: BarSegment[];
  height?: string;
}) {
  const total = segments.reduce((s, seg) => s + seg.value, 0);
  const label = segments.map((s) => `${s.label} ${s.value}`).join(", ");

  return (
    <div
      role="img"
      aria-label={label}
      className={`flex w-full min-w-0 overflow-hidden rounded bg-gray-100 ${height}`}
    >
      {total > 0 &&
        segments
          .filter((s) => s.value > 0)
          .map((s) => (
            <div
              key={s.label}
              className={`${s.className} h-full`}
              style={{ width: `${(s.value / total) * 100}%` }}
              title={`${s.label}: ${s.value}`}
            />
          ))}
    </div>
  );
}
