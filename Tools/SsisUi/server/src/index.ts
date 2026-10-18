import express from "express";
import "./db.js";
import workspacesRouter from "./routes/workspaces.js";
import fsRouter from "./routes/fs.js";
import runsRouter from "./routes/runs.js";
import artifactsRouter from "./routes/artifacts.js";
import fillsRouter from "./routes/fills.js";
import settingsRouter from "./routes/settings.js";
import systemRouter from "./routes/system.js";

const app = express();
const PORT = 5174;

app.use(express.json());

app.get("/api/health", (_req, res) => {
  res.json({ ok: true, time: new Date().toISOString() });
});

app.use("/api/workspaces", workspacesRouter);
app.use("/api/fs", fsRouter);
app.use("/api/runs", runsRouter);
app.use("/api/workspaces/:id/artifacts", artifactsRouter);
app.use("/api/workspaces/:id/fills", fillsRouter);
app.use("/api/workspaces/:id/system", systemRouter);
app.use("/api/settings", settingsRouter);

app.listen(PORT, () => {
  console.log(`SsisUi server listening on http://localhost:${PORT}`);
});
