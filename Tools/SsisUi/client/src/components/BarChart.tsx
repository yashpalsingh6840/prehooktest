import { useId, useLayoutEffect, useRef, useState } from "react";
import { CHART_ACCENT, CHART_CHROME } from "../utils/chartPalette";

export type BarDatum = {
  label: string;
  value: number;
  /** Per-bar override -- omit to use the single accent color (the right
   * choice whenever bars don't need cross-chart identity, e.g. an unbounded
   * or purely-ranked category set like "coverage % by package"). */
  color?: string;
};

/** Round a step count up to a "nice" value (1/2/5 x a power of ten) so
 * gridlines land on clean numbers instead of an arbitrary fraction of max. */
function niceCeiling(max: number): number {
  if (max <= 0) return 1;
  const magnitude = Math.pow(10, Math.floor(Math.log10(max)));
  const residual = max / magnitude;
  let niceResidual: number;
  if (residual <= 1) niceResidual = 1;
  else if (residual <= 2) niceResidual = 2;
  else if (residual <= 5) niceResidual = 5;
  else niceResidual = 10;
  return niceResidual * magnitude;
}

function buildTicks(niceMax: number, count = 4): number[] {
  const step = niceMax / count;
  return Array.from({ length: count + 1 }, (_, i) => Math.round(i * step * 10) / 10);
}

function compactNumber(n: number): string {
  if (Math.abs(n) >= 1000) {
    const k = n / 1000;
    return `${Number.isInteger(k) ? k : k.toFixed(1)}K`;
  }
  return `${n}`;
}

function truncateLabel(label: string, maxChars: number): string {
  return label.length > maxChars ? `${label.slice(0, maxChars - 1)}…` : label;
}

/** An SVG path for a bar with a rounded top and a square baseline --
 * `rect rx` would round all four corners, which isn't the spec. */
function barPath(x: number, yTop: number, width: number, height: number, radius: number): string {
  if (height <= 0) return "";
  const r = Math.max(0, Math.min(radius, height, width / 2));
  const yBottom = yTop + height;
  if (r === 0) {
    return `M${x},${yBottom} L${x},${yTop} L${x + width},${yTop} L${x + width},${yBottom} Z`;
  }
  return [
    `M${x},${yBottom}`,
    `L${x},${yTop + r}`,
    `Q${x},${yTop} ${x + r},${yTop}`,
    `L${x + width - r},${yTop}`,
    `Q${x + width},${yTop} ${x + width},${yTop + r}`,
    `L${x + width},${yBottom}`,
    "Z",
  ].join(" ");
}

/**
 * A single-series, category-by-value bar chart -- a plain, styled SVG
 * replacement for the Mermaid `xychart-beta` bars this app used to render
 * everywhere (dull fixed default theme, no hover, no per-category color).
 * Per the dataviz skill: thin capped bars, 4px rounded top / square
 * baseline, hairline recessive gridlines, a hover tooltip (the mark is its
 * own hit target), and a value label at the bar's cap when it fits. No
 * legend box -- every category is already named on the x-axis, so color
 * (when provided per-bar) is never the only identity channel.
 */
