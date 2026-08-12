# Teardown Boundary Remover

A Windows utility for removing supported boundary elements from Teardown maps.

## Download

Download `TeardownBoundaryRemover.exe` from the [latest release](https://github.com/Capt-Speed/TeardownBoundaryRemover/releases/latest).

The released EXE is self-contained. Players do not need to install .NET.

## Usage

1. Run the application.
2. Select the map sources and click **Scan**.
3. Select the maps to change.
4. Click **Backup and remove boundary**.
5. Use **Restore latest backup** if a change needs to be undone.

The scanner supports game maps, Steam Workshop maps, local maps, and folders added by the user.

## Safety

- Only selected maps are changed.
- XML editing removes exact lowercase `<boundary>` elements. Groups named `Boundary` are not removed.
- Files are backed up before they are changed.
- A file changed after scanning is rejected and must be scanned again.
- Binary map editing is a separate action and requires an additional warning confirmation.
- Game or Workshop updates may replace modified files.

## Build

The .NET 8 SDK is required only to build from source.

```powershell
build-win-x64.cmd
```

The executable is written to `publish\win-x64`.

## License

See [LICENSE](LICENSE).
