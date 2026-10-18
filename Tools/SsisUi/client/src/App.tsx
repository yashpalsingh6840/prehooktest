import { Routes, Route } from "react-router-dom";
import AppShell from "./components/AppShell";
import WorkspacePicker from "./pages/WorkspacePicker";
import WorkspaceDetail from "./pages/WorkspaceDetail";
import Settings from "./pages/Settings";

export default function App() {
  return (
    <AppShell>
      <Routes>
        <Route path="/" element={<WorkspacePicker />} />
        <Route path="/workspaces/:id/*" element={<WorkspaceDetail />} />
        <Route path="/settings" element={<Settings />} />
      </Routes>
    </AppShell>
  );
}
