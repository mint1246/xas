# Wayland RemoteDesktop input helper

Build on Linux with a C compiler, `pkg-config`, and the libei/liboeffis development packages:

```sh
make
```

The helper uses `liboeffis-1.0` for the XDG RemoteDesktop session lifecycle (CreateSession, SelectDevices, Start, ConnectToEIS) and passes the returned fd to `ei_setup_backend_fd()` in `libei-1.0`. The no-prompt probe connects to the session bus and queries the portal's RemoteDesktop v2 version and available keyboard/pointer types through GDBus. The portal presents its normal user consent prompt only during activation. Absolute pointer capability is confirmed only after consent, when the granted EIS devices arrive; therefore v2 availability is a runtime capability that can still fail at activation if the compositor does not offer an absolute device. Runtime needs a user session D-Bus, a portal backend implementing RemoteDesktop v2 `ConnectToEIS`, and a Wayland desktop. `XAS_WAYLAND_EIS_HELPER` can point the daemon at the built executable; otherwise it must be beside the daemon or on `PATH`.

No input is considered available without this helper and a Wayland session. After a granted session, the daemon keeps the helper open across monitor crossings, releases held keys/buttons whenever a control lease ends, and closes the helper when the daemon exits. The portal may still deny or revoke the session, in which case the helper reports failure and no input is injected.
