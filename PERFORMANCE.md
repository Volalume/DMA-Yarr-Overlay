# YarrOverlay latency architecture and measurement report

## 1. Original pipeline

```text
AcquireNextFrame(0)
  -> CopyResource(capture -> SourceTexture)
  -> pixel shader(OutputTexture)
  -> CopyResource(OutputTexture -> staging)
  -> Map(Read), wait for GPU
  -> allocate/copy QHD Bitmap
  -> FrameReady
  -> WinForms BeginInvoke queue
  -> Bitmap/HBITMAP/GDI DC
  -> UpdateLayeredWindow
```

The current repository was inspected rather than relying only on the request description. The original implementation's dominant costs were the serialized GPU-to-CPU readback, `Map` synchronization, full-frame memory copy/allocation, cross-thread UI mailbox, GDI object construction, and layered-window submission. GPU scaling was followed by another CPU transport of the complete 2560x1440 frame.

## 2. New pipeline

### Same adapter: GPU Native / GPU Copy

```text
DXGI Desktop Duplication resource
  -> one GPU CopyResource to BGRA SRV texture
  -> chroma key + sharpening + scaling pixel shader
  -> DirectComposition swap-chain back buffer
  -> Present(0, DO_NOT_WAIT)
```

Capture, drawing, and Present run serially on one dedicated MTA capture thread. The UI thread is not in this path. The source copy remains because a Desktop Duplication resource is not guaranteed to have the shader-resource bind flags needed to create an SRV; there is no second output texture. The GPU Native context does not even allocate legacy output/staging resources.

The composition swap chain is `B8G8R8A8_UNORM`, two-buffer, `FLIP_SEQUENTIAL`, stretch-scaled, and premultiplied-alpha. The shader multiplies RGB by its final alpha. The DirectComposition visual is attached to the existing topmost, no-activate, transparent/tool-window HWND, preserving click-through behavior.

`Present(0, DO_NOT_WAIT)` returns `DXGI_ERROR_WAS_STILL_DRAWING` instead of blocking. Such a frame is counted as dropped; no Present backlog or software queue is allowed. Device maximum frame latency defaults to one. A frame-latency waitable handle is not used because the capture-driven path must not wait for a queued frame; the user may select device maximum latency 1-3 for comparison.

### Cross adapter or initialization failure: Legacy CPU

The explicit fallback retains the staging/Map/Bitmap/UpdateLayeredWindow path only for compatibility. It uses a single-slot latest-frame mailbox: an unsubmitted frame is disposed and replaced, and queue depth cannot exceed one. `Bitmap.Clone` and per-frame CPU text drawing were removed. Stable cross-adapter shared-resource/keyed-mutex behavior varies by driver and format, so this build does not claim an unverified shared path. The UI reports `Legacy CPU`, adapter names, and whether the route is on the same adapter.

## 3. Scheduling and submission policy

- `AcquireNextFrame(0)` remains non-blocking. A `SpinWait` progressively mixes spinning and yielding; there is no unconditional `Sleep(1)`.
- Pointer-only updates (`LastPresentTime == 0`) are not rendered again.
- Only the latest frame is useful. GPU Native has no CPU frame queue; Legacy has one replaceable slot.
- Threshold and sharpness use volatile reads/writes. The constant buffer is updated only when its value changes.
- Capture priority is Normal, AboveNormal (default), or Highest. MMCSS `Games` is optional and safely reverts/falls back.
- `Present` uses immediate, non-blocking semantics. Unsupported tearing flags were not added to the composition swap chain.

## 4. Timing model

Every accepted frame has a monotonically increasing `FrameId` and QPC/`Stopwatch.GetTimestamp` timestamps for acquire call/return, Desktop Duplication present/mouse times, source/shader/staging submission, map, CPU copy, event/mailbox/UI, and submit call/return. `OutduplFrameInfo` accumulated frames, metadata size, pointer visibility, and protected-content state are retained.

`LastPresentTime` is already a QueryPerformanceCounter-domain value on Windows. A zero value is treated as invalid/N/A, never synthesized. The app reports:

- capture acquisition / acquire wait
- acquire-return to software submit (`software pipeline latency`)
- estimated desktop-present to software submit
- legacy Map, CPU copy, UI queue, and UpdateLayeredWindow submit times
- frame age at submit and submitted-frame interval
- GPU source-copy, shader, and total command durations
- Present call/return and available DXGI frame statistics

`Present`/`UpdateLayeredWindow` return is only a software/composition submission boundary. It is not scan-out completion, display response, or photon latency. `Measured display latency` is deliberately not shown unless an external camera method is used.

