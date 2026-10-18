import { useMemo, useState } from "react";
import { Link } from "react-router-dom";
import InlineMarkdown from "./InlineMarkdown";
import EmptyState from "./EmptyState";

type SortDirection = "asc" | "desc";

const filterBtnCls =
  "rounded border border-gray-300 bg-white px-3 py-1.5 text-sm text-gray-700 placeholder:text-gray-400 focus:border-blue-500 focus:outline-none";

function isNumericColumn(rows: string[][], colIndex: number): boolean {
  let sawValue = false;
  for (const row of rows) {
    const cell = row[colIndex] ?? "";
    if (cell.trim() === "") continue;
    sawValue = true;
    if (Number.isNaN(Number(cell))) return false;
  }
  return sawValue;
}

function compareRows(a: string[], b: string[], colIndex: number, numeric: boolean): number {
  const av = a[colIndex] ?? "";
  const bv = b[colIndex] ?? "";
  if (numeric) return Number(av) - Number(bv);
  return av.localeCompare(bv, undefined, { sensitivity: "base" });
}

export type DataTableProps = {
  headers: string[];
  rows: string[][];
  pageSize?: number;
  filterPlaceholder?: string;
  /** Column whose cell is truncated with a "more"/"less" toggle instead of shown in full. */
  expandableColumnIndex?: number;
  truncateAt?: number;
  /** Column whose cell renders as an internal (react-router) link instead of
   * plain/markdown text -- e.g. a "Package" column linking to that package's
   * own detail page. Ignored unless `linkTo` is also given. */
  linkColumnIndex?: number;
  linkTo?: (cellValue: string) => string;
};

export default function DataTable({
  headers,
  rows,
  pageSize = 20,
  filterPlaceholder = "Filter...",
  expandableColumnIndex,
  truncateAt = 140,
  linkColumnIndex,
  linkTo,
}: DataTableProps) {
  const [filterText, setFilterText] = useState("");
  const [sortColumn, setSortColumn] = useState<number | null>(null);
  const [sortDirection, setSortDirection] = useState<SortDirection>("asc");
  const [page, setPage] = useState(1);
  const [expandedRows, setExpandedRows] = useState<Set<number>>(new Set());

  const filtered = useMemo(() => {
    if (!filterText.trim()) return rows;
    const needle = filterText.toLowerCase();
    return rows.filter((row) => row.some((cell) => cell.toLowerCase().includes(needle)));
  }, [rows, filterText]);

  const sorted = useMemo(() => {
    if (sortColumn === null) return filtered;
    const numeric = isNumericColumn(rows, sortColumn);
    const copy = [...filtered];
    copy.sort((a, b) => {
      const cmp = compareRows(a, b, sortColumn, numeric);
      return sortDirection === "asc" ? cmp : -cmp;
    });
    return copy;
  }, [filtered, sortColumn, sortDirection, rows]);

  const pageCount = Math.max(1, Math.ceil(sorted.length / pageSize));
  const currentPage = Math.min(page, pageCount);
  const pageRows = sorted.slice((currentPage - 1) * pageSize, currentPage * pageSize);

  function handleFilterChange(value: string) {
    setFilterText(value);
    setPage(1);
  }

  function toggleSort(colIndex: number) {
    if (sortColumn !== colIndex) {
      setSortColumn(colIndex);
      setSortDirection("asc");
    } else if (sortDirection === "asc") {
      setSortDirection("desc");
    } else {
      setSortColumn(null);
    }
  }

  function toggleExpanded(rowIndex: number) {
    setExpandedRows((prev) => {
      const next = new Set(prev);
      if (next.has(rowIndex)) next.delete(rowIndex);
      else next.add(rowIndex);
      return next;
    });
  }

  return (
    <div className="mb-4">
      <div className="mb-2 flex items-center gap-3">
        <input
          type="text"
          value={filterText}
          onChange={(e) => handleFilterChange(e.target.value)}
          placeholder={filterPlaceholder}
          className={filterBtnCls}
        />
        <span className="text-sm text-gray-500">
          {sorted.length} of {rows.length} row(s)
        </span>
      </div>

      {sorted.length === 0 ? (
        <EmptyState message="No rows match this filter." />
      ) : (
        <>
          <div className="overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
            <div className="overflow-x-auto">
              <table className="w-full border-collapse text-sm">
                <thead>
                  <tr className="bg-gray-50 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                    {headers.map((header, colIndex) => {
                      const isActive = sortColumn === colIndex;
                      return (
                        <th
                          key={colIndex}
                          className="cursor-pointer select-none px-3 py-2 hover:bg-gray-100"
                          onClick={() => toggleSort(colIndex)}
                        >
                          {header}
                          {isActive && <span className="ml-1">{sortDirection === "asc" ? "▲" : "▼"}</span>}
                        </th>
                      );
                    })}
                  </tr>
                </thead>
                <tbody>
                  {pageRows.map((row, i) => {
                    // A stable key across sort/filter re-renders: index within
                    // the full (unfiltered) row set, so expansion state keyed
                    // on that index stays attached to the same logical row.
                    const rowKey = rows.indexOf(row);
                    const isExpanded = expandedRows.has(rowKey);
                    return (
                      <tr key={rowKey} className={`border-t border-gray-100 hover:bg-gray-50 ${i % 2 === 1 ? "bg-gray-50/50" : ""}`}>
                        {row.map((cell, colIndex) => {
                          const isExpandable = expandableColumnIndex === colIndex;
                          const needsTruncation = isExpandable && cell.length > truncateAt;
                          const isLinked = linkColumnIndex === colIndex && !!linkTo && !!cell;
                          return (
                            <td key={colIndex} className="px-3 py-2 align-top">
                              {isLinked ? (
                                <Link
                                  to={linkTo!(cell)}
                                  className="font-medium text-blue-700 hover:underline"
                                >
                                  {cell}
                                </Link>
                              ) : needsTruncation && !isExpanded ? (
                                <>
                                  {cell.slice(0, truncateAt)}…{" "}
                                  <button
                                    className="text-xs font-medium text-blue-600 hover:underline"
                                    onClick={() => toggleExpanded(rowKey)}
                                  >
                                    more ▾
                                  </button>
                                </>
                              ) : (
                                <>
                                  <InlineMarkdown text={cell} />
                                  {needsTruncation && isExpanded && (
                                    <>
                                      {" "}
                                      <button
                                        className="text-xs font-medium text-blue-600 hover:underline"
                                        onClick={() => toggleExpanded(rowKey)}
                                      >
                                        less ▸
                                      </button>
                                    </>
                                  )}
                                </>
                              )}
                            </td>
                          );
                        })}
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </div>

          {pageCount > 1 && (
            <div className="mt-2 flex items-center gap-3">
              <button
                className="rounded border border-gray-300 bg-white px-3 py-1 text-sm text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-50"
                onClick={() => setPage((p) => Math.max(1, p - 1))}
                disabled={currentPage <= 1}
              >
                Prev
              </button>
              <span className="text-sm text-gray-500">
                Page {currentPage} of {pageCount}
              </span>
              <button
                className="rounded border border-gray-300 bg-white px-3 py-1 text-sm text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-50"
                onClick={() => setPage((p) => Math.min(pageCount, p + 1))}
                disabled={currentPage >= pageCount}
              >
                Next
              </button>
            </div>
          )}
        </>
      )}
    </div>
  );
}
