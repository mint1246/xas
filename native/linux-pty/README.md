# Linux PTY helper

`xas-linux-pty` is a small Linux subprocess that owns one pseudo-terminal. It
runs the selected shell with the current process credentials, or starts the
normal `sudo` client when explicitly requested. In sudo mode, authentication
uses sudo's ordinary controlling-terminal prompt inside the PTY. The helper
never reads, stores, or forwards a password through its control protocol. The
parent application should launch it as the logged-in user and communicate
using the framed protocol below. Raw terminal bytes never share a stream with
control messages.

## Build and launch

Build on Linux with `make` (requires a C compiler and the platform `libutil`;
glibc systems provide `forkpty` there). Launch as:

```text
xas-linux-pty [--shell PATH] [--sudo] [--exec PATH -- ARG...] [--cwd PATH] [--cols N] [--rows N]
```

When `--shell` is omitted, `$SHELL` is used, then `/bin/sh`. The shell is
executed directly with no `-c` command string; its `argv[0]` is prefixed with
`-` to request login-shell behavior. Initial dimensions default to 80 columns
by 24 rows. `--sudo` starts `sudo -i` for interactive sessions. With `--exec`,
the helper executes the provided argument vector directly, prefixed with
`sudo --` when sudo mode is enabled. `--cwd` changes the child working
directory before launch. Standard input and output are reserved for the
protocol; helper diagnostics go to standard error.

## Framing

Both directions use frames: one type byte, a 4-byte unsigned big-endian
payload length, then that many payload bytes. Receivers must handle partial
reads and multiple frames per read. Payloads are limited to 65,536 bytes.

Parent to helper (stdin):

| Type | Payload | Meaning |
| --- | --- | --- |
| `0x01` | 1–65,536 bytes | Write these bytes to the PTY master. |
| `0x02` | 4 bytes: columns then rows, each unsigned big-endian 16-bit | Resize the PTY. Zero dimensions are invalid. |
| `0x03` | empty | Terminate the shell process group and close the session. |
| `0x04` | empty | Send the PTY's configured end-of-input character to the child. |

Helper to parent (stdout):

| Type | Payload | Meaning |
| --- | --- | --- |
| `0x81` | 1–16,384 bytes | Bytes read from the PTY master. |
| `0x82` | 4-byte signed big-endian integer | Shell exit status. Normal exit is its status; a signal is represented as `128 + signal`. |
| `0xff` | UTF-8 text | Startup/protocol error; helper then exits nonzero. |

The helper exits after sending `0x82`. EOF on helper stdin is treated as a
disconnect: it sends SIGTERM to the shell process group, waits briefly, sends
SIGKILL if needed, reaps the child, and exits. `0x03` follows the same cleanup
path. Unknown frames, invalid lengths, and malformed resize requests terminate
the session after reporting an error frame when stdout is available.
