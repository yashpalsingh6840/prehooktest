import { useQueryClient } from "@tanstack/react-query";
import { getApplyTestsInstructions, startRun, type Workspace } from "../api/client";
import AiPromptModal from "./AiPromptModal";

/** The Apply Tests popup: the AI writes the extra tests, then this copies them in. */
export default function ApplyTestsModal({
  workspace,
  onClose,
}: {
  workspace: Workspace;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  return (
    <AiPromptModal
      workspace={workspace}
      onClose={onClose}
      config={{
        noun: "tests",
        runStep: "apply-tests",
        refreshText: "the Applied Tests page",
        copyDetail:
          "It builds each file's test project to check it, removes any file the compiler rejects, and doesn't write tests.",
        doneLabel: "Done, refresh coverage",
        fetchInstructions: getApplyTestsInstructions,
        // Done means the extra tests are in place, so the coverage report must be
        // recomputed against them. Throwing keeps the popup open with the error shown.
        onDone: async (ws) => {
          await startRun(ws.id, "coverage");
          queryClient.invalidateQueries({ queryKey: ["artifacts", ws.id] });
          queryClient.invalidateQueries({ queryKey: ["step-status", ws.id] });
          queryClient.invalidateQueries({ queryKey: ["runs", ws.id] });
        },
      }}
    />
  );
}