export default function BarChart({
  data,
  unit = "",
  formatValue,
  height = 220,
}: {
  data: BarDatum[];
  /** Appended after each displayed value, e.g. "%". Ignored if `formatValue` is given. */
  unit?: string;
  formatValue?: (value: number) => string;
  height?: number;
}) {
  const uid = useId().replace(/:/g, "-");
  const [hovered, setHovered] = useState<number | null>(null);

  // Measures the wrapping div's own available width -- a low-cardinality
  // chart (4 tiers, 4 roles) should fill its container instead of sitting
  // at its own minimum natural width with empty space beside it; a
  // high-cardinality one still grows past the container and scrolls, since
  // this only ever widens the chart, never shrinks it below that minimum.
  const containerRef = useRef<HTMLDivElement>(null);
  const [containerWidth, setContainerWidth] = useState(0);
  useLayoutEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    setContainerWidth(el.clientWidth);
    const observer = new ResizeObserver((entries) => {
      const w = entries[0]?.contentRect.width;
      if (w) setContainerWidth(w);
    });
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  if (data.length === 0) return null;

  const format = formatValue ?? ((v: number) => `${compactNumber(v)}${unit}`);
  const maxValue = Math.max(1, ...data.map((d) => d.value));
  const niceMax = niceCeiling(maxValue);
  const ticks = buildTicks(niceMax);

  const padding = { top: 22, right: 12, bottom: 34, left: 34 };
  const minBandWidth = 56;
  const width = Math.max(320, data.length * minBandWidth, containerWidth);
  const plotW = width - padding.left - padding.right;
  const plotH = height - padding.top - padding.bottom;
  const bandWidth = plotW / data.length;
  const barWidth = Math.min(24, bandWidth * 0.55);
  const labelChars = Math.max(4, Math.floor(bandWidth / 7));

  function yFor(value: number): number {
    return padding.top + plotH - (value / niceMax) * plotH;
  }
  const baselineY = yFor(0);

  return (
    <div ref={containerRef} className="relative overflow-x-auto">
      <svg
        role="img"
        aria-label="Bar chart"
        width={width}
        height={height}
        viewBox={`0 0 ${width} ${height}`}
        className="block"
      >
        {ticks.map((t) => (
          <g key={`${uid}-tick-${t}`}>
            <line
              x1={padding.left}
              x2={width - padding.right}
              y1={yFor(t)}
              y2={yFor(t)}
              stroke={CHART_CHROME.gridline}
              strokeWidth={1}
            />
            <text
              x={padding.left - 6}
              y={yFor(t)}
              textAnchor="end"
              dominantBaseline="middle"
              fontSize={10}
              fill={CHART_CHROME.axisText}
            >
              {compactNumber(t)}
            </text>
          </g>
        ))}
        <line
          x1={padding.left}
          x2={width - padding.right}
          y1={baselineY}
          y2={baselineY}
          stroke={CHART_CHROME.baseline}
          strokeWidth={1}
        />

        {data.map((d, i) => {
          const x = padding.left + i * bandWidth + (bandWidth - barWidth) / 2;
          const barHeight = baselineY - yFor(d.value);
          const color = d.color ?? CHART_ACCENT;
          const isHovered = hovered === i;
          const valueY = Math.max(padding.top + 10, yFor(d.value) - 6);

          return (
            <g
              key={d.label}
              onPointerEnter={() => setHovered(i)}
              onPointerLeave={() => setHovered((h) => (h === i ? null : h))}
              onFocus={() => setHovered(i)}
              onBlur={() => setHovered((h) => (h === i ? null : h))}
              tabIndex={0}
              style={{ outline: "none", cursor: "pointer" }}
            >
              {/* wider, invisible hit target -- a thin bar is an unreliable pointer target on its own */}
              <rect
                x={padding.left + i * bandWidth}
                y={padding.top}
                width={bandWidth}
                height={plotH}
                fill="transparent"
              />
              {d.value > 0 && (
                <path
                  d={barPath(x, yFor(d.value), barWidth, barHeight, 4)}
                  fill={color}
                  opacity={isHovered ? 0.82 : 1}
                />
              )}
              {d.value > 0 && (
                <text
                  x={x + barWidth / 2}
                  y={valueY}
                  textAnchor="middle"
                  fontSize={10}
                  fontWeight={600}
                  fill={CHART_CHROME.valueText}
                >
                  {format(d.value)}
                </text>
              )}
              <text
                x={x + barWidth / 2}
                y={baselineY + 14}
                textAnchor="middle"
                fontSize={10}
                fill={CHART_CHROME.axisText}
              >
                {truncateLabel(d.label, labelChars)}
              </text>
              {isHovered && <title>{`${d.label}: ${format(d.value)}`}</title>}
            </g>
          );
        })}
      </svg>

      {hovered !== null && (
        <div
          className="pointer-events-none absolute z-10 whitespace-nowrap rounded border border-gray-200 bg-white px-2 py-1 text-xs shadow-lg"
          style={{
            left: padding.left + hovered * bandWidth + bandWidth / 2,
            top: Math.max(0, yFor(data[hovered].value) - 40),
            transform: "translateX(-50%)",
          }}
        >
          <div className="font-semibold text-gray-900">{format(data[hovered].value)}</div>
          <div className="text-gray-500">{data[hovered].label}</div>
        </div>
      )}
    </div>
  );
}
