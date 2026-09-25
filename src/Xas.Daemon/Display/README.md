# Linux display metadata

`LinuxDisplayMetadataService.GetPrimaryDisplayAsync()` returns the active display metadata or `null` if the local desktop API is unavailable. Start the daemon in the logged-in graphical user's session so it inherits `DISPLAY`/`WAYLAND_DISPLAY`, X authorization, and the compositor's user bus. A system service without that context cannot reliably inspect an interactive display.

- X11: `xrandr` must be on `PATH`; reads the connected output marked `primary`, or an `eDP`/`LVDS` panel if none is marked.
- Wayland / wlroots: `wlr-randr` must be on `PATH`; selects the focused output, or a built-in panel.
- Wayland / KDE Plasma: if `wlr-randr` is unavailable or cannot report a display, `kscreen-doctor` is queried; selects priority 1, or a built-in panel.

The provider reports active mode pixels, refresh in millihertz, rotation, scale where available, and physical dimensions where the backend exposes them. Unknown values stay null. It does not estimate dimensions from DPI. Other Wayland compositors do not have a common command-line display query, so they need a compositor-specific adapter.

For the daemon RPC, serialize `DisplayMetadata` as the payload; its properties are `Id`, `Name`, `WidthPixels`, `HeightPixels`, optional `RefreshMilliHertz`, optional physical dimensions, `RotationDegrees`, and optional `Scale`. The service is stateless and can be instantiated per request.
