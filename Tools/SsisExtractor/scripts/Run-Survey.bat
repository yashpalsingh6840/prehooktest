@echo off
setlocal enabledelayedexpansion

rem ---------------------------------------------------------------------------
rem  Runs the ssisx portfolio survey over a folder of SSIS packages.
rem
rem  This is the PowerShell-free twin of Run-Survey.ps1, and it exists for one
rem  reason: on a locked-down client machine, running a .ps1 is frequently
rem  refused outright --
rem
rem    File ...\Run-Survey.ps1 cannot be loaded because running scripts is
rem    disabled on this system.
rem
rem  ...and when the execution policy is set by group policy, even
rem  "-ExecutionPolicy Bypass" does not get you past it. A .bat is not subject
rem  to script execution policy at all, and this one calls ssisx.exe directly
rem  rather than shelling out to PowerShell, so there is nothing left to block.
rem
rem  Usage:  Run-Survey.bat "D:\Their\SSIS_Projects"
rem ---------------------------------------------------------------------------

set "SCRIPTDIR=%~dp0"
set "EXE=%SCRIPTDIR%ssisx.exe"
set "PKGFOLDER=%~1"
set "OUTDIR=%SCRIPTDIR%ssis-survey"
set "EXPORTDIR=%SCRIPTDIR%ssis-survey-EXPORT"

if "%PKGFOLDER%"=="" (
    echo.
    echo   Usage: Run-Survey.bat "path\to\ssis\packages"
    echo.
    echo   Scans that folder for .dtproj / .ispac / .dtsx files at any depth and
    echo   writes a survey next to this script.
    echo.
    exit /b 2
)

if not exist "%EXE%" (
    echo ERROR: ssisx.exe not found next to this script ^(%SCRIPTDIR%^).
    echo        Keep Run-Survey.bat and ssisx.exe in the same folder.
    exit /b 2
)

if not exist "%PKGFOLDER%" (
    echo ERROR: package folder not found: %PKGFOLDER%
    exit /b 2
)

echo Scanning : %PKGFOLDER%
echo Writing  : %OUTDIR%
echo.

rem The report pass. Exit 2 here means "some packages could not be read" -- the
rem survey still completed for the rest, so it is captured and carried to the end
rem rather than aborting.
"%EXE%" report --input "%PKGFOLDER%" --out "%OUTDIR%" --recursive
set "REPORTEXIT=%ERRORLEVEL%"

rem The conformance pass: turns each package into the obligations a rewrite has
rem to satisfy. Cheap, additive, and it is what actually sizes the work.
"%EXE%" conformance --input "%PKGFOLDER%" --out "%OUTDIR%" --recursive

rem Two tiers, deliberately. The full survey holds SQL text, script source,
rem table/column names, file paths and server names -- useful, and the part that
rem usually may not leave the site. The digest is counts only, with package names
rem reduced to PKG-nnn, and is also printed above so it can leave as a screenshot
rem when no file may leave at all.
if exist "%EXPORTDIR%" rmdir /s /q "%EXPORTDIR%"
mkdir "%EXPORTDIR%"
if exist "%OUTDIR%\portfolio-digest.md"   copy /y "%OUTDIR%\portfolio-digest.md"   "%EXPORTDIR%\" >nul
if exist "%OUTDIR%\portfolio-digest.json" copy /y "%OUTDIR%\portfolio-digest.json" "%EXPORTDIR%\" >nul

rem No archive step on purpose. Windows' own tar.exe would do it, but if Git for
rem Windows is installed its tar.exe shadows the system one on PATH and quietly
rem ignores -a, writing an uncompressed TAR under a .zip name -- verified here,
rem a 10 KB "zip" that is not a zip. Handing a client a mislabelled archive is a
rem worse outcome than handing them a folder, and the two digest files together
rem are about 4 KB, so there is nothing to gain by compressing them.

echo.
echo ==============================================================================
echo  FULL SURVEY ^(stays here -- contains SQL, script source, table/server names^)
echo    %OUTDIR%
echo.
echo  CLEARED FOR EXPORT ^(counts only, no names, no code -- about 4 KB total^)
echo    %EXPORTDIR%\portfolio-digest.md
echo    %EXPORTDIR%\portfolio-digest.json
echo.
echo  If no file may leave: screenshot or photograph the digest printed above.
echo  It is the whole picture needed to size the migration.
echo ==============================================================================

if "%REPORTEXIT%"=="2" (
    echo.
    echo NOTE: some packages could not be read and are absent from the survey.
    echo       See load-failures.md in the output folder for which ones and why.
)

exit /b %REPORTEXIT%
