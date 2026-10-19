import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  getSettings,
  updateSettings,
  type ResolutionInfo,
} from "../api/client";
import LoadingState from "../components/LoadingState";
import ErrorState from "../components/ErrorState";
import NavIcon from "../components/NavIcon";

type ToolKey = "ssisx" | "svk" | "validator";

const TOOL_LABEL: Record<ToolKey, string> = {
  ssisx: "SSIS Extract/Rewrite EXE Path",
  svk: "SSIS Walkthrough EXE Path",
  validator: "SSIS Rewrite Validation EXE Path",
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

// Hidden for now: the publish script box is not used from the UI. Flip to true to show it again.
const SHOW_PUBLISH_SCRIPT = false;

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
  const [publishInput, setPublishInput] = useState("");
  const [formError, setFormError] = useState<string | null>(null);

  // The box shows the saved override if there is one, else the exe the tool is expected at
  // (defaultExePaths from the server -- the same locations auto-detection looks at).
  useEffect(() => {
    if (data) {
      setInputs({
        ssisx: data.ssisxExePath ?? data.defaultExePaths.ssisx,
        svk: data.svkExePath ?? data.defaultExePaths.svk,
        validator: data.validatorExePath ?? data.defaultExePaths.validator,
      });
      setPublishInput(data.publishScriptPath ?? data.defaultPublishScript);
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
    if (!data) return;
    const value = inputs[tool].trim();
    const savedOverride = data[FIELD[tool]];
    // Pressing Save on the auto-detected path, with no override saved, must not pin it
    // as an override -- that would stop auto-detection from following a later republish.
    const isUnchangedDetection = !savedOverride && value === data.defaultExePaths[tool];
    mutation.mutate({ [FIELD[tool]]: value && !isUnchangedDetection ? value : null });
  }

  function savePublish() {
    setFormError(null);
    if (!data) return;
    const value = publishInput.trim();
    // Same rule as the tool boxes: saving the checked-in default must not pin it as an override.
    const isUnchangedDefault = !data.publishScriptPath && value === data.defaultPublishScript;
    mutation.mutate({ publishScriptPath: value && !isUnchangedDefault ? value : null });
  }

  function clearPublish() {
    setFormError(null);
    setPublishInput("");
    mutation.mutate({ publishScriptPath: null });
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
          {SHOW_PUBLISH_SCRIPT && (
          <section className="mb-5 rounded-lg border border-gray-200 bg-white p-4 shadow-sm">
            <h2 className="mb-1 text-sm font-semibold uppercase tracking-wide text-gray-500">
              Publish SSIS Extract/Rewrite/Walkthrough/Validation Script Path
            </h2>
            <p className="mb-2 break-all font-mono text-xs text-gray-600">
              {data.resolvedPublishScript
                ? `Run when no ssisx.exe is found, then ssisx is used: ${data.resolvedPublishScript}`
                : `No publish script found. ssisx will use the slower "dotnet run" fallback.`}
            </p>
            <div className="flex gap-2">
              <input
                className="flex-1 rounded border border-gray-300 px-2 py-1.5 text-sm"
                placeholder="Path to Publish-Survey.ps1"
                value={publishInput}
                onChange={(e) => setPublishInput(e.target.value)}
              />
              <button
                type="button"
                className="flex h-[34px] w-[34px] cursor-pointer items-center justify-center rounded bg-blue-600 text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-50"
                disabled={mutation.isPending}
                onClick={savePublish}
                title="Save publish script path"
                aria-label="Save publish script path"
              >
                <NavIcon name="save" className="h-4 w-4" />
              </button>
              <button
                type="button"
                className="flex h-[34px] w-[34px] cursor-pointer items-center justify-center rounded border border-gray-300 bg-white text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-50"
                disabled={mutation.isPending}
                onClick={clearPublish}
                title="Clear saved path (use the checked-in default)"
                aria-label="Clear publish script path"
              >
                <NavIcon name="clear" className="h-4 w-4" />
              </button>
            </div>
          </section>
          )}

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
                    className="flex h-[34px] w-[34px] cursor-pointer items-center justify-center rounded bg-blue-600 text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-50"
                    disabled={mutation.isPending}
                    onClick={() => save(tool)}
                    title="Save override"
                    aria-label={`Save ${TOOL_LABEL[tool]} path`}
                  >
                    <NavIcon name="save" className="h-4 w-4" />
                  </button>
                  <button
                    type="button"
                    className="flex h-[34px] w-[34px] cursor-pointer items-center justify-center rounded border border-gray-300 bg-white text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-50"
                    disabled={mutation.isPending}
                    onClick={() => clear(tool)}
                    title="Clear override (revert to auto-detection)"
                    aria-label={`Clear ${TOOL_LABEL[tool]} override`}
                  >
                    <NavIcon name="clear" className="h-4 w-4" />
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
