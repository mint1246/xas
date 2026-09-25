# Text clipboard protocol, version 1

The daemon advertises `Clipboard` capability version 1 only when a real user-session text clipboard backend is available. The receiver checks its local per-peer `Clipboard` grant for each request. This initial protocol carries plain UTF-8 text up to 256 KiB on the existing authenticated connection; images and rich text are future versions.

- RPC `clipboard.get`: empty request. Response JSON `{ "origin": "device-id", "version": 123, "text": "..." }`.
- RPC `clipboard.set`: JSON with the same fields. `origin` must equal the authenticated caller's device ID. Response is empty. The receiver records the imported origin/version alongside its resulting native clipboard change sequence.

When the receiver's native clipboard change sequence still matches an imported update, `clipboard.get` reports the imported origin/version. A new local change reports the receiver's own device ID and native change sequence. Sync clients must not send an update back to its origin or reapply a version they already processed. Clipboard access remains in the logged-in user's session. The initial CLI exposes explicit `clipboard push` and `clipboard pull`; automatic watching/reconnect is not yet implemented.
