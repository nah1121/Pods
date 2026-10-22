# Changelog

All notable changes to this project are documented here.

## [0.1.0] - 2026-10-22

First public release.

### Added
- WPF desktop app: noise control, conversation awareness, press speed, press-and-hold
  duration, volume swipe and length, personalized volume, noise cancellation with one
  AirPod, tone volume, adaptive audio noise.
- Live battery for both buds and the case, live device identity (model, serial,
  firmware), ear detection.
- Settings persist between launches and are restored on connect.
- `pods-cli` command-line interface for scripting.
- `aapprobe` protocol debugger for inspecting raw frames.
- Unit tests covering the packet layouts and frame decoders.
- Protocol and driver reverse-engineering notes in `docs/`.

### Known limitations
- Six of the eleven settings are read back by the AirPods; the other five are
  write-only, so the app cannot confirm they applied. This is a property of the
  hardware — see `docs/PROTOCOL.md`.
- The left/right bit mapping in the ear-detection frame is an assumption, pending
  confirmation.
- The driver delivers its push stream to a single client, so only one AAP client can
  be active at a time.
