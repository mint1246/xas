# XAS Windows virtual display driver

This is a UMDF 2 Indirect Display Driver (IddCx) for creating one Windows monitor coordinate region named **XAS Linux Display**. It does not encode, transmit, or otherwise expose desktop frames. The IddCx swapchain is consumed and discarded so Windows can drive the monitor path without a frame consumer backing another display.

## Build and install prerequisites

- Windows 11 24H2 or later and a supported x64 Windows installation (the project uses the current UMDF 2.25 toolset; IddCx API level is 1.4).
- Visual Studio 2026 with MSVC x64 tools, Windows SDK 10.0.28000, and WDK 10.0.28000.2526. The checked-in `build-local.ps1` can use the signed Microsoft WDK NuGet package extracted at `.tools/wdk`, without installing the WDK Visual Studio extension. A Visual Studio project build requires that extension and its `WindowsUserModeDriver10.0` platform toolset.
- Test signing enabled for local development, followed by reboot. Production deployment requires a Microsoft-signed package.
- Microsoft `devcon.exe` from the WDK to create the root-enumerated software device (elevated). The INF binds the UMDF driver to the IndirectKmd display filter.

For a local command-line build, download `Microsoft.Windows.WDK.x64` version `10.0.28000.2526` from NuGet, verify its signature with `dotnet nuget verify <package> --all`, extract it to the repository's `.tools/wdk`, and run `native/windows-virtual-display/build-local.ps1 -Configuration Release`. The script compiles and links the UMDF DLL with MSVC, then checks the INF using the WDK's Windows Driver and Universal rules. The output is under `artifacts/windows-virtual-display/x64/Release`.

The release DLL was built on this host with MSVC 14.51, Windows SDK 10.0.28000, and the Microsoft-signed WDK 10.0.28000.2526 NuGet package. The INF passed `infverif /w`, `/u`, and `/h`. The package is still unsigned and has no catalog; it has not been installed or exercised as a monitor. Signing and driver installation require the appropriate administrative setup and a real Windows display test. Once a signed package is prepared, create its root-enumerated device with elevated `devcon install XasVirtualDisplay.inf Root\XASVirtualDisplay`; remove it with `devcon remove Root\XASVirtualDisplay`.

## Current behavior and contract

The adapter starts one monitor using a valid EDID whose product descriptor begins `XAS Linux`; Windows can identify it through the monitor friendly name / EDID product name in DisplayConfig or EnumDisplayDevices. It exposes 1920x1080@60, 1600x900@60 and 1280x720@60 modes. Linux monitor metadata is not yet wired to the driver: resolution selection is currently done in Windows Display Settings. A later control channel can replace this fixed mode list and call the IddCx display configuration update API.

The implementation uses IddCx adapter-init, monitor-create/arrival, mode-query, swapchain assignment, and swapchain unassignment callbacks. Its worker sets a D3D device on the assigned swapchain, acquires each frame, immediately releases the surface without reading its pixels, and signals frame processing completion. It has no capture, encoding, or network transport.
