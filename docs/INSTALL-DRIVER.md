# The MagicAAP driver

Pods needs a kernel driver to reach the AirPods' L2CAP channel. This document explains
what is required and how to check it.

---

## What it does

The AirPods' control channel is **Bluetooth L2CAP PSM 0x1001**. Windows does not allow
user-mode code to open a raw L2CAP PSM, so a kernel driver must own the connection.

`MagicAAP.sys` is a modified version of Microsoft's public **BTHECHO** KMDF sample,
adapted to forward raw L2CAP payloads to the AirPods.

---

## Checking whether you have it

```powershell
sc.exe query MagicAAP
```

```
        TYPE               : 1  KERNEL_DRIVER
        STATE              : 4  RUNNING
```

You also need the device interface, which appears only while the AirPods are actually
connected:

```powershell
reg query "HKLM\SYSTEM\CurrentControlSet\Control\DeviceClasses\{9eec98bb-3c54-45d4-a843-7900c4635e08}"
```

If you use [MagicPods](https://magicpods.app), the driver is installed with it and you
already have this.

---

## Installing it yourself

**This is the risky part.** The driver is unsigned, and Windows will refuse to load it
unless test signing is enabled. That is a system-wide security setting — think carefully
before changing it, and turn it back off afterwards.

1. Enable test signing (requires an elevated prompt and a reboot):

   ```powershell
   bcdedit /set testsigning on
   ```

2. Install the driver. The `MagicAAP.inf` expects the `BTHENUM` device class:

   ```
   devcon.exe MagicAAP.inf BTHENUM\{74ec2172-0bad-4d01-8f77-997b2be0722a}
   ```

3. Verify it loaded:

   ```powershell
   sc.exe query MagicAAP
   ```

4. **Turn test signing back off** when you are done:

   ```powershell
   bcdedit /set testsigning off
   ```

---

## A note on trust

`MagicAAP.sys` carries a valid Authenticode signature (a 2010 VeriSign leaf), but the
certificate chain is not trusted by modern Windows, so it will not load without test
signing. That is expected.

This project does not distribute the driver. Obtain it from a source you trust, and
understand what you are installing — it runs in kernel mode with full access to the
system.
