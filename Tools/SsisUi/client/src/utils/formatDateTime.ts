/**
 * A handful of common IANA zones whose conventional 3-4 letter abbreviation
 * (IST, PST, EST, ...) the browser's own Intl/CLDR data deliberately omits
 * from `timeZoneName: "short"` -- it falls back to a bare UTC offset
 * ("GMT+5:30") instead, because several of these abbreviations are
 * genuinely ambiguous across zones (IST alone could mean India, Israel, or
 * Ireland) and CLDR won't guess. This is a deliberate, small, named
 * exception list for the zones this team actually works across -- not an
 * attempt at a complete abbreviation table for every zone on Earth.
 */
const SHORT_ZONE_NAMES: Record<string, string> = {
  "Asia/Kolkata": "IST",
  "Asia/Calcutta": "IST",
  "America/Los_Angeles": "PT",
  "America/Denver": "MT",
  "America/Chicago": "CT",
  "America/New_York": "ET",
};

/**
 * Formats an ISO timestamp for display in the viewer's own local timezone,
 * with a short zone label appended (e.g. "10/18/2026, 2:05:14 PM IST") so
 * it's unambiguous which timezone the time is already converted to -- plain
 * `toLocaleString()` renders in local time but never says which zone that
 * is. Prefers the named abbreviation above when the viewer's resolved zone
 * is one of them; otherwise falls back to whatever Intl's own
 * `timeZoneName: "short"` produces (a real abbreviation where CLDR has an
 * unambiguous one, a UTC offset otherwise).
 */
export function formatDateTime(iso: string): string {
  const date = new Date(iso);
  const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
  const known = SHORT_ZONE_NAMES[zone];
  if (known) {
    return `${date.toLocaleString()} ${known}`;
  }
  return date.toLocaleString(undefined, { timeZoneName: "short" });
}
