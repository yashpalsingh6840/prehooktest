import fs from "node:fs";
import { Router } from "express";
import { z } from "zod";
import {
  describeResolution,
  getOverride,
  getValidatorOverride,
  setOverride,
  setValidatorOverride,
} from "../pipeline/toolResolver.js";

const router = Router();

const settingsSchema = z.object({
  ssisxExePath: z.string().nullable().optional(),
  svkExePath: z.string().nullable().optional(),
  validatorExePath: z.string().nullable().optional(),
});

function currentState() {
  return {
    ssisxExePath: getOverride("ssisx"),
    svkExePath: getOverride("svk"),
    validatorExePath: getValidatorOverride(),
    resolution: describeResolution(),
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

  const { ssisxExePath, svkExePath, validatorExePath } = parsed.data;

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
