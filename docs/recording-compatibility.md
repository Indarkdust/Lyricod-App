# Liquid Glass recording compatibility

Test branch: `record-compability`. This changes capture exclusion, not the Win2D effect.

## What changed

For each new monitor capture session, Lyricod queries `IDisplayGraphicsCaptureSession` and calls `SetWindowExclusionList` with the island's HWND represented as a **Windows.UI.WindowId**. This happens before `StartCapture`, with a fresh frame pool, so previously unexcluded frames cannot enter the glass renderer. Only after successful configuration does that session run with the island's window affinity set to `WDA_NONE`.

The supported path never briefly sets `WDA_EXCLUDEFROMCAPTURE`, including on session creation and monitor changes. The island should therefore appear normally to screenshots and other desktop capture applications while its own session sees the background behind it. The screenshot button does not freeze the glass or change affinity on this path.

The existing frame delivery, refraction/displacement, Gaussian blur, saturation, tint, lens map and edge highlights are retained. The `Draw`, `Lens` and `Light` implementations are unchanged.

## Runtime support and fallback

| Platform or condition | Behavior |
| --- | --- |
| Updated Windows 11, build 26100+, interface present and exclusion call succeeds | Live glass with session-only exclusion; `WDA_NONE` lets other recorders see the island. |
| Older Windows 11, or the interface is absent/disabled, or the exclusion call fails | Existing live capture with global `WDA_EXCLUDEFROMCAPTURE`. NVIDIA/desktop-recording restrictions may remain on this fallback. |
| Neither exclusion path can be safely configured, or capture/drawing fails | Capture stops and the existing clear-glass fallback is used; unexcluded live self-capture is not started. |
| Windows 10 | Liquid capture remains unsupported, as before. Other glass effects are unchanged. |

The build check is only an initial gate. A 26100+ version number does not prove feature availability; the session's `QueryInterface` result and the setter HRESULT decide. x64 and ARM64 use the same runtime check; NVIDIA recording needs a physical supported GPU/driver and cannot be validated by this Mac/Windows ARM setup.

On the legacy fallback, **Show in screenshots** retains its existing ten-second pause of the background, with lyrics still running. Resuming first restores global exclusion and then accepts new frames after the existing settling delay. This temporary, user-requested legacy behavior is not used to implement the new recording path.

The project continues targeting `net10.0-windows10.0.19041.0`, with its existing minimum OS version and BuildTools dependency. A small source-generated COM helper supplies the newer ABI rather than upgrading the SDK target. Its eight-byte `CaptureWindowId` and its iterable/iterator IIDs describe **Windows.UI.WindowId**, not the separate Windows App SDK **Microsoft.UI.WindowId**. No WinUI window-ID collection is passed to Windows Graphics Capture.

## Identify the path on your PC

Play a song, show the island, and select **Lyricod Glass → Liquid**. Keep the island locked while checking the effect. Open **Settings → About → Diagnostics**, then Show or Copy Diagnostics:

- `Glass capture: session-only exclusion (live, recordable)` means the new exclusion call succeeded.
- `Glass capture: global exclusion fallback` means the older mechanism is active. Check Windows updates before expecting this change to improve NVIDIA compatibility.
- `inactive` means Liquid capture is not currently running, for example because the island is hidden, another glass effect is selected, or Move mode is active.

Diagnostics report the chosen mechanism, not a guarantee that a particular recorder or driver works. Reopen or copy diagnostics after a mode/monitor change to refresh the displayed status.

## Validation completed here

- The test project builds with zero warnings and errors.
- All **473 tests pass**, including six new cases for the OS gate, unavailable display interface, rejected exclusion call, correct full-width HWND transfer, independent enumeration, and iterator bounds.
- The native ABI test double exercises real `QueryInterface`, the session method slot, and both iterator enumeration styles through generated COM wrappers. It runs without Windows Graphics Capture or a GPU; it does not prove real Windows exclusion/composition behavior.
- Source comparison confirms the Win2D `Draw`, `Lens` and `Light` code is unchanged.
- Windows CI builds and packages both x64 and ARM64 when this branch is pushed. Check that run's results before installing its artifacts.

**Real Windows capture, feedback, NVIDIA Instant Replay, screenshots, movement and multi-monitor behavior have not been verified here.** The following is the physical-PC acceptance checklist, not a list of passed tests.

## Physical Windows PC checklist

Use the portable artifact from this branch's Windows Actions run. Record the Windows build, GPU model, driver/NVIDIA app version and Glass capture diagnostics with the results. Check the path again whenever capture restarts.

| Check | Procedure and expected result on the session-only path |
| --- | --- |
| Feedback and live background | Put an animated window or video behind the island. Let it run for at least a minute. The glass must update continuously without recursive lyrics, trails, repeated borders or progressive darkening. |
| Effect fidelity | Change blur, refraction, transparency and tint, and toggle highlights. The same live background must respond to every control. This must not become a frozen screenshot. |
| NVIDIA Instant Replay | With desktop capture enabled in the NVIDIA app, enable Instant Replay before turning Liquid on. Save and play a clip with the island visible. Repeat with Liquid already on, and through hide/show, hover expansion, movement and monitor changes. Replay must stay enabled and the saved clip must include the live island. |
| Normal recording | Make an OBS/display-capture or other full-desktop recording alongside Liquid. Play back the file and check the island, background motion and absence of feedback. A game-only/window-only recording is not a desktop-capture test. |
| Screenshots | Capture with Print Screen and Snipping Tool without pressing Show in screenshots. The island should be visible. Pressing that button must not freeze live glass on the session-only path. |
| Resize and reposition | Expand/collapse on hover, change island size and maximum width, switch floating/docked modes, and use Move then relock. Capture must resume in the correct place without stale crops or self-capture. Existing Move mode temporarily suspends Liquid; this change preserves that behavior. |
| Multiple monitors | Move the floating island between monitors, including mixed DPI, negative desktop coordinates and different refresh rates. Each restarted session must keep session-only exclusion. Test resizing and screenshots on each display, then unplug/reconnect a display if practical. |
| Stop/restart and failure | Toggle Liquid off/on, hide/show the island, and quit/restart. The island must not remain globally hidden from recording. A lost display/device should stop capture or use the existing clear-glass fallback. |
| Legacy fallback | On a system without the interface, confirm live glass still works without feedback. Check the ten-second screenshot workaround and normal resumption. Global recording restrictions on that path are expected. |

## Remaining limits

This removes Lyricod's global exclusion on supporting systems; it does not promise that Windows Graphics Capture and every NVIDIA driver can capture concurrently. NVIDIA compatibility is pending the physical-PC results. Protected content, recorder capture mode/privacy settings and GPU/driver restrictions are outside this exclusion change.

The existing renderer follows one monitor at a time. This change does not add a stitched multi-monitor background when the island straddles displays. Display removal and device resets retain the existing fallback rather than introducing a new capture recovery system.

## API references

- [Microsoft capture sample maintainer: display-only QI, updated 26100+ builds, HWND conversion and feature availability](https://github.com/robmikh/Win32CaptureSample/issues/106).
- [IDisplayGraphicsCaptureSession](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.idisplaygraphicscapturesession?view=winrt-26100) and [SetWindowExclusionList](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.idisplaygraphicscapturesession.setwindowexclusionlist?view=winrt-26100).
- [Microsoft Windows SDK ABI header](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/winrt/windows.graphics.capture.h): session IID and method order, and the Windows.UI.WindowId collection IIDs.
