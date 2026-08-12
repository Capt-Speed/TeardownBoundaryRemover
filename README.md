# Teardown Boundary Remover

A Windows utility for inspecting Teardown maps, backing up eligible files, and removing confirmed boundary records after explicit user confirmation.

The interface uses Chinese when Windows is configured for a Chinese UI culture; all other UI cultures use English.

## What this version scans

- **Local mods:** Windows' redirected Documents known folder → `Teardown\mods`.
- **Game-native maps:** compiled campaign/sandbox entries from `data\bin\*.bin` are identified and conservatively analyzed; only copy-tested `TDBIN 2.0.4` boundary records are eligible for the separate single-map danger action. Installed built-in/DLC content mods with scene XML are scanned normally.
- **Steam Workshop maps:** discovered from each Steam library under `steamapps\workshop\content\1167630`.
- **Extra locations:** users can add custom folders; those locations are saved in `%LOCALAPPDATA%\TeardownBoundaryRemover\settings.json`.

The source selector at the top lets users independently include game-native, Workshop, and local maps. Normalized paths and XML paths are de-duplicated.

For normal local/Workshop mods, the display name and author are read from `info.txt` when available. For base-game scene XML without `info.txt`, the file/folder name is used as the conservative fallback instead of inventing a title.

Teardown App ID: `1167630`.

## Boundary safety rule

The remover does **not** use regular expressions to rewrite XML.

A target file is eligible only when:

1. It parses successfully with .NET's XML parser.
2. DTD processing and external XML resolution are disabled.
3. It is a Teardown level-like XML; final write operations require a `<scene>` document root.
4. Its root is the namespace-free, exact lowercase element `<scene>`.
5. It contains a namespace-free element whose local element name is exactly `boundary` (case-sensitive).

`<group name="Boundary">` is not a supported target. Current evidence does not establish that it is equivalent to a Teardown boundary entity, so the program reports it as an informational warning and never changes it. This intentionally avoids deleting ordinary group content.

When executing a batch, the utility:

1. Re-checks every selected file's SHA-256 hash and Boundary count against the scan result.
2. Opens every file for read/write as a non-destructive permission probe.
3. Shows a complete list of selected maps/XML files.
4. Requires explicit review confirmation; Workshop/base-game selections require an additional acknowledgement.
5. Copies **all** affected XML files to a dated backup session and verifies each backup SHA-256.
6. Shows a final Yes/No confirmation *after the backup has succeeded*.
7. Loads the XML with whitespace preservation, removes only the `boundary` XML nodes, and writes a temporary file.
8. Parses the temporary file again and performs semantic tree comparison to ensure the remaining XML structure is unchanged.
9. Re-checks the source SHA-256 immediately before an atomic replacement, then verifies that the result parses and contains zero target nodes.
10. If a later file in the same batch fails, restores every file already modified in that batch from the verified backups.

The program never intentionally deletes a map, mod, VOX, Lua file, image, or XML file. Temporary files are the only files it removes.

## Scan performance

Discovery uses a streaming XML reader and computes SHA-256 only for files containing an eligible exact `<boundary>` node. Scene XML files without an eligible target avoid that second full-file read. XML candidates inside a map/mod are parsed concurrently with a conservative cap of 2–4 workers, while results remain deterministically ordered. A local metadata cache (path, size, and last-write time) skips reparsing unchanged XML on later scans; cache contents are never trusted for a write, because full hash and XML checks remain mandatory before backup and before writing a selected file.

Current game-native maps use zlib-compressed proprietary `TDBIN` v2 files. They are never treated as XML. Only the copy-tested `TDBIN 2.0.4` boundary-vertex form is eligible for a separate, one-map-at-a-time danger action with backup, hash verification, post-write validation, and rollback; unknown forms remain read-only.

## Backups

Backups are stored in:

```text
Documents\Teardown Boundary Remover\Backups\<session>\
```

Each session contains `manifest.json` with original paths, original SHA-256, modified SHA-256, source type, Workshop ID (when applicable), and removed Boundary counts.

The UI includes **Restore latest backup**. Restore first verifies the backup hash and refuses to overwrite a current XML unless it is either the recorded original or the exact version previously written by this utility.

