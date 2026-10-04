# SMT in game UI research

Reference repository: [drafty46/SMT](https://github.com/drafty46/SMT), inspected at commit [`eebc34a9faa89233419dd99271ed63ecfb5bd9a7`](https://github.com/drafty46/SMT/tree/eebc34a9faa89233419dd99271ed63ecfb5bd9a7). A shallow source copy is kept in the ignored `logs/references/SMT` directory for line accurate inspection. No SMT code was copied into the product and the game was not modified.

## What actually loads the code

SMT builds a native DLL whose Release target name is `SMT` with extension `.asi` (`DLL/DLL.vcxproj`, lines 79-85). Its README tells users to extract the release beside `SnowRunner.exe`; the credits identify ThirteenAG's Ultimate ASI Loader as the injector. Ultimate ASI Loader is a proxy DLL: it is placed in the game directory under a supported system DLL name and loads `.asi` files from the game root or `scripts`, `plugins`, or `update` folders. See the [loader README](https://github.com/ThirteenAG/Ultimate-ASI-Loader#installation) and the [SMT release instructions](https://github.com/drafty46/SMT#installation).

For a SnowRunner integration, treat the loader and the ASI as two separate deployment pieces. The ASI is the native renderer and IPC endpoint. The loader proxy is selected according to the installed game build and supported loader names; do not hardcode a proxy name until the release package or the target game directory has been checked. SMT's source itself does not contain the proxy DLL.

## SMT initialization and lifetime

`DLL/dllmain.cpp` starts a worker thread from `DllMain` after `DisableThreadLibraryCalls` ([source](https://github.com/drafty46/SMT/blob/eebc34a9faa89233419dd99271ed63ecfb5bd9a7/DLL/dllmain.cpp#L60-L68)). The worker opens `SMT_LOG.txt`, loads the INI, initializes memory hooks, starts the graphics hook, waits for `isGuiInitialized`, and only then starts input ([lines 46-58](https://github.com/drafty46/SMT/blob/eebc34a9faa89233419dd99271ed63ecfb5bd9a7/DLL/dllmain.cpp#L46-L58)). Detach stops input, waits for its worker, removes memory hooks, shuts down ImGui and Kiero, then closes logging ([lines 69-79](https://github.com/drafty46/SMT/blob/eebc34a9faa89233419dd99271ed63ecfb5bd9a7/DLL/dllmain.cpp#L69-L79)).

This worker-thread pattern is suitable for the spectator ASI. Do not do expensive process discovery, named-pipe connection, JSON parsing, or file I/O inside `DllMain` or the Present hook. Start a dedicated IPC/state worker after the first successful graphics initialization and publish a lock-free or mutex-protected snapshot to the render callback.

## D3D11 and ImGui path

SMT uses Kiero's D3D11 method table and binds index 8, the `IDXGISwapChain::Present` slot, in a retry loop ([`gui.cpp` lines 60-70](https://github.com/drafty46/SMT/blob/eebc34a9faa89233419dd99271ed63ecfb5bd9a7/DLL/gui.cpp#L60-L70)). The hook is not a separate overlay window: it draws into the game's own swap chain before calling the original Present.

On the first successful Present, SMT:

1. Gets `ID3D11Device` from the swap chain and obtains the immediate context.
2. Reads `DXGI_SWAP_CHAIN_DESC::OutputWindow` as the game HWND.
3. Gets back buffer 0 and creates an `ID3D11RenderTargetView`.
4. Replaces the window procedure with `SetWindowLongPtr`.
5. Creates the ImGui context and initializes `imgui_impl_win32` and `imgui_impl_dx11`.

These operations are visible at [`gui.cpp` lines 131-160](https://github.com/drafty46/SMT/blob/eebc34a9faa89233419dd99271ed63ecfb5bd9a7/DLL/gui.cpp#L131-L160). Each Present then calls `ImGui_ImplDX11_NewFrame`, `ImGui_ImplWin32_NewFrame`, `ImGui::NewFrame`, builds the UI, calls `ImGui::Render`, sets the render target, calls `ImGui_ImplDX11_RenderDrawData`, and finally calls the original Present ([lines 158-160 and 311-316](https://github.com/drafty46/SMT/blob/eebc34a9faa89233419dd99271ed63ecfb5bd9a7/DLL/gui.cpp#L158-L160)).

SMT does not hook `IDXGISwapChain::ResizeBuffers` and has no resize or device-loss path. A production spectator renderer should add a ResizeBuffers hook or detect a changed swap chain/back buffer. Before the original ResizeBuffers call, release the render target view; after it returns, recreate the back-buffer view and let the ImGui DX11 backend invalidate and recreate device objects as required. Also guard against multiple swap chains and reset all references during unload.

## Input and focus behavior

SMT subclasses the Win32 WndProc. If the game is foreground and the menu is visible it forwards messages to `ImGui_ImplWin32_WndProcHandler`; while the menu is visible it consumes every mouse message and forwards other messages to the original WndProc ([`gui.cpp` lines 114-128](https://github.com/drafty46/SMT/blob/eebc34a9faa89233419dd99271ed63ecfb5bd9a7/DLL/gui.cpp#L114-L128)). This is intentionally simple, but a spectator UI should consume input based on `ImGui::GetIO().WantCaptureMouse` / `WantCaptureKeyboard` so camera controls continue to work outside an active widget.

SMT separately creates OIS keyboard, joystick, and mouse devices against the game HWND and captures them on a roughly 16 ms worker loop ([`input.cpp` lines 232-246](https://github.com/drafty46/SMT/blob/eebc34a9faa89233419dd99271ed63ecfb5bd9a7/DLL/input.cpp#L232-L246), [`lines 360-386`](https://github.com/drafty46/SMT/blob/eebc34a9faa89233419dd99271ed63ecfb5bd9a7/DLL/input.cpp#L360-L386)). The native spectator can use the WndProc hook for menu input and retain the existing helper's hotkeys for camera commands. There is no need to use OIS just to render a menu.

## Dependencies and licenses

The SMT project vendors source or libraries for the following pieces:

| Component | Role in SMT | Evidence / license handling |
| --- | --- | --- |
| Ultimate ASI Loader | Proxy DLL that loads `.asi` plugins | [Loader README](https://github.com/ThirteenAG/Ultimate-ASI-Loader) and MIT license; deploy separately. |
| Kiero | Render API method table and hook binding | `DLL/kiero`; SMT credits Rebzzel. Preserve its license when redistributing. |
| Dear ImGui | UI core plus Win32 and DX11 backends | `DLL/imgui`; SMT credits ocornut. Dear ImGui is MIT. |
| Microsoft Detours | Game function hooks in `memory.cpp` | `DLL/detours`; source identifies Detours 4.0.1. Microsoft Detours is MIT. |
| OIS | Optional keyboard, mouse, and joystick capture | `DLL/ois`; SMT credits wgois. Keep the OIS license/notice with redistribution. |
| inifile-cpp | INI configuration | `DLL/inifile-cpp/include`; SMT credits Rookfighter. Keep its MIT notice. |
| tsl ordered map | Ordered configuration iteration | `DLL/tsl`; SMT credits Tessil. Keep its MIT notice. |
| stb_image | Embedded texture decoding | `DLL/stb`; SMT credits nothings. Keep its public-domain/MIT notice. |

The SMT top-level license is MIT and requires retaining the copyright and permission notice ([LICENSE](https://github.com/drafty46/SMT/blob/eebc34a9faa89233419dd99271ed63ecfb5bd9a7/LICENSE)). Vendor directories may have their own notices; a product package should carry those notices instead of relying only on SMT's top-level MIT file. The existing project can avoid OIS, Detours, `inifile-cpp`, `tsl`, and `stb_image` in the first UI slice if it only needs ImGui + Kiero + the Windows/DX11 system libraries.

The Release project settings show C++20, `/MT` runtime, x64 and Win32 configurations, and explicit links to `d3d11.lib`, `d3dcompiler.lib`, `dxgi.lib`, `dxguid.lib`, `user32.lib`, `detours.lib`, `OIS.lib`, and `dinput8.lib` ([`DLL.vcxproj` lines 149-171](https://github.com/drafty46/SMT/blob/eebc34a9faa89233419dd99271ed63ecfb5bd9a7/DLL/DLL.vcxproj#L149-L171)). For the spectator native DLL, x64 is the relevant target because the current SnowRunner process is 64-bit; build a DynamicLibrary and use the same runtime choice as the parent process only if the chosen third-party binaries support it.

## Bridge to the existing C# helper

The current helper uses `OpenProcess`, `ReadProcessMemory`, and `WriteProcessMemory` in `SnowFlyer2/Program.cs`, and the target window is a separate WinForms `TargetOverlayForm`. There is no IPC abstraction today. Keep camera ownership in the helper for the first native UI pass and use a named pipe for commands and state:

```text
\\.\pipe\SnowRunnerSpectator-<SnowRunner PID>

native ASI                    C# helper
-----------                  -----------
Present -> snapshot  <----  pipe reader / state publisher
pipe writer  ------->       command dispatcher -> existing camera methods
```

Recommended protocol:

* The C# process creates a duplex named pipe using a name containing the game PID and waits asynchronously for one native client.
* The ASI starts a worker after graphics initialization, connects with a short timeout, and never blocks Present waiting for the pipe.
* Messages are length-prefixed UTF-8 JSON or a small binary header plus JSON payload. Keep commands such as `toggle_freecam`, `refresh_targets`, `select_target`, `attach`, `set_face_truck`, `set_orbit`, and `close_ui`; return a monotonically increasing sequence and an error string.
* The helper sends a compact state snapshot at 10–20 Hz: connection, freecam, follow/orbit mode, selected target, target count, and status text. The ASI copies the latest complete snapshot under a mutex or double buffer and renders it without pipe calls.
* The ASI sends UI actions through a bounded command queue. If the helper is absent, disable action buttons and show `helper offline`; do not fall back to a second camera writer in the injected DLL.
* On pipe disconnect or game shutdown, clear the snapshot, close the worker handle, and let the render thread continue drawing a small disconnected indicator until unload.

This keeps the existing C# memory and safety guards in one owner while placing the controls and target list inside the actual game render. It also avoids having the native Present callback call managed code, wait on a pipe, or compete with the helper's `WriteProcessMemory` loop.

The first native screen can mirror the current form's visible operations: target list, Refresh, Attach / follow, Face truck, Orbit mode, and status. The current form's callbacks and data shape are at [`TargetOverlayForm.cs`](../SnowFlyer2/TargetOverlayForm.cs#L9-L66) only as a product-local design reference.

## Build environment observed locally

The machine has Visual Studio 2022 Build Tools at `C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools`, MSVC `14.44.35207`, MSBuild, and the bundled CMake 3.31.6. `vswhere`, `cmake`, `cl`, and `devenv` are not on the normal PATH. The Windows 10 SDK registration exists, but its `Include` and `Lib` directories are absent: `WindowsSDKVersion` is empty and a compiler link probe fails with `LNK1104: cannot open file 'kernel32.lib'`. The native source therefore could not be linked on this machine; no DLL was installed in the game. Installing or repairing the Windows SDK is required before the CMake target can produce `SpectatorUI.dll`.

## Native spectator implementation in this checkout

The reviewed native slice is under `native/SpectatorUI`. It vendors Dear ImGui and MinHook only, builds an x64 `SpectatorUI.dll`, creates a small D3D11 swap chain only to obtain the Present slot 8 and ResizeBuffers slot 13, and then hooks the game swap chain. The first game swap chain that reaches Present owns the ImGui context; secondary swap chains are passed through. The renderer restores all eight output-merger RTV slots and the depth stencil view after drawing. Pipe I/O, reconnects, and response parsing run on a worker thread, while Present consumes a mutex-protected snapshot and a bounded command queue.

The parent C# helper loads this DLL directly with `LoadLibraryW` into the running game, so this checkout does not need an Ultimate ASI Loader proxy for the prototype. If a later release switches to ASI deployment, use the loader procedure above and rename/package the native binary according to that loader's supported proxy names. The current output name is deliberately `SpectatorUI.dll`; it is not the historical `SMT.asi` name.

## Reusable implementation checklist

1. Repair/install the Windows SDK, then build the x64 `SpectatorUI.dll` target.
2. Start an initialization worker from `DllMain`; wait for the MinHook D3D11 method hooks before touching ImGui.
3. Bind Present slot 8, obtain device/context/HWND once, and create the back-buffer RTV.
4. Hook WndProc with a saved original and forward messages to ImGui while visible; consume only messages ImGui wants.
5. Add ResizeBuffers/device-loss handling, which SMT lacks.
6. Run named-pipe I/O on a worker and publish immutable state to the Present callback.
7. Render the target list and command widgets in the game's swap chain; call the original Present exactly once per path.
8. Unhook WndProc, release RTV/device/context, shut down ImGui, disconnect IPC, and unbind Kiero in a controlled order.

