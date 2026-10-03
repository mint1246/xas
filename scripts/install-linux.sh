#!/usr/bin/env sh
set -eu

PACKAGE_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
BIN_DIR=${XAS_INSTALL_DIR:-"$HOME/.local/bin"}
MOUNT_ROOT="$HOME/xas"
mkdir -p "$BIN_DIR"
mkdir -p "$MOUNT_ROOT"
chmod 700 "$MOUNT_ROOT" 2>/dev/null || true
install_tmp_one=""
install_tmp_two=""
cleanup_install_tmp() {
    [ -z "$install_tmp_one" ] || rm -f "$install_tmp_one"
    [ -z "$install_tmp_two" ] || rm -f "$install_tmp_two"
}
trap cleanup_install_tmp EXIT
trap 'exit 1' HUP INT TERM
for name in xas Xas.Daemon; do
    if [ ! -f "$PACKAGE_DIR/$name" ]; then
        echo "Package is missing $name in $PACKAGE_DIR" >&2
        exit 1
    fi
    tmp_path="$BIN_DIR/.$name.install.$$"
    if [ -z "$install_tmp_one" ]; then install_tmp_one="$tmp_path"; else install_tmp_two="$tmp_path"; fi
    install -m 0755 "$PACKAGE_DIR/$name" "$tmp_path"
    mv -f "$tmp_path" "$BIN_DIR/$name"
done

fuse_missing=""
if ! command -v fusermount3 >/dev/null 2>&1; then fuse_missing="$fuse_missing fusermount3"; fi
if [ ! -e /dev/fuse ]; then fuse_missing="$fuse_missing /dev/fuse"; fi
if command -v ldconfig >/dev/null 2>&1; then
    if ! ldconfig -p 2>/dev/null | grep -Eq 'libfuse3\.so(\.3|[[:space:]])'; then fuse_missing="$fuse_missing libfuse3"; fi
else
    found_fuse_lib=false
    for candidate in /lib*/libfuse3.so* /usr/lib*/libfuse3.so* /lib/*/libfuse3.so* /usr/lib/*/libfuse3.so*; do
        if [ -e "$candidate" ]; then found_fuse_lib=true; break; fi
    done
    if [ "$found_fuse_lib" = false ]; then fuse_missing="$fuse_missing libfuse3"; fi
fi
if [ -n "$fuse_missing" ]; then
    echo "Warning: native Linux remote mounts are unavailable; missing:$fuse_missing" >&2
    distro_id=""
    if [ -r /etc/os-release ]; then
        distro_id=$(sed -n 's/^ID=//p' /etc/os-release | tr -d '"' | head -n 1)
    fi
    case "$distro_id" in
        ubuntu|debian|linuxmint|pop) echo 'Install FUSE3 with: sudo apt install fuse3 libfuse3-3' >&2 ;;
        fedora|rhel|centos|rocky|almalinux) echo 'Install FUSE3 with: sudo dnf install fuse3 fuse3-libs' >&2 ;;
        arch|cachyos|manjaro|endeavouros) echo 'Install FUSE3 with: sudo pacman -S fuse3' >&2 ;;
        *) echo 'Install your distribution FUSE3 package (libfuse3 + fusermount3) and ensure /dev/fuse is accessible.' >&2 ;;
    esac
else
    echo "FUSE3 remote-mount support is available; mount root: $MOUNT_ROOT"
fi
# Carry native helpers only when the package actually contains built helpers.
for name in xas-linux-pty xas-wayland-eis xas-uinput; do
    if [ -f "$PACKAGE_DIR/$name" ]; then install -m 0755 "$PACKAGE_DIR/$name" "$BIN_DIR/$name"; fi
done

# Run xas inside the user's desktop session. This is deliberately a user service rather than a
# system/root service so Wayland, portals, clipboard, PTY, and the normal user's home remain usable.
if command -v systemctl >/dev/null 2>&1; then
    UNIT_DIR="$HOME/.config/systemd/user"
    mkdir -p "$UNIT_DIR"
    cat > "$UNIT_DIR/xas-daemon.service" <<EOF
[Unit]
Description=xas user-session daemon
After=graphical-session.target network-online.target

[Service]
Type=simple
ExecStart="$BIN_DIR/Xas.Daemon" serve
Restart=always
RestartSec=2
Environment="PATH=$BIN_DIR:/usr/local/bin:/usr/bin:/bin"

[Install]
WantedBy=default.target
EOF
    if ! systemctl --user daemon-reload; then
        echo 'Failed to reload the xas user service definition.' >&2
        exit 1
    fi
    if systemctl --user is-active --quiet xas-daemon.service; then
        if ! systemctl --user restart xas-daemon.service; then
            echo 'Failed to restart the xas user daemon after installation.' >&2
            exit 1
        fi
    else
        if ! systemctl --user enable --now xas-daemon.service; then
            echo 'Failed to enable and start the xas user daemon.' >&2
            exit 1
        fi
    fi
else
    echo 'systemctl not found; xas daemon autostart was not installed.' >&2
fi

case ":${PATH}:" in
    *":$BIN_DIR:"*) ;;
    *)
        profile=${XAS_SHELL_PROFILE:-}
        if [ -z "$profile" ]; then
            case ${SHELL:-} in */zsh) profile="$HOME/.zshrc" ;; *) profile="$HOME/.profile" ;; esac
        fi
        mkdir -p "$(dirname -- "$profile")"
        marker='# xas user bin PATH'
        if [ ! -f "$profile" ] || ! grep -Fq "$marker" "$profile"; then
            { printf '\n%s\n' "$marker"; printf 'case ":$PATH:" in *":%s:"*) ;; *) PATH="%s:$PATH" ;; esac\nexport PATH\n' "$BIN_DIR" "$BIN_DIR"; } >> "$profile"
        fi
        ;;
esac
echo "Installed xas and Xas.Daemon to $BIN_DIR"
echo 'If this shell cannot find xas yet, open a new terminal or source your shell profile.'