## 5. Aggregation and overhead control

Recent 1-second and 10-second samples live in a fixed 65,536-entry ring. Whole-session percentiles use fixed 0.05 ms histograms instead of an unbounded list. Current, average, minimum, maximum, P50, P95, and P99 are generated on the 4 Hz UI snapshot path; no per-frame LINQ or sort exists. `Current` is the newest sample, not the maximum.

Counters include captured/submitted/replaced/dropped frames, acquire timeouts, DXGI errors, `AccumulatedFrames` sum/max, and queue current/max. The settings status also shows duplication recreations, last DXGI error, last-good-frame age, capture/render thread IDs, adapters/same-adapter, CSV loss, recovery duration, GC counts, allocation total, and working set.

Detailed GPU mode uses an eight-slot timestamp/disjoint query ring. Results are requested eight frames later with `D3D11_ASYNC_GETDATA_DONOTFLUSH`; unavailable data is left N/A instead of stalling. Off disables per-frame metric aggregation; Latency Test Mode temporarily selects Detailed GPU metrics.

CSV rows contain all timestamps/durations, Outdupl data, drop marker, pipeline/adapters, DXGI/Present results and frame statistics. A below-normal writer thread drains a bounded 4096-row queue. Full buffers increment `CSV lost` and discard diagnostics rather than blocking rendering.

## 6. Performance HUD meanings

- **submit fps**: frames whose software submit call completed per second
- **software submit**: acquire return to Present/UpdateLayeredWindow return
- **desktop→submit estimate**: valid Desktop Duplication `LastPresentTime` to software submit return
- **age**: desktop present to submit call start
- **interval**: time between submitted samples
- **acquire**: `AcquireNextFrame` call duration
- **map / CPU copy / UI**: Legacy-only readback, memory copy, and mailbox wait; N/A on GPU Native
- **GPU copy/shader/total**: delayed D3D11 query durations, not CPU duration
- **queue**: current and observed maximum software-frame queue depth
- **dropped**: nonblocking Present backlog or failed frame processing
- **replaced**: a Legacy single-slot frame superseded by a newer one
- **accumulated**: DXGI desktop updates accumulated while the previous frame was being processed

Basic HUD shows only the main software and desktop-submit figures. Detailed adds individual stages and counters. Both refresh at 4 Hz and are off by default.

## 7. A/B benchmark captured during implementation

Test route: virtual `1920x1080 @ 120 Hz` capture to physical `2560x1440 @ 240 Hz`, both on an NVIDIA GeForce RTX 4070 SUPER (same adapter). A changing `LatencyMarker` supplied frames. CSV sampling was every frame. These are short implementation A/B samples, not a completed five-minute certification; the requested long-running test was stopped at the user's direction.

| Metric (ms) | Legacy avg | Legacy P50 | Legacy P95 | Legacy P99 | GPU avg | GPU P50 | GPU P95 | GPU P99 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Software pipeline / submit | 19.974 | 18.985 | 25.804 | 32.240 | 0.282 | 0.249 | 0.573 | 0.711 |
| Desktop-present→submit estimate | 27.241 | 26.688 | 34.783 | 40.651 | 3.237 | 2.809 | 7.847 | 12.515 |
| Submit call | 1.833 | 1.678 | 2.901 | 4.058 | 0.234 | 0.199 | 0.521 | 0.653 |
| Map wait | 1.208 | 0.869 | 3.341 | 5.280 | N/A | N/A | N/A | N/A |
| CPU full-frame copy | 1.816 | 1.823 | 2.284 | 2.799 | N/A | N/A | N/A | N/A |
| GPU total query | not sampled | not sampled | not sampled | not sampled | 0.442 | 0.249 | 1.565 | 3.483 |

Sample sizes were 1,599 Legacy rows and 2,690 GPU Native rows. In these samples the software-pipeline average fell by about 70.8x and P95 by about 45.1x. This is a reduction in this application's software submit path, not Parsec end-to-end or measured physical display latency.

An approximate separate 20-second process observation was 36.33 CPU-seconds and 147.7 MB working set for Legacy versus 2.22 CPU-seconds and 111.9 MB for GPU Native. GPU utilization, visual alpha accuracy, click-through, topmost retention, and five-minute stability were not quantitatively certified in the final pass because testing was stopped. The GPU composition path did initialize successfully on the stated route; no claim beyond that evidence is made.

## 8. Latency Test Mode and true end-to-end testing

