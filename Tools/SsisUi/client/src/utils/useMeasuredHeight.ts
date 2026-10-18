import { useLayoutEffect, useState } from "react";

/**
 * Publishes an element's real rendered height as a CSS variable on <html>, and
 * keeps it current as the element resizes. Sticky bars stacked under each other
 * use these variables for their `top` offsets, so the offsets track the bars'
 * actual heights rather than a guessed constant (a guess shows up as the bars
 * shifting a little on the first scroll).
 *
 * Returns a callback ref to attach to the element.
 */
export function useMeasuredHeight(cssVar: string) {
  const [el, setEl] = useState<HTMLElement | null>(null);

  useLayoutEffect(() => {
    if (!el) return;
    const root = document.documentElement;
    const apply = () => root.style.setProperty(cssVar, `${el.offsetHeight}px`);
    apply();
    const observer = new ResizeObserver(apply);
    observer.observe(el);
    return () => {
      observer.disconnect();
      root.style.removeProperty(cssVar);
    };
  }, [el, cssVar]);

  return setEl;
}
