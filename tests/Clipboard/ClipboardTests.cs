using System.Text.Json;
using Xas.Core;
using Xas.Core.Security;
using Xas.Daemon.Clipboard;

namespace Xas.Tests;

public static class ClipboardTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "xas-clipboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var permissions = new PeerPermissionStore(root);
            var backend = new MemoryClipboard("local text");
            var service = new ClipboardService("local", permissions, backend);
            var get = new ProtocolMessage(MessageKind.Request, 1, 0, "clipboard.get", []);
            try { await service.HandleAsync("peer", get, CancellationToken.None); throw new Exception("Denied clipboard read succeeded."); }
            catch (UnauthorizedAccessException) { }
            permissions.SetAllowed("peer", Capability.Clipboard, true);
            var original = Decode(await service.HandleAsync("peer", get, CancellationToken.None));
            Assert(original.Origin == "local" && original.Text == "local text", "Local clipboard origin was wrong.");

            var payload = new ClipboardPayload("peer", 42, "remote text");
            var set = new ProtocolMessage(MessageKind.Request, 2, 0, "clipboard.set",
                JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await service.HandleAsync("peer", set, CancellationToken.None);
            var imported = Decode(await service.HandleAsync("peer", get, CancellationToken.None));
            Assert(imported.Origin == "peer" && imported.Version == 42 && imported.Text == "remote text",
                "Imported clipboard origin/version was not retained.");

            await backend.SetTextAsync("changed locally", CancellationToken.None);
            var changed = Decode(await service.HandleAsync("peer", get, CancellationToken.None));
            Assert(changed.Origin == "local" && changed.Text == "changed locally",
                "A new local clipboard change retained the remote origin.");
            var spoof = set with { Payload = JsonSerializer.SerializeToUtf8Bytes(
                new ClipboardPayload("other", 1, "spoof"), new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
            try { await service.HandleAsync("peer", spoof, CancellationToken.None); throw new Exception("Spoofed clipboard origin succeeded."); }
            catch (InvalidDataException) { }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ClipboardPayload Decode(ProtocolMessage message) =>
        JsonSerializer.Deserialize<ClipboardPayload>(message.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new Exception("Empty clipboard response.");

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class MemoryClipboard(string text) : ITextClipboardBackend
    {
        private string _text = text;
        private ulong _version = 1;
        public bool IsAvailable => true;
        public ValueTask<ClipboardTextSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ClipboardTextSnapshot(_text, _version));
        public ValueTask SetTextAsync(string value, CancellationToken cancellationToken)
        {
            _text = value;
            _version++;
            return ValueTask.CompletedTask;
        }
    }
}