The included `LatencyMarker` app renders a high-contrast frame number, local QPC value, and Gray-code bars. Put it on the sending/virtual display, enable YarrOverlay Latency Test Mode for detailed internal metrics, and film both the source marker and receiving physical monitor with one high-speed camera. Frame differences multiplied by camera frame time give a camera-observed end-to-end distribution.

Do not subtract QPC values from two PCs: QPC epochs/frequencies are not a shared clock. A network timestamp mode would require an explicit offset/drift synchronization protocol and uncertainty accounting; none is fabricated here. Camera measurement is therefore the supplied, honest full-chain method.

## 9. Recovery

Access lost, invalid call, device removed/reset, resolution/topology changes, virtual-display recreation, output changes, and full-screen transitions release active frames and owned COM resources, wait a bounded backoff, re-enumerate displays by the persisted stable identity, and rebuild duplication/composition. A guard prevents overlapping application-level recoveries. Counts, last/total recovery milliseconds, and the last error are shown.

## 10. Modified files

- `DuplicationCapture.cs`: QPC instrumentation, same-thread GPU path, latest-frame policy, lazy Legacy resources, adapter routing, recovery, scheduling/MMCSS
- `GpuOverlayRenderer.cs`: DirectComposition flip swap chain, premultiplied BGRA, max latency and nonblocking Present/statistics
- `GpuTimestampCollector.cs`: delayed nonblocking timestamp/disjoint ring
- `FrameTiming.cs`: frame record, fixed recent ring, whole-session histograms, percentiles/counters
- `PerformanceCsvWriter.cs`: bounded asynchronous CSV writer
- `PerformanceHudWindow.cs`: 4 Hz click-through Basic/Detailed HUD
- `CaptureOptions.cs`, `CaptureDiagnostics.cs`, `SettingsStore.cs`: persisted modes and diagnostics contracts
- `OverlayWindow.cs`, `OverlayHost.cs`: one-slot Legacy mailbox and companion HUD; GPU frames bypass them
- `AppState.cs`, `SettingsWindow.cs`: routing, recovery, settings, and live status
- `MonitorInfo.cs`: DXGI adapter/output identity and topology metadata
- `NativeMethods.cs`: layered-window compatibility calls and MMCSS APIs
- `Program.cs`: optional `--autostart`
- `LatencyMarker/*`, `Overlay.slnx`: external marker tool and solution integration

## 11. Package and API choices

Added NuGet package: `Vortice.DirectComposition` 3.8.3, matching the existing Vortice Direct3D11/DXGI/D3DCompiler packages. It is needed for typed `IDCompositionDevice`, target, and visual bindings. The alternative is hand-written COM interop or retaining UpdateLayeredWindow; the former adds brittle surface area and the latter retains the measured readback bottleneck.

The swap-chain descriptor follows Microsoft's documented `CreateSwapChainForComposition` requirements: flip sequential and stretch scaling, with two buffers. `DO_NOT_WAIT` is documented to return `DXGI_ERROR_WAS_STILL_DRAWING` rather than sleep. Desktop Duplication's `LastPresentTime` is QPC-domain and zero for pointer-only/no-image updates. GPU query data is collected asynchronously without forcing a flush.

Primary API references:

- [IDXGIFactory2::CreateSwapChainForComposition](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgifactory2-createswapchainforcomposition)
- [DXGI_SWAP_CHAIN_DESC1](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_swap_chain_desc1)
- [DXGI_PRESENT flags](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-present)
- [DXGI_OUTDUPL_FRAME_INFO](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_outdupl_frame_info)
- [ID3D11DeviceContext::GetData](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-getdata)

## 12. Remaining limitations and possible follow-ups

- Cross-adapter GPU sharing is not implemented; it falls back explicitly to CPU for correctness. A future path should probe shared NT handles/keyed mutex/cross-adapter format support per driver, then benchmark it before selecting it in Auto.
- The required single source GPU copy remains because the acquired resource may not be SRV-bindable. A driver-specific direct SRV path could be probed, but must fall back safely.
- DirectComposition controls actual scheduling; Present statistics are opportunistic and do not expose a universal photon timestamp.
- Dirty-rectangle processing could reduce work for mostly static content, but a full-screen chroma/sharpen scale pass remains predictable and was already far below one millisecond in the measured software path.
- A PresentMon/ETW integration could add independent composition/scan-out evidence without blocking the renderer.
- Five-minute stability, GPU utilization, high-speed-camera display latency, and systematic visual/input/topmost tests remain uncompleted test items. They are not described as implemented measurements.
