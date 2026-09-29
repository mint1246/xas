#!/usr/bin/env sh
set -eu
ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
export DOTNET_ROOT="$ROOT/artifacts/linux-runtime"
export PATH="$DOTNET_ROOT:/usr/local/bin:/usr/bin:/bin"
cd "$ROOT"
for suite in LinuxInstallerTests FileSystemTests ShellTests FuseCoreTests WebSessionTests RemoteMountManagerTests PeerSessionManagerTests LocalIpcShellForwardingTests ProtocolTests HandoffLifecycleTests TerminalTests; do
    dotnet tests/Xas.Tests/bin/Debug/net10.0/Xas.Tests.dll --suite "$suite"
done
