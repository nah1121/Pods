# Changelog

All notable changes to this project are documented here.

## [0.1.0] - 2026-10-22

First public release.

### Added
- WPF desktop app: noise control, conversation awareness, press speed, press-and-hold
  duration, volume swipe and length, personalized volume, noise cancellation with one
  AirPod, tone volume, adaptive audio noise.
- Live battery for both buds and the case, with charging state.
- Live device identity (model, serial, firmware) and per-bud in-ear state.
- Settings persist between launches and are restored on connect.
- `pods-cli` command-line interface for scripting.
- `frame-monitor` — a live frame monitor with multi-select and burst tagging, for
  working out what an unknown code means.
- `aapprobe` — a command-line protocol debugger.
- Unit tests covering the packet layouts and frame decoders.
- Protocol and driver reverse-engineering notes in `docs/`.

### Fixed during development
- Device discovery is registry-based; `SetupAPI` does not enumerate this interface.
- The driver's IO queue is synchronous and sequential, so a pending read starves writes.
  Writes cancel the pending read with `CancelIoEx` first.
- A setting is only accepted after the startup sequence, a settle delay, and a repeated
  write — a lone write reports success and is silently discarded.
- Reconnect re-enumerates the device path, because Windows creates a new BTHENUM
  instance when the AirPods come back.
- Battery levels keep the last good value: the buds and the case drop out of reporting
  for a few seconds at a time, and treating that as "0%" made the display flicker.
- A named mutex stops two Pods apps from silently fighting over the device.

### Known limitations
- Six of the eleven settings are read back by the AirPods; the other five are
  write-only, so the app cannot confirm they applied. This is a property of the
  hardware — see `docs/PROTOCOL.md`.
- The byte order the device uses for the per-bud states is not fixed, so the left/right
  mapping is resolved from the charging state and otherwise only counts are shown.
- The driver delivers its push stream to a single client, so only one AAP client can
  be active at a time.
