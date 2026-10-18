import { useEffect, useState } from "react";
import type { RunStatus } from "../api/client";
import Badge, { type BadgeColor } from "./Badge";
import LogViewer from "./LogViewer";

type StreamEvent =
  | { chunk: string }
  | { done: true; status: RunStatus; exitCode: number | null };

type LogPaneProps = {
  runId: string;
};

export default function LogPane({ runId }: LogPaneProps) {
  const [log, setLog] = useState("");
  const [terminal, setTerminal] = useState<{
    status: RunStatus;
    exitCode: number | null;
  } | null>(null);

  useEffect(() => {
    setLog("");
    setTerminal(null);

    const source = new EventSource(`/api/runs/${runId}/stream`);

    source.onmessage = (event) => {
      const payload = JSON.parse(event.data) as StreamEvent;
      if ("chunk" in payload) {
        setLog((prev) => prev + payload.chunk);
      } else if (payload.done) {
        setTerminal({ status: payload.status, exitCode: payload.exitCode });
        source.close();
      }
    };

    source.onerror = () => {
      // The browser will keep retrying on a transient drop; once the
      // server has genuinely finished writing (done event sent) it closes
      // the connection itself, which also lands here but is harmless.
    };

    return () => {
      source.close();
    };
  }, [runId]);

  return (
    <LogViewer
      text={log}
      follow
      heightClass="max-h-80"
      emptyText="(waiting for output...)"
      footer={
        terminal ? (
          <>
            <Badge color={statusColor(terminal.status)}>{terminal.status}</Badge>
            <span className="text-gray-400">exit code {terminal.exitCode ?? "n/a"}</span>
          </>
        ) : (
          <Badge color="blue">running</Badge>
        )
      }
    />
  );
}

function statusColor(status: RunStatus): BadgeColor {
  switch (status) {
    case "success":
      return "green";
    case "warning":
      return "amber";
    case "failed":
      return "red";
    default:
      return "gray";
  }
}
