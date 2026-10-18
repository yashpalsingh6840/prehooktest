import { Router } from "express";
import { execFile } from "node:child_process";

const router = Router();

// PowerShell script that shows the standard Windows folder dialog. -STA is
// required for WinForms dialogs. The initial folder comes in through an env
// var rather than being spliced into the script, so no path needs escaping.
// A hidden TopMost owner form keeps the dialog in front of the browser window.
const PICK_FOLDER_SCRIPT = `
Add-Type -AssemblyName System.Windows.Forms
$owner = New-Object System.Windows.Forms.Form
$owner.TopMost = $true
$owner.ShowInTaskbar = $false
$owner.StartPosition = 'CenterScreen'
$owner.Opacity = 0
$owner.Show()
$dialog = New-Object System.Windows.Forms.FolderBrowserDialog
$dialog.Description = 'Choose a folder'
$dialog.ShowNewFolderButton = $true
if ($env:SSISUI_INITIAL -and (Test-Path -LiteralPath $env:SSISUI_INITIAL -PathType Container)) {
  $dialog.SelectedPath = $env:SSISUI_INITIAL
}
$result = $dialog.ShowDialog($owner)
$owner.Close()
if ($result -eq [System.Windows.Forms.DialogResult]::OK) {
  Write-Output $dialog.SelectedPath
}
`;

/**
 * Opens the native folder picker on the machine running this server, starting
 * at `initialPath` when it is an existing folder. Returns the chosen path, or
 * `path: null` if the user cancelled. Only works where the server runs on the
 * same desktop as the user (the intended single-user setup).
 */
router.post("/pick-folder", (req, res) => {
  const initial = typeof req.body?.initialPath === "string" ? req.body.initialPath : "";

  execFile(
    "powershell.exe",
    ["-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-Command", PICK_FOLDER_SCRIPT],
    {
      // The dialog blocks until the user answers, so allow a generous wait.
      timeout: 10 * 60 * 1000,
      windowsHide: true,
      env: { ...process.env, SSISUI_INITIAL: initial },
    },
    (error, stdout, stderr) => {
      if (error) {
        res.status(500).json({
          error: `Could not open the folder picker: ${stderr.trim() || error.message}`,
        });
        return;
      }
      const chosen = stdout.trim();
      res.json({ path: chosen.length > 0 ? chosen : null });
    },
  );
});

export default router;