## UI / DPI behavior

The program uses Windows Forms and native Windows controls. It enables `PerMonitorV2` DPI awareness and uses docked/percentage/flow layouts rather than fixed screen coordinates. The main window is resizable, has a low minimum size, and buttons wrap in narrow layouts.

The list includes:

- checkbox selection;
- a tri-state **Select all eligible items** checkbox;
- mod/map name;
- source (Local / Workshop / Built-in / Custom);
- level XML count;
- Boundary count;
- safety/status information;
- search, source filtering, and "only show Boundary" filtering;
- read-only XML preview;
- folder opening and backup restore actions.

## Important behavior for Steam content

Workshop files and base-game files are deliberately not treated as permanent user-owned copies. The UI warns that Steam Workshop updates, game updates, or Steam file verification may restore/replace them.

The program does **not** request administrator elevation automatically. If a built-in game XML is not writable, the preflight check blocks the whole batch before any selected XML is modified.

## Build

Requirements:

- Windows 10/11 x64
- .NET 8 SDK (only needed to build; the published EXE is self-contained)

Run:

```bat
build-win-x64.cmd
```

or:

```powershell
dotnet publish src\TeardownBoundaryRemover\TeardownBoundaryRemover.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -o publish\win-x64
```

Output:

```text
publish\win-x64\TeardownBoundaryRemover.exe
```

A GitHub Actions workflow is also included at `.github/workflows/build.yml`; it runs the built-in self-tests and publishes the single-file Windows EXE as an Actions artifact.

For a personal-use protected build, install Dotfuscator Community 7.7 yourself and explicitly provide its `cli` directory:

```powershell
powershell -ExecutionPolicy Bypass -File .\build-protected-win-x64.ps1 `
  -DotfuscatorDirectory "<Dotfuscator Community cli directory>"
```

The script produces a standalone single EXE and a multi-file comparison build using the same renamed application DLL. Keep `protect-map-private` private; never distribute its renaming map.

## Self-test

```powershell
dotnet run --project src\TeardownBoundaryRemover\TeardownBoundaryRemover.csproj -c Release -- --self-test
```

The test suite covers:

- normal self-closing Boundary nodes;
- multiline/nested Boundary nodes;
- conservative rejection/ignore behavior for non-exact case variants such as `<Boundary>`;
- namespace isolation for both `scene` and `boundary`;
- preservation/reporting of `<group name="Boundary">`;
- UTF-16 BOM plus Chinese XML content;
- preservation of sibling XML content;
- DTD rejection;
- restore refusal when an XML was externally changed;
- `info.txt` name/author parsing.

## Why the implementation uses these locations/structures

Teardown's official modding documentation describes local mods under `Documents/Teardown/mods`, `info.txt`, content-mod `main.xml`, multi-scene XML files, built-in mods, the base game's `data` structure, and the fact that built-in content should normally be copied before editing:

- https://teardowngame.com/modding/

The current official Lua API exposes Boundary-reading functions (`GetBoundaryArea` / `GetBoundaryBounds`) but does not expose a corresponding Boundary removal setter:

- https://teardowngame.com/modding/api.html

Steam's official documentation describes app manifests / install state and Workshop content installation behavior:

- https://partner.steamgames.com/doc/sdk/uploading
- https://partner.steamgames.com/doc/features/workshop/implementation

Microsoft Windows Forms documentation for high-DPI behavior:

- https://learn.microsoft.com/dotnet/desktop/winforms/high-dpi-support-in-windows-forms
- https://learn.microsoft.com/dotnet/api/system.windows.forms.application.sethighdpimode

## Prototype limitation worth testing on a real Teardown install

The base-game scanner identifies compiled `data\bin` entries and separately analyzes scene XML from built-in/DLC content mods. Only the copy-tested `TDBIN 2.0.4` boundary form is writable through the isolated danger action; other binary formats remain read-only.

Version 0.5.2 was self-tested on Windows x64 with the .NET 8 SDK in standard, protected single-file, and protected multi-file forms. Self-tests never modify installed Teardown or Workshop files. The optional TDBIN integration test runs only when `TBR_TEST_TDBIN_PATH` points to a known input file, which is copied to a temporary directory before testing.
