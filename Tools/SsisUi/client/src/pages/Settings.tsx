import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  getSettings,
  updateSettings,
  type ResolutionInfo,
} from "../api/client";
import LoadingState from "../components/LoadingState";
import ErrorState from "../components/ErrorState";

type ToolKey = "ssisx" | "svk" | "validator";

const TOOL_LABEL: Record<ToolKey, string> = {
  ssisx: "ssisx",
  svk: "svk",
  validator: "SSIS_RewriteValidationTool",
};

const PLACEHOLDER: Record<ToolKey, string> = {
  ssisx: "Path to ssisx.exe",
  svk: "Path to svk.exe",
  validator: "Path to ssis_validator.exe",
};

/** Settings-API field each tool's override is stored under. */
const FIELD: Record<ToolKey, "ssisxExePath" | "svkExePath" | "validatorExePath"> = {
  ssisx: "ssisxExePath",
  svk: "svkExePath",
  validator: "validatorExePath",
};

const btnSecondary = "rounded border border-gray-300 px-3 py-1.5 text-sm hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-50";
const btnPrimary =
  "rounded bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-50";

function describeMode(info: ResolutionInfo): string {
  switch (info.mode) {
    case "override":
      return `Using your override: ${info.path}`;
    case "published":
      return `Using published exe at ${info.path}`;
    case "dotnet-run":
      return `Using "dotnet run" fallback (slower -- publish the exe for faster runs). Command: ${info.path}`;
    case "venv":
      return `Using the validator's own Python venv (created on first run). Command: ${info.path}`;
  }
}

export default function Settings() {
  const queryClient = useQueryClient();
  const { data, isLoading, error } = useQuery({
    queryKey: ["settings"],
    queryFn: getSettings,
  });

  const [inputs, setInputs] = useState<Record<ToolKey, string>>({ ssisx: "", svk: "", validator: "" });
  const [formError, setFormError] = useState<string | null>(null);

  useEffect(() => {
    if (data) {
      setInputs({
        ssisx: data.ssisxExePath ?? "",
        svk: data.svkExePath ?? "",
        validator: data.validatorExePath ?? "",
      });
    }
  }, [data]);

  const mutation = useMutation({
    mutationFn: updateSettings,
    onSuccess: (result) => {
      queryClient.setQueryData(["settings"], result);
      setFormError(null);
    },
    onError: (err: unknown) =>
      setFormError(err instanceof Error ? err.message : String(err)),
  });

  function save(tool: ToolKey) {
    setFormError(null);
    mutation.mutate({ [FIELD[tool]]: inputs[tool] || null });
  }

  function clear(tool: ToolKey) {
    setFormError(null);
    setInputs((prev) => ({ ...prev, [tool]: "" }));
    mutation.mutate({ [FIELD[tool]]: null });
  }

  return (
    <main className="mx-auto max-w-2xl p-6">
      <h1 className="mb-2 text-xl font-semibold">Settings</h1>
      <p className="mb-4 text-sm text-gray-600">
        Override where <code className="rounded bg-gray-100 px-1 py-0.5 text-xs">ssisx.exe</code>,
        <code className="rounded bg-gray-100 px-1 py-0.5 text-xs"> svk.exe</code> and the validator's
        <code className="rounded bg-gray-100 px-1 py-0.5 text-xs"> ssis_validator.exe</code> live, for
        when auto-detection's "dotnet run" fallback is too slow or the validator's own Python venv
        isn't set up, and you've manually published the exes somewhere.
      </p>

      {isLoading && <LoadingState />}
      {error && <ErrorState error={error} />}
      {formError && (
        <div className="mb-3">
          <ErrorState error={formError} />
        </div>
      )}

      {data && (
        <>
          {(["ssisx", "svk", "validator"] as const).map((tool) => {
            const info = data.resolution[tool];
            return (
              <section
                key={tool}
                className="mb-5 rounded-lg border border-gray-200 bg-white p-4 shadow-sm"
              >
                <h2 className="mb-1 text-sm font-semibold uppercase tracking-wide text-gray-500">
                  {TOOL_LABEL[tool]}
                </h2>
                <p className="mb-2 break-all font-mono text-xs text-gray-600">{describeMode(info)}</p>
                <div className="flex gap-2">
                  <input
                    className="flex-1 rounded border border-gray-300 px-2 py-1.5 text-sm"
                    placeholder={PLACEHOLDER[tool]}
                    value={inputs[tool]}
                    onChange={(e) => setInputs((prev) => ({ ...prev, [tool]: e.target.value }))}
                  />
                  <button
                    type="button"
                    className={btnPrimary}
                    disabled={mutation.isPending}
                    onClick={() => save(tool)}
                  >
                    Save
                  </button>
                  <button
                    type="button"
                    className={btnSecondary}
                    disabled={mutation.isPending}
                    onClick={() => clear(tool)}
                  >
                    Clear
                  </button>
                </div>
              </section>
            );
          })}
        </>
      )}
    </main>
  );
}
