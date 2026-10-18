import { useEffect, useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  createWorkspace,
  deleteWorkspace,
  listWorkspaces,
  pickFolder,
  updateWorkspace,
  type Framework,
  type Workspace,
  type WorkspaceInput,
} from "../api/client";
import LoadingState from "../components/LoadingState";
import ErrorState from "../components/ErrorState";
import EmptyState from "../components/EmptyState";
import { formatDateTime } from "../utils/formatDateTime";

const emptyForm: WorkspaceInput = {
  name: "",
  inputPath: "",
  outputPath: "",
  fillsPath: "",
  framework: "net10.0",
};

type BrowseTarget = "inputPath" | "outputPath" | "fillsPath" | null;

const PAGE_SIZE = 10;

const inputCls = "w-full rounded border border-gray-300 px-2 py-1.5 text-sm";
const labelCls = "flex flex-col gap-1 text-sm font-medium text-gray-700";
const btnPrimary =
  "rounded bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-50";
const btnSecondary =
  "rounded border border-gray-300 px-3 py-1.5 text-sm hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-50";

export default function WorkspacePicker() {
  const queryClient = useQueryClient();
  const { data: workspaces, isLoading, error } = useQuery({
    queryKey: ["workspaces"],
    queryFn: listWorkspaces,
  });

  const [editingId, setEditingId] = useState<string | null>(null);
  const [form, setForm] = useState<WorkspaceInput>(emptyForm);
  // Which path field's native folder dialog is currently open, if any.
  const [browsing, setBrowsing] = useState<BrowseTarget>(null);
  // Once the user edits Fills by hand, stop deriving it from the Output path.
  const [fillsTouched, setFillsTouched] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const [showModal, setShowModal] = useState(false);
  const [query, setQuery] = useState("");
  const [page, setPage] = useState(1);
  // The workspace awaiting delete confirmation; the modal is shown while set.
  const [pendingDelete, setPendingDelete] = useState<Workspace | null>(null);

  function invalidate() {
    queryClient.invalidateQueries({ queryKey: ["workspaces"] });
  }

  const createMutation = useMutation({
    mutationFn: createWorkspace,
    onSuccess: () => {
      invalidate();
      resetForm();
    },
    onError: (err: unknown) =>
      setFormError(err instanceof Error ? err.message : String(err)),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, data }: { id: string; data: WorkspaceInput }) =>
      updateWorkspace(id, data),
    onSuccess: () => {
      invalidate();
      resetForm();
    },
    onError: (err: unknown) =>
      setFormError(err instanceof Error ? err.message : String(err)),
  });

  const deleteMutation = useMutation({
    mutationFn: ({ id, deleteOutput }: { id: string; deleteOutput: boolean }) =>
      deleteWorkspace(id, deleteOutput),
    onSuccess: () => {
      setPendingDelete(null);
      invalidate();
    },
  });

  function resetForm() {
    setEditingId(null);
    setForm(emptyForm);
    setFormError(null);
    setFillsTouched(false);
    setShowModal(false);
  }

  function openCreate() {
    setEditingId(null);
    setForm(emptyForm);
    setFormError(null);
    setFillsTouched(false);
    setShowModal(true);
  }

  function startEdit(ws: Workspace) {
    setEditingId(ws.id);
    setForm({
      name: ws.name,
      inputPath: ws.inputPath,
      outputPath: ws.outputPath,
      fillsPath: ws.fillsPath,
      framework: ws.framework,
    });
    // An existing workspace keeps its own fills path, even if it differs from
    // the default -- only a new workspace derives it from the output path.
    setFillsTouched(true);
    setFormError(null);
    setShowModal(true);
  }

  /** The default Fills path: a `fills` folder inside the Output path. */
  function deriveFillsPath(outputPath: string): string {
    const trimmed = outputPath.trim().replace(/[\\/]+$/, "");
    return trimmed ? `${trimmed}\\fills` : "";
  }

  function changeOutputPath(v: string) {
    setForm((f) => ({
      ...f,
      outputPath: v,
      // Follow the output path until the user edits Fills themselves.
      ...(fillsTouched ? {} : { fillsPath: deriveFillsPath(v) }),
    }));
  }

  function changeFillsPath(v: string) {
    setFillsTouched(true);
    setForm((f) => ({ ...f, fillsPath: v }));
  }

  // Opens the native folder dialog for one path field. The field is only
  // changed when the user picks a folder -- cancelling leaves it untouched.
  async function browse(target: Exclude<BrowseTarget, null>) {
    setBrowsing(target);
    setFormError(null);
    try {
      const result = await pickFolder(form[target] || undefined);
      if (result.path) {
        setForm((f) => ({ ...f, [target]: result.path! }));
      }
    } catch (e) {
      setFormError(e instanceof Error ? e.message : String(e));
    } finally {
      setBrowsing(null);
    }
  }

  function submit(e: FormEvent) {
    e.preventDefault();
    setFormError(null);
    if (editingId) {
      updateMutation.mutate({ id: editingId, data: form });
    } else {
      createMutation.mutate(form);
    }
  }

  const saving = createMutation.isPending || updateMutation.isPending;

  // Name search, then paging over the matches. Clamp the page so a shrinking
  // list (e.g. after a delete) never leaves the view on an empty page.
  const needle = query.trim().toLowerCase();
  const matches = (workspaces ?? []).filter((ws) => ws.name.toLowerCase().includes(needle));
  const pageCount = Math.max(1, Math.ceil(matches.length / PAGE_SIZE));
  const currentPage = Math.min(page, pageCount);
  const pageStart = (currentPage - 1) * PAGE_SIZE;
  const pageItems = matches.slice(pageStart, pageStart + PAGE_SIZE);

  return (
    <main className="p-6">
      <div className="mb-4 flex items-center justify-between">
        <h1 className="text-xl font-semibold">SsisUi — Workspaces</h1>
        <button type="button" className={btnPrimary} onClick={openCreate}>
          New workspace
        </button>
      </div>

      <section>
        <div className="mb-3 flex flex-wrap items-center justify-between gap-3">
          <h2 className="text-sm font-semibold uppercase tracking-wide text-gray-500">
            Existing workspaces
          </h2>
          {workspaces && workspaces.length > 0 && (
            <input
              type="search"
              value={query}
              onChange={(e) => {
                setQuery(e.target.value);
                setPage(1);
              }}
              placeholder="Search by name..."
              aria-label="Search workspaces by name"
              className="w-64 rounded border border-gray-300 px-3 py-1.5 text-sm shadow-sm focus:border-blue-500 focus:outline-none"
            />
          )}
        </div>
        {isLoading && <LoadingState />}
        {error && <ErrorState error={error} />}
        {workspaces && workspaces.length === 0 && (
          <EmptyState message="No workspaces yet." />
        )}
        {workspaces && workspaces.length > 0 && matches.length === 0 && (
          <EmptyState message={`No workspace matches "${query.trim()}".`} />
        )}
        {matches.length > 0 && (
          <div className="overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
            <table className="w-full border-collapse text-sm">
              <thead>
                <tr className="bg-gray-50 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                  <th className="px-3 py-2">Name</th>
                  <th className="px-3 py-2">Input</th>
                  <th className="px-3 py-2">Output</th>
                  <th className="px-3 py-2">Fills</th>
                  <th className="px-3 py-2">Framework</th>
                  <th className="whitespace-nowrap px-3 py-2">Last run</th>
                  <th className="px-3 py-2"></th>
                </tr>
              </thead>
              <tbody>
                {pageItems.map((ws, i) => (
                  <tr
                    key={ws.id}
                    className={`border-t border-gray-100 hover:bg-gray-50 ${
                      i % 2 === 1 ? "bg-gray-50/50" : ""
                    }`}
                  >
                    <td className="px-3 py-2">
                      <Link to={`/workspaces/${ws.id}`} className="font-medium text-blue-700 hover:underline">
                        {ws.name}
                      </Link>
                    </td>
                    <td className="max-w-[220px] truncate px-3 py-2 font-mono text-xs" title={ws.inputPath}>
                      {ws.inputPath}
                    </td>
                    <td className="max-w-[220px] truncate px-3 py-2 font-mono text-xs" title={ws.outputPath}>
                      {ws.outputPath}
                    </td>
                    <td className="max-w-[220px] truncate px-3 py-2 font-mono text-xs" title={ws.fillsPath}>
                      {ws.fillsPath}
                    </td>
                    <td className="px-3 py-2">{ws.framework}</td>
                    <td className="whitespace-nowrap px-3 py-2 text-xs text-gray-600">
                      {ws.lastRunAt ? (
                        formatDateTime(ws.lastRunAt)
                      ) : (
                        <span className="italic text-gray-400">Never run</span>
                      )}
                    </td>
                    <td className="whitespace-nowrap px-3 py-2">
                      <button className={`${btnSecondary} mr-1.5`} onClick={() => startEdit(ws)}>
                        Edit
                      </button>
                      <button
                        className="rounded border border-red-200 px-3 py-1.5 text-sm text-red-700 hover:bg-red-50"
                        onClick={() => setPendingDelete(ws)}
                      >
                        Delete
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            {pageCount > 1 && (
              <div className="flex items-center justify-between border-t border-gray-100 bg-gray-50 px-3 py-2 text-xs text-gray-600">
                <span>
                  Showing {pageStart + 1}–{pageStart + pageItems.length} of {matches.length}
                </span>
                <div className="flex items-center gap-2">
                  <button
                    type="button"
                    className={btnSecondary}
                    onClick={() => setPage(currentPage - 1)}
                    disabled={currentPage <= 1}
                  >
                    Previous
                  </button>
                  <span className="tabular-nums">
                    Page {currentPage} of {pageCount}
                  </span>
                  <button
                    type="button"
                    className={btnSecondary}
                    onClick={() => setPage(currentPage + 1)}
                    disabled={currentPage >= pageCount}
                  >
                    Next
                  </button>
                </div>
              </div>
            )}
          </div>
        )}
      </section>

      {pendingDelete && (
        <DeleteConfirmModal
          workspace={pendingDelete}
          deleting={deleteMutation.isPending}
          error={deleteMutation.error instanceof Error ? deleteMutation.error.message : null}
          onCancel={() => {
            deleteMutation.reset();
            setPendingDelete(null);
          }}
          onConfirm={(deleteOutput) =>
            deleteMutation.mutate({ id: pendingDelete.id, deleteOutput })
          }
        />
      )}

      {showModal && (
        // Clicking the backdrop no longer closes the form, so an accidental
        // click or a text-selection drag out of a field can't lose your input.
        // Close with the Cancel button instead.
        <div className="fixed inset-0 z-40 flex items-center justify-center bg-black/40">
          <div
            className="max-h-[90vh] w-[560px] overflow-y-auto rounded-lg bg-white p-5 shadow-xl"
            onClick={(e) => e.stopPropagation()}
          >
            <h2 className="mb-3 text-sm font-semibold uppercase tracking-wide text-gray-500">
              {editingId ? "Edit workspace" : "New workspace"}
            </h2>
            <form onSubmit={submit} className="grid gap-3">
              <label className={labelCls}>
                Name
                <input
                  className={inputCls}
                  value={form.name}
                  onChange={(e) => setForm({ ...form, name: e.target.value })}
                />
              </label>

              <PathField
                label="Input path"
                value={form.inputPath}
                onChange={(v) => setForm({ ...form, inputPath: v })}
                browsing={browsing === "inputPath"}
                onBrowse={() => browse("inputPath")}
              />
              <PathField
                label="Output path"
                value={form.outputPath}
                onChange={changeOutputPath}
                browsing={browsing === "outputPath"}
                onBrowse={() => browse("outputPath")}
              />
              <PathField
                label="Fills path"
                value={form.fillsPath}
                onChange={changeFillsPath}
                browsing={browsing === "fillsPath"}
                onBrowse={() => browse("fillsPath")}
                showBrowse={false}
                hint="Defaults to a fills folder inside the Output path. Edit it only if your fills live elsewhere."
              />

              <label className={labelCls}>
                Framework
                <select
                  className={inputCls}
                  value={form.framework}
                  onChange={(e) =>
                    setForm({
                      ...form,
                      framework: e.target.value as Framework,
                    })
                  }
                >
                  <option value="net8.0">net8.0</option>
                  <option value="net10.0">net10.0</option>
                </select>
              </label>

              {formError && <ErrorState error={formError} />}

              <div className="flex gap-2">
                <button type="submit" className={btnPrimary} disabled={saving}>
                  {editingId ? "Save changes" : "Create workspace"}
                </button>
                <button type="button" className={btnSecondary} onClick={resetForm}>
                  Cancel
                </button>
              </div>
            </form>
          </div>
        </div>
      )}

    </main>
  );
}

/** Confirms before removing a workspace. Only the SsisUi entry is deleted; the
 * input, output and fills folders on disk are left exactly as they are. */
function DeleteConfirmModal({
  workspace,
  deleting,
  error,
  onCancel,
  onConfirm,
}: {
  workspace: Workspace;
  deleting: boolean;
  error: string | null;
  onCancel: () => void;
  onConfirm: (deleteOutput: boolean) => void;
}) {
  const [deleteOutput, setDeleteOutput] = useState(false);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape" && !deleting) onCancel();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onCancel, deleting]);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-gray-900/40 p-4">
      <div role="alertdialog" aria-modal="true" aria-labelledby="delete-workspace-title" className="w-full max-w-md rounded-lg bg-white p-5 shadow-xl">
        <h2 id="delete-workspace-title" className="text-base font-semibold text-gray-900">
          Delete workspace?
        </h2>
        <p className="mt-2 text-sm text-gray-700">
          <span className="font-semibold">{workspace.name}</span> will be removed from SsisUi, along with
          its run history.
        </p>
        <label className="mt-3 flex items-start gap-2 rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-900">
          <input
            type="checkbox"
            className="mt-0.5"
            checked={deleteOutput}
            onChange={(e) => setDeleteOutput(e.target.checked)}
            disabled={deleting}
          />
          <span>
            Also delete the output folder and everything in it (generated code, logs, reports).
            <span className="mt-1 block break-all font-mono text-xs text-red-800">{workspace.outputPath}</span>
          </span>
        </label>
        <p className="mt-2 text-xs text-gray-600">
          The input folder (your source packages) and the fills folder (hand-written fills) are{" "}
          <strong>never</strong> deleted.
        </p>
        {error && <p className="mt-3 text-sm text-red-700">{error}</p>}
        <div className="mt-5 flex justify-end gap-2">
          <button type="button" className={btnSecondary} onClick={onCancel} disabled={deleting}>
            Cancel
          </button>
          <button
            type="button"
            onClick={() => onConfirm(deleteOutput)}
            disabled={deleting}
            className="rounded bg-red-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-red-700 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {deleting ? "Deleting..." : "Delete workspace"}
          </button>
        </div>
      </div>
    </div>
  );
}

function PathField({
  label,
  value,
  onChange,
  browsing,
  onBrowse,
  showBrowse = true,
  hint,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  browsing: boolean;
  onBrowse: () => void;
  showBrowse?: boolean;
  hint?: string;
}) {
  // The path stays typeable while the native dialog is open or after it closes,
  // so a path you already have can be pasted in directly.
  return (
    <label className={labelCls}>
      {label}
      <div className="flex gap-2">
        <input
          className={`${inputCls} flex-1`}
          value={value}
          onChange={(e) => onChange(e.target.value)}
        />
        {showBrowse && (
          <button
            type="button"
            className={`${btnSecondary} cursor-pointer disabled:cursor-not-allowed disabled:opacity-50`}
            onClick={onBrowse}
            disabled={browsing}
          >
            {browsing ? "Opening…" : "Browse…"}
          </button>
        )}
      </div>
      {hint && <span className="text-xs font-normal text-gray-500">{hint}</span>}
    </label>
  );
}
