export type IconName =
  | "dashboard"
  | "reports"
  | "gaps"
  | "fills"
  | "walkthrough"
  | "extracted"
  | "chevron"
  | "menu"
  | "close"
  | "collapse"
  | "expand"
  | "folder"
  | "external"
  | "sampledata"
  | "copy"
  | "download"
  | "pipeline"
  | "coverage"
  | "validation"
  | "edit"
  | "delete"
  | "save"
  | "refresh"
  | "clear"
  | "play"
  | "plus"
  | "info";

// A small, dependency-free icon set built only from simple SVG primitives
// (never a hand-written path string) so there's no risk of malformed path
// syntax. Stroke-based, currentColor, so icons inherit text color/size from
// their caller.
export default function NavIcon({ name, className }: { name: IconName; className?: string }) {
  const cls = className ?? "h-4 w-4";
  const common = {
    viewBox: "0 0 24 24",
    fill: "none",
    stroke: "currentColor",
    strokeWidth: 1.5,
    strokeLinecap: "round" as const,
    strokeLinejoin: "round" as const,
    className: cls,
  };

  switch (name) {
    case "dashboard":
      return (
        <svg {...common}>
          <rect x="3" y="3" width="8" height="8" rx="1.5" />
          <rect x="13" y="3" width="8" height="8" rx="1.5" />
          <rect x="3" y="13" width="8" height="8" rx="1.5" />
          <rect x="13" y="13" width="8" height="8" rx="1.5" />
        </svg>
      );
    case "reports":
      return (
        <svg {...common}>
          <rect x="4" y="3" width="16" height="18" rx="1.5" />
          <line x1="7.5" y1="8" x2="16.5" y2="8" />
          <line x1="7.5" y1="12" x2="16.5" y2="12" />
          <line x1="7.5" y1="16" x2="13" y2="16" />
        </svg>
      );
    case "gaps":
      return (
        <svg {...common}>
          <polygon points="12,3 21,20 3,20" />
          <line x1="12" y1="9" x2="12" y2="14" />
          <circle cx="12" cy="17" r="0.75" fill="currentColor" stroke="none" />
        </svg>
      );
    case "fills":
      return (
        <svg {...common}>
          <circle cx="12" cy="12" r="9" />
          <polyline points="7.5,12.5 10.5,15.5 16.5,9" />
        </svg>
      );
    case "walkthrough":
      return (
        <svg {...common}>
          <circle cx="4.5" cy="12" r="2" />
          <circle cx="12" cy="6" r="2" />
          <circle cx="19.5" cy="12" r="2" />
          <line x1="6.3" y1="11" x2="10.2" y2="7.2" />
          <line x1="13.8" y1="7.2" x2="17.7" y2="11" />
        </svg>
      );
    case "extracted":
      // A "</>" code-bracket mark -- built only from polylines, same
      // primitives-only rule as every icon in this file.
      return (
        <svg {...common}>
          <polyline points="9,4 4,12 9,20" />
          <polyline points="15,4 20,12 15,20" />
        </svg>
      );
    case "chevron":
      // The caller rotates this via a CSS class (e.g. -rotate-90) rather
      // than this component taking a direction prop.
      return (
        <svg {...common}>
          <polyline points="6,9 12,15 18,9" />
        </svg>
      );
    case "menu":
      return (
        <svg {...common}>
          <line x1="3.5" y1="6" x2="20.5" y2="6" />
          <line x1="3.5" y1="12" x2="20.5" y2="12" />
          <line x1="3.5" y1="18" x2="20.5" y2="18" />
        </svg>
      );
    case "close":
      return (
        <svg {...common}>
          <line x1="5" y1="5" x2="19" y2="19" />
          <line x1="19" y1="5" x2="5" y2="19" />
        </svg>
      );
    case "collapse":
      // Double chevron pointing left (<<), used to hide the side menu.
      return (
        <svg {...common}>
          <polyline points="11,17 6,12 11,7" />
          <polyline points="18,17 13,12 18,7" />
        </svg>
      );
    case "expand":
      // Double chevron pointing right (>>), used to show the side menu again.
      return (
        <svg {...common}>
          <polyline points="6,17 11,12 6,7" />
          <polyline points="13,17 18,12 13,7" />
        </svg>
      );
    case "folder":
      return (
        <svg {...common}>
          <polygon points="3,7 3,19 21,19 21,9 11,9 9,7" />
        </svg>
      );
    case "sampledata":
      // A simple data-grid mark -- rows/columns of sample values.
      return (
        <svg {...common}>
          <rect x="3.5" y="4" width="17" height="16" rx="1.5" />
          <line x1="3.5" y1="9" x2="20.5" y2="9" />
          <line x1="9.5" y1="9" x2="9.5" y2="20" />
          <line x1="15" y1="9" x2="15" y2="20" />
        </svg>
      );
    case "pipeline":
      // Three connected nodes in a horizontal row -- a sequential run, as
      // distinct from "dashboard"'s 2x2 grid-of-tiles mark.
      return (
        <svg {...common}>
          <circle cx="4" cy="12" r="2.25" />
          <circle cx="12" cy="12" r="2.25" />
          <circle cx="20" cy="12" r="2.25" />
          <line x1="6.25" y1="12" x2="9.75" y2="12" />
          <line x1="14.25" y1="12" x2="17.75" y2="12" />
        </svg>
      );
    case "copy":
      // Two overlapping rectangles -- the standard "copy to clipboard" mark,
      // built only from rects per this file's own primitives-only rule.
      return (
        <svg {...common}>
          <rect x="8.5" y="8.5" width="11.5" height="11.5" rx="1.5" />
          <rect x="4" y="4" width="11.5" height="11.5" rx="1.5" />
        </svg>
      );
    case "download":
      // An arrow down into a tray, built from lines and a polyline per this
      // file's own primitives-only rule.
      return (
        <svg {...common}>
          <line x1="12" y1="4" x2="12" y2="15" />
          <polyline points="7 10.5 12 15.5 17 10.5" />
          <line x1="5" y1="20" x2="19" y2="20" />
        </svg>
      );
    case "coverage":
      // A simple bar-chart mark -- three bars of increasing height, built
      // only from rects/a line per this file's own primitives-only rule.
      return (
        <svg {...common}>
          <line x1="3.5" y1="20.5" x2="20.5" y2="20.5" />
          <rect x="5" y="13" width="3.5" height="7.5" />
          <rect x="10.25" y="8" width="3.5" height="12.5" />
          <rect x="15.5" y="3.5" width="3.5" height="17" />
        </svg>
      );
    case "validation":
      // A shield with a check mark -- "verified", built from a polygon and a
      // polyline per this file's own primitives-only rule.
      return (
        <svg {...common}>
          <polygon points="12,3 19.5,6 19.5,12 12,21 4.5,12 4.5,6" />
          <polyline points="8.5,12 11,14.5 15.5,9.5" />
        </svg>
      );
    case "external":
      // An arrow breaking out of a box -- "open this elsewhere" (a new
      // window/app, or an in-app page reached by leaving the current one).
      return (
        <svg {...common}>
          <rect x="5" y="9" width="10" height="10" rx="1" />
          <polyline points="10,5 19,5 19,14" />
          <line x1="19" y1="5" x2="9.5" y2="14.5" />
        </svg>
      );

    case "edit":
      // A pencil: a body line plus a tip, drawn as primitives.
      return (
        <svg {...common}>
          <path d="M4 20h4l10.5-10.5a2.1 2.1 0 0 0-3-3L5 17v3z" />
          <line x1="13.5" y1="7.5" x2="16.5" y2="10.5" />
        </svg>
      );
    case "delete":
      // A bin: lid line, handle, and body.
      return (
        <svg {...common}>
          <line x1="4" y1="7" x2="20" y2="7" />
          <line x1="9.5" y1="4" x2="14.5" y2="4" />
          <rect x="6" y="7" width="12" height="13" rx="1.5" />
          <line x1="10" y1="11" x2="10" y2="16" />
          <line x1="14" y1="11" x2="14" y2="16" />
        </svg>
      );
    case "save":
      // A floppy disk: outline, label, and shutter.
      return (
        <svg {...common}>
          <path d="M5 3h11l3 3v13a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z" />
          <rect x="8" y="3" width="7" height="5" />
          <rect x="7" y="13" width="10" height="7" rx="0.5" />
        </svg>
      );
    case "refresh":
      // A circular reload arrow.
      return (
        <svg {...common}>
          <path d="M20 12a8 8 0 1 1-2.34-5.66" />
          <polyline points="20,4 20,9 15,9" />
        </svg>
      );
    case "play":
      // A filled-outline play triangle: "start this run".
      return (
        <svg {...common}>
          <polygon points="7,4.5 19.5,12 7,19.5" />
        </svg>
      );
    case "plus":
      return (
        <svg {...common}>
          <line x1="12" y1="5" x2="12" y2="19" />
          <line x1="5" y1="12" x2="19" y2="12" />
        </svg>
      );
    case "info":
      // A circle with an "i": help / guidance.
      return (
        <svg {...common}>
          <circle cx="12" cy="12" r="9" />
          <line x1="12" y1="11" x2="12" y2="16.5" />
          <circle cx="12" cy="7.75" r="0.75" fill="currentColor" stroke="none" />
        </svg>
      );
    case "clear":
      // Two crossed strokes: "clear / remove contents".
      return (
        <svg {...common}>
          <line x1="6" y1="6" x2="18" y2="18" />
          <line x1="18" y1="6" x2="6" y2="18" />
        </svg>
      );
  }
}
