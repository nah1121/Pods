# MagicAAP driver notes

Reverse-engineering notes for `MagicAAP.sys`, the kernel transport this project talks
to. Decompiled with [Ghidra](https://ghidra-sre.org/) 12.1.4 (headless).

---

## Provenance

The binary embeds its PDB path:

```
C:\Users\steam\Local Storage\Repos\steam3d\MagicAAP\MagicAAP\x64\Release\MagicAAP.pdb
```

`steam3d` is the author of [MagicPodsCore](https://github.com/steam3d/MagicPodsCore), so
the driver and the reference protocol implementation come from the same person.

Structure names in `.rdata` — `BTHECHO_CONNECTION`, `BTHECHOSAMPLE_CLIENT_CONTEXT`,
`BTHECHOSAMPLE_CLIENT_FILE_CONTEXT` — show it is a derivative of the **Microsoft BTHECHO
KMDF sample**, not a from-scratch driver.

---

## Binary layout

x64 PE, image base `0x140000000`, 30,800 bytes.

| section | VA | size | notes |
|---|---|---|---|
| `.text` | `0x1000` | `0x2900` | ~10 KB of real code |
| `.rdata` | `0x4000` | `0x0c2c` | strings, the WDF function table |
| `.data` | `0x5000` | `0x400` | |
| `.pdata` | `0x6000` | `0x2c4` | unwind info |
| `PAGE` | `0x7000` | `0xc0c` | pageable code |
| `INIT` | `0x8000` | `0x382` | `DriverEntry` |
| `.reloc` | `0x9000` | `0x44` | |

Entry point `entry` @ `0x140002eb0` → KMDF stub → `WdfDriverCreate`.

All WDF calls are indirect through the `WDF_FUNCTIONS` table at `0x1400053a0` via
`_guard_dispatch_icall`, so the decompiler cannot name them — the call sites have to be
read by hand.

---

## The IO queue

`FUN_140001d80` builds the `WDF_IO_QUEUE_CONFIG` used by `WdfIoQueueCreate`:

```
local_48  = 0x38;            // sizeof(WDF_IO_QUEUE_CONFIG)
uStack_30 = 0x100000001;     // Type = 1, Flags = 1
```

* `Type = 1` → `WdfIoQueueReadSynchronous`
* `Flags = 1` → `WdfIoQueueDispatchSequential`

This is the single most important fact about the driver: **one IO at a time, in order**.
A pending read blocks every subsequent write. See
[PROTOCOL.md](PROTOCOL.md#the-io-queue-is-synchronous-and-sequential).

---

## IOCTLs

All three are **outbound** — driver to the Bluetooth miniport, not user-mode entry
points.

| IOCTL | buffer | purpose |
|---|---|---|
| `0x410003` | — | send an L2CAP payload to the AirPods |
| `0x41000b` | `0x110` (272 B) | query / attach; result cached at `param_1+0x80` |
| `0x410220` | `0x18` | connect / open; results at `param_1+0x60/0x68/0x70` |

Issuing them from user mode fails outright, which is why the `0x410003` in earlier
reverse-engineering notes was misleading — it is the driver→miniport direction, not a
user-mode IOCTL.

---

## Connection bring-up

`FUN_140001f30` is the device-start path:

1. Resolve the **`wbth`** interface. The literal `0x68746277` is `'w','b','t','h'` in
   little-endian — this reaches the Windows Bluetooth port driver underneath.
2. `FUN_1400022e8(devObj, target, wbth, 0x78)` — L2CAP registration.
3. Store `*(wbth + 0x70)` at `param_1+0x58`.
4. `FUN_140001e68` → `IOCTL 0x410220` to open the channel.

The bring-up runs on device start regardless of which user-mode client opened the
interface, which is why writes work even when another client already holds the device.

---

## Reproducing the analysis

```powershell
C:\Users\nah\Programs\ghidra\ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat `
  <project-dir> MagicAAP -import MagicAAP.sys `
  -scriptPath <scripts> -postScript DumpAll.java
```

`DumpAll.java` decompiles every function to `decompiled.c` and dumps the function and
symbol tables. 73 functions were recovered.

---

## Open questions

* **What marks a handle as the data sink?** The driver delivers the push stream to one
  reader. `FUN_140002068` (638 bytes) handles the `0x410003` response path and is the
  most likely place the answer lives.
* **Why do ear-detection frames only appear in the startup burst?** Possibly a
  notification-registration flag set during bring-up.
* **`cmd 0x17`** (firmware info) carries XMP property blobs that are still undecoded —
  `MaxReportSize`, `RepDescriptor`, `RangeReport`, `VendorID`, `VendorVersion`.
