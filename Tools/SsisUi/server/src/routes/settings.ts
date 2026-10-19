import fs from "node:fs";
import { Router } from "express";
import { z } from "zod";
import {
  defaultExePaths,
  describeResolution,
  getOverride,
  defaultPublishScript,
  getPublishScriptOverride,
  getValidatorOverride,
  resolvePublishScript,
  setOverride,
  setPublishScriptOverride,
  setValidatorOverride,
} from "../pipeline/toolResolver.js";

const router = Router();

const settingsSchema = z.object({
  ssisxExePath: z.string().nullable().optional(),
  svkExePath: z.string().nullable().optional(),
  validatorExePath: z.string().nullable().optional(),
  publishScriptPath: z.string().nullable().optional(),
});

function currentState() {
  return {
    ssisxExePath: getOverride("ssisx"),
    svkExePath: getOverride("svk"),
    validatorExePath: getValidatorOverride(),
    publishScriptPath: getPublishScriptOverride(),
    resolvedPublishScript: resolvePublishScript(),
    defaultPublishScript: defaultPublishScript(),
    resolution: describeResolution(),
    defaultExePaths: defaultExePaths(),
  };
}

router.get("/", (_req, res) => {
  res.json(currentState());
});

router.put("/", (req, res) => {
  const parsed = settingsSchema.safeParse(req.body);
  if (!parsed.success) {
    res.status(400).json({ error: parsed.error.flatten().fieldErrors });
    return;
  }

  const { ssisxExePath, svkExePath, validatorExePath, publishScriptPath } = parsed.data;

  for (const [field, value] of [
    ["ssisxExePath", ssisxExePath] as const,
    ["svkExePath", svkExePath] as const,
    ["validatorExePath", validatorExePath] as const,
  ]) {
    if (value === undefined) continue; // field omitted -> leave that override untouched
    const trimmed = value?.trim() || null;
    if (trimmed && !trimmed.toLowerCase().endsWith(".exe")) {
      res.status(400).json({ error: `${field} must end in .exe` });
      return;
    }
    if (trimmed && !fs.existsSync(trimmed)) {
      res
        .status(400)
        .json({ error: `${field}: no file exists at "${trimmed}"` });
      return;
    }
  }

  if (publishScriptPath !== undefined) {
    const trimmed = publishScriptPath?.trim() || null;
    if (trimmed && !trimmed.toLowerCase().endsWith(".ps1")) {
      res.status(400).json({ error: "publishScriptPath must end in .ps1" });
      return;
    }
    if (trimmed && !fs.existsSync(trimmed)) {
      res.status(400).json({ error: `publishScriptPath: no file exists at "${trimmed}"` });
      return;
    }
    setPublishScriptOverride(trimmed);
  }

  if (ssisxExePath !== undefined) {
    setOverride("ssisx", ssisxExePath?.trim() || null);
  }
  if (svkExePath !== undefined) {
    setOverride("svk", svkExePath?.trim() || null);
  }
  if (validatorExePath !== undefined) {
    setValidatorOverride(validatorExePath?.trim() || null);
  }

  res.json(currentState());
});

export default router;
