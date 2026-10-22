# Vendored dependencies

This folder makes `device-center-preview` buildable and runnable on its own.
It is a source snapshot copied from the sibling repositories; the app does not
reference anything outside this directory.

## .NET projects

| Folder | Upstream | Purpose |
|---|---|---|
| `VigilWatt.Core` | `vigil-watt/src/VigilWatt.Core` | HID++ transport, Logitech device discovery, battery models |
| `YuandaoTws.Domain` | `NICEHCK_OriG-in/src/YuandaoTws.Domain` | Headset domain models and protocol constants |
| `YuandaoTws.Application` | `NICEHCK_OriG-in/src/YuandaoTws.Application` | Headset application services |
| `YuandaoTws.Infrastructure` | `NICEHCK_OriG-in/src/YuandaoTws.Infrastructure` | Windows Bluetooth/SPP implementation |

`VigilWatt.Core` embeds `Plugins/Logitech/logitech-hidpp.json`; keep that file
next to its `.csproj`.

## Rust crates

`rust/` is a small Cargo workspace containing `sayall-core` and
`sayall-windows`, copied from `remote-mic-app-windows/crates`. The sidecar
`sidecars/sayall-engine` references these local paths instead of the sibling
repository.

## Updating

These are snapshots, not submodules. When fixing a bug in a vendored project,
make the change here as well (or re-copy the relevant upstream project and
re-run the standalone checks). Do not add a `ProjectReference` that points
outside `device-center-preview`.
