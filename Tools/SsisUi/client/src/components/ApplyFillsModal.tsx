import { useQueryClient } from "@tanstack/react-query";
import { getApplyFillsInstructions, type Workspace } from "../api/client";
import AiPromptModal from "./AiPromptModal";

/** The Apply Fills popup: the AI writes the fills, then this copies them in. */
export default function ApplyFillsModal({
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
        noun: "fills",
        runStep: "apply-fills",
        copilotSlashCommand: "/ssisx-fill",
        refreshText: "the Gaps and Fills pages",
        copyDetail: "It doesn't write fills.",
        doneLabel: "Done, refresh",
        fetchInstructions: getApplyFillsInstructions,
        onDone: (ws) => {
          queryClient.invalidateQueries({ queryKey: ["artifacts", ws.id] });
          queryClient.invalidateQueries({ queryKey: ["step-status", ws.id] });
        },
      }}
    />
  );
}
