# XAS Windows virtual display driver

This is a UMDF 2 Indirect Display Driver (IddCx) for creating one Windows monitor coordinate region named **XAS Linux Display**. It does not encode, transmit, or otherwise expose desktop frames. The IddCx swapchain is consumed and discarded so Windows can drive the monitor path without a frame consumer backing another display.

## Build and install prerequisites

- Windows 11 24H2 or later and a supported x64 Windows installation (the project uses the current UMDF 2.25 toolset; IddCx API level is 1.4).
- Visual Studio 2022 with C++ desktop tools and the Windows 11 WDK (integrated WDK/MSBuild driver targets).
- Test signing enabled for local development, followed by reboot. Production deployment requires a Microsoft-signed package.
- Microsoft `devcon.exe` from the WDK to create the root-enumerated software device (elevated). The INF binds the UMDF driver to the IndirectKmd display filter.

Build `XasVirtualDisplay.vcxproj` in Visual Studio, then from an elevated Developer PowerShell create the device with `devcon install XasVirtualDisplay.inf Root\XASVirtualDisplay`. Remove it with `devcon remove Root\XASVirtualDisplay`.

## Current behavior and contract

The adapter starts one monitor using a valid EDID whose product descriptor begins `XAS Linux`; Windows can identify it through the monitor friendly name / EDID product name in DisplayConfig or EnumDisplayDevices. It exposes 1920x1080@60, 1600x900@60 and 1280x720@60 modes. Linux monitor metadata is not yet wired to the driver: resolution selection is currently done in Windows Display Settings. A later control channel can replace this fixed mode list and call the IddCx display configuration update API.

The implementation uses IddCx adapter-init, monitor-create/arrival, mode-query, swapchain assignment, and swapchain unassignment callbacks. Its worker sets a D3D device on the assigned swapchain, acquires each frame, immediately releases the surface without reading its pixels, and signals frame processing completion. It has no capture, encoding, or network transport. This first version is source-only validated: the host has neither WDK headers/libraries nor Visual Studio/MSBuild or MSVC, so it cannot produce or sign a driver package here.
