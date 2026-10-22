# AAP protocol notes

Everything here was established empirically on Windows 11 against an Airoha AB156x
clone (AirPods Pro 2, model `A2084`, serial `GX5ZK3T9LKKT`, firmware `51.9.6`).

Packet layouts are byte-identical to
[MagicPodsCore](https://github.com/steam3d/MagicPodsCore), the open-source core
published by the MagicPods author.

---

## Transport

| | |
|---|---|
| Channel | Bluetooth **L2CAP PSM 0x1001** — not GATT |
| Driver | `MagicAAP.sys`, a derivative of Microsoft's BTHECHO KMDF sample |
| Interface GUID | `{9eec98bb-3c54-45d4-a843-7900c4635e08}` (lowercase) |
| Device path | `\\?\BTHENUM#{74ec2172-0bad-4d01-8f77-997b2be0722a}_VID&0001004c_PID&200e#7&<instance>#<guid>` |

Windows user-mode code cannot open a raw L2CAP PSM, so a kernel driver is mandatory.
That is why the original app ships one.

### Discovering the device path

`SetupDiEnumDeviceInterfaces` does **not** enumerate this interface, even with
`DICF_PRESENT` cleared. Read it from the registry instead:

```
HKLM\SYSTEM\CurrentControlSet\Control\DeviceClasses\{9eec98bb-3c54-45d4-a843-7900c4635e08}
```

The subkey name is the NT path with its `\\?\` prefix re-encoded as `##?#`. Replacing
that prefix gives the path — and **every other byte must be left alone**, in particular
`BTHENUM#{service-class-guid}` keeps its `#`. Converting those to `\` yields
`ERROR_PATH_NOT_FOUND`.

The GUID in the path must be lowercase: the registry lookup is case-insensitive, the NT
path is not.

### Opening it

No elevation required:

```c
CreateFile(path,
           GENERIC_READ | GENERIC_WRITE,
           FILE_SHARE_READ | FILE_SHARE_WRITE,
           NULL, OPEN_EXISTING, 0, NULL);
```

### The IO queue is synchronous and sequential

The driver creates its queue with `Type = WdfIoQueueReadSynchronous` and
`Flags = WdfIoQueueDispatchSequential`.

Consequences, both directly observed:

* A pending `ReadFile` **owns the queue** — any later `WriteFile` blocks forever.
* All writes must complete before a read is posted.

To write while a read is outstanding, cancel the read first:

```c
CancelIoEx(handle, NULL);   // cancels IO on the handle from any thread
```

`CancelSynchronousIo` does **not** work here — it only cancels synchronous IO issued by
the *calling* thread, so calling it from the writer leaves the reader blocked. Using it
cost 3 s per change until it was replaced with `CancelIoEx`.

---

## Packet format

All commands share an envelope. Byte 4 is the command, byte 6 the sub-command.

```
[0] 0x04   [1] 0x00   [2] 0x04   [3] 0x00   [4] cmd   [5] 0x00   [6] sub   [7..] args
```

### Startup sequence

Sent on every new connection, in this order:

| Packet | Bytes |
|---|---|
| `AapInit` | `00 00 04 00 01 00 02 00 00 00 00 00 00 00 00 00` |
| `EnableNotifications(ef)` | `04 00 04 00 0f 00 ff ff ef ff` |
| `EnableNotifications(ff)` | `04 00 04 00 0f 00 ff ff ff ff` |
| `AapInitExt` (156x only) | `04 00 04 00 4d 00 0e 00 00 00 00 00 00 00` |
| `AapPrivateKeys` | `04 00 04 00 30 00 05 00` |

After this the AirPods dump their full settings state at once.

### Setting a value

```
04 00 04 00 09 00 <sub> <value> 00 00 00
```

| sub | setting | values |
|---|---|---|
| `0x0d` | noise control | 1=off 2=on 3=transparency 4=adaptive |
| `0x17` | press speed | 0=default 1=slower 2=slowest |
| `0x18` | press & hold | 0=default 1=shorter 2=shortest |
| `0x1b` | ANC with one AirPod | 1=on 2=off |
| `0x1f` | tone volume | 15–100 |
| `0x23` | volume swipe length | 0=default 1=longer 2=longest |
| `0x24` | end call / mute | `04 00 04 00 09 00 24 21 <mute> <call> 00` |
| `0x25` | volume swipe | 1=on 2=off |
| `0x26` | personalized volume | 1=on 2=off |
| `0x28` | conversation awareness | 1=on 2=off |
| `0x2e` | adaptive audio noise | 0–100 |

### Applying a setting

A setting is only accepted when **all** of the following hold:

1. the startup sequence was sent on this connection,
2. a settle delay of roughly 2 s has elapsed,
3. the packet is written twice.

Failing any of these, `WriteFile` still reports success and the AirPods silently ignore
the packet. A successful write is therefore **not** evidence that the setting applied.

---

## Which settings are reported

| sub | setting | reported? |
|---|---|---|
| `0x0d` | noise control | yes |
| `0x17` | press speed | yes |
| `0x18` | press & hold | yes |
| `0x1b` | ANC with one AirPod | yes |
| `0x1f` | tone volume | yes |
| `0x24` | end call | yes |
| `0x23` | volume swipe length | **no — write-only** |
| `0x25` | volume swipe | **no — write-only** |
| `0x26` | personalized volume | **no — write-only** |
| `0x28` | conversation awareness | **no — write-only** |
| `0x2e` | adaptive audio noise | **no — write-only** |

This was measured over 12 s windows per setting, counting only raw `cmd 0x09` frames.

An important nuance: the six "reported" settings are not echoes of changes. They are
exactly the set the AirPods include in their **startup state dump**, and they are not
re-sent when the value changes.

That the other five never report is expected, not a bug. The MagicPods author documents
it in their own source — `AapConversationAwarenessCapability.cpp`,
`AapAdaptiveAudioNoiseCapability.cpp` and `AapPersonalizedVolumeCapability.cpp` each say:

```
// Manually notify because AirPods will not send a notification that the option has been changed.
```

---

## Inbound frames

### Battery — `cmd 0x04`

```
04 00 04 00 04 00 <count>  then <count> blocks of 5 bytes
```

Each block: `[0]` type, `[1]` unused, `[2]` level 0–100, `[3]` charging status,
`[4]` unused.

**The block order is not fixed** — the left and right blocks swap between frames, so the
type byte must be used to identify each block.

| type | |
|---|---|
| `0x01` | single (AirPods Max) |
| `0x02` | right |
| `0x04` | left |
| `0x08` | case |

| status | |
|---|---|
| `0x00` | undefined |
| `0x01` | charging |
| `0x02` | not charging |
| `0x04` | disconnected — the case is empty, or the bud is in a closed case |

The status byte is what tells you a pod is charging *because it is in the case*:

```
right 99 not-charging   left 96 charging   case 79 not-charging   <- left pod in the case
right 98 not-charging   left 98 charging   case 79 not-charging   <- both in the case
right 98 charging       left 98 charging   case 79 not-charging   <- right starts charging
```

So: a pod reporting `0x01` is in the case and charging; the case itself reports `0x04`
when it is empty, `0x02` when present but idle, and `0x01` when charging.

Two quirks on clone hardware:

* **`0xFF` is a no-reading sentinel, not a percentage.** MagicPodsCore documents it
  (`AapBatteryWatcher.h`: `// ff - unknown`) and notes that fake units "send a value of
  127 after the case is closed". Any level above 100 must be treated as unknown, or the
  UI shows a nonsensical "255%".
* A bud in a **closed** case genuinely stops reporting — the case is radio-transparent
  and the bud reaches the host only intermittently. Expect gaps of 10 s or more, and do
  not present a stale reading as current.

### Settings — `cmd 0x09`

Same shape as the request: `[6]` sub-command, `[7]` value.

### Identity — `cmd 0x1d`

```
[0..3] envelope   [4] 0x1d   [5..10] header   [11..] NULL-TERMINATED strings
```

The strings are null-terminated, not length-prefixed, and binary data follows them, so
parsing must stop at the first non-printable byte. Expected order:

```
0 "AirPods Pro"   1 "A2084"   2 "Apple Inc."   3 "GX5ZK3T9LKKT"
4 "51.9.6"   5 "51.9.6"   6 "1.0.0"   7 "com.apple.accessory.update.app.60-ANC"   ...
```

### Bud state — `cmd 0x06` and `cmd 0x08`

```
04 00 04 00 06 00 <state> <connected>
04 00 04 00 08 00 <state> <state> ...
```

**`cmd 0x06` byte 6 is a state code, byte 7 is the number of buds connected.**

| byte 6 | meaning |
|---|---|
| `0x00` | in ear |
| `0x01` | out of ear |
| `0x02` | in case |

Identified by tagging frames live in the frame monitor while moving the buds:

| observed | meaning |
|---|---|
| `06 00 01 02` | the connected bud is **out of ear** |
| `06 00 00 02` | the connected bud is **in ear** |
| `06 00 02 02` | **both in case** |

Note the "connected" byte stays at 2 in every one of those, so it is a count rather than
a per-bud flag. When only one bud is connected the frame still reports the state of that
bud.

`cmd 0x08` carries two state bytes that swap position between frames
(`08 00 02 00 01 00` / `08 00 00 02 00 00`), which looks like per-bud values delivered in
alternating order. Its exact layout is still open.

### In-ear detection per bud

MagicPods shows a separate "L in / R in" for each bud. The AAP channel does not carry
that: `cmd 0x06` reports the state of the bud that is currently active, not both. In
MagicPodsCore, per-bud in-ear state comes from the **BLE advertising** path
(`PassiveHciBasedBleAdvertisingService`) — the advertisements the buds broadcast.

Sniffing BLE advertisements needs a separate HCI-level sniffer, which the `MagicAAP`
driver does not provide. So per-bud in-ear state is not available here, and the app
reports what the channel does carry.

`aapprobe watch` prints the raw frames if you want to explore further.

### Others seen

| cmd | meaning |
|---|---|
| `0x01` | session / version ack |
| `0x0c` | MAC address + role |
| `0x0e` | role / identity |
| `0x17` | firmware info — XMP property blobs, `MaxReportSize`, `VendorID 0x05AC`, `VendorVersion 0x012C` |
| `0x2b` | MAC addresses of both ends |
| `0x31` | session key material |

### Push cadence and refresh

The AirPods push battery on their own schedule, roughly every 60 s. There is **no
battery-request command** — sending `EnableNotifications` or `AapInitExt` does not
provoke an earlier frame.

To force an immediate update, start a **genuinely fresh session**: close the device
handle and re-open it, then re-run the handshake. Replaying the handshake on an
already-established connection does *not* make the AirPods re-dump their state.

---

## Channel ownership

The driver delivers the push stream to a single reader. Two clients can both hold the
device open (`FILE_SHARE_READ|WRITE` allows it), but only one receives the live frames:

| client | device held | frames received |
|---|---|---|
| this project | 100 s+ | 8, all within the first 1.9 s |
| MagicPods | 45 s | continuous, including ear and case changes |

So close any other AAP client before using this. If the app goes quiet, that is usually
why — the GUI detects 20 s of silence and reconnects automatically.
