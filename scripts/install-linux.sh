#!/usr/bin/env sh
set -eu

PACKAGE_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
BIN_DIR=${XAS_INSTALL_DIR:-"$HOME/.local/bin"}
mkdir -p "$BIN_DIR"
for name in xas Xas.Daemon; do
    if [ ! -f "$PACKAGE_DIR/$name" ]; then
        echo "Package is missing $name in $PACKAGE_DIR" >&2
        exit 1
    fi
    install -m 0755 "$PACKAGE_DIR/$name" "$BIN_DIR/$name"
done
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
ExecStart=$BIN_DIR/Xas.Daemon serve
Restart=on-failure
RestartSec=2
Environment=PATH=$BIN_DIR:/usr/local/bin:/usr/bin:/bin

[Install]
WantedBy=default.target
EOF
    systemctl --user daemon-reload
    systemctl --user enable --now xas-daemon.service
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
