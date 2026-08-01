# YarrOverlay

YarrOverlay captures one Windows display with DXGI Desktop Duplication, removes near-black pixels, sharpens/scales the image on D3D11, and composites it as a transparent, topmost, click-through overlay on another display.

The default `Auto` pipeline uses a GPU-only DirectComposition presenter when capture and output are on the same adapter:

```text
Desktop Duplication texture
  -> one D3D11 CopyResource to an SRV-capable texture
  -> chroma/sharpen/scale pixel shader
  -> premultiplied BGRA DirectComposition swap-chain back buffer
  -> non-blocking Present
```

There is no staging texture, `Map`, full-frame CPU copy, `Bitmap`, GDI DC, or `UpdateLayeredWindow` operation in the GPU Native hot path. Cross-adapter routes and failed GPU initialization use the selectable Legacy CPU fallback.

## Requirements

- Windows 10 or later
- .NET 10 Desktop Runtime for a framework-dependent build, or the .NET 10 SDK to build
- A GPU/driver supporting D3D11 and DXGI Desktop Duplication
- A second physical display or active virtual display

## Build and run

```powershell
dotnet build Overlay.slnx -c Release
dotnet run --project Overlay -c Release
```

Use `--autostart` to start capture immediately after loading the saved route. Logs and persisted settings are written beside `YarrOverlay.exe`.

## Virtual-display / Parsec routing

1. Install and enable a virtual display driver, extend the Windows desktop to it, and set it to 1920x1080 at the desired refresh rate.
2. Run Parsec Client full-screen on that virtual display.
3. In YarrOverlay select the virtual display as **Capture Display** and the physical monitor as **Output Display**.
4. Keep **Pipeline: Auto**. A same-adapter route should report `GPU Native`; a cross-adapter route reports `Legacy CPU`.
5. Use `Stretch` for 1920x1080 to 2560x1440, then press **Start Overlay**.

Display selection is persisted by device name, adapter LUID, DXGI output index, size, and desktop position. Starting with the same capture/output display is blocked to prevent a feedback loop. Display topology changes and recoverable DXGI errors trigger a bounded, logged recreation path.

## Controls and diagnostics

- `Space`: start/stop
- `+` / `-`: adjust black threshold
- Settings buttons: capture/output, scaling, sharpness, pipeline, HUD, metrics level, thread priority, MMCSS, CSV, maximum frame latency, CSV interval, and latency-test mode
- `Performance HUD`: Off by default; Basic and Detailed refresh at 4 Hz
- `Metrics Collection`: Off, Lightweight, or delayed non-blocking Detailed GPU timestamps
- `CSV`: bounded asynchronous writer; rendering never waits for disk I/O

The displayed software latency ends when `Present` or `UpdateLayeredWindow` returns. It is not physical scan-out or photon latency. See [PERFORMANCE.md](./PERFORMANCE.md) for architecture, metric definitions, A/B results, limitations, and the external latency-marker workflow.

## Latency marker

`LatencyMarker` is a separate high-contrast frame counter, QPC value, and Gray-code pattern generator. Place it on the Parsec host/virtual display for a high-speed-camera comparison with the receiving physical display. It intentionally does not subtract clocks from different PCs: without a clock-offset/drift protocol that result would be invalid.

```powershell
dotnet run --project LatencyMarker -c Release
```

## Preview

![YarrOverlay Control Window](./imgs/1.png)
![YarrOverlay Overlay Output](./imgs/2.png)
![YarrOverlay Overlay](./imgs/3.png)
