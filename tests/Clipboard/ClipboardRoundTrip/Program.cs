using Xas.Core;

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("This check requires Windows.");

var clipboard = new WindowsTextClipboard();
var original = await clipboard.GetSnapshotAsync(CancellationToken.None);
var marker = $"Xas clipboard round-trip {Guid.NewGuid():N}";
try
{
    await clipboard.SetTextAsync(marker, CancellationToken.None);
    var actual = await clipboard.GetSnapshotAsync(CancellationToken.None);
    if (actual.Text != marker || actual.ChangeId == original.ChangeId)
        throw new InvalidOperationException($"Clipboard round-trip failed. Text matched: {actual.Text == marker}; sequence advanced: {actual.ChangeId != original.ChangeId}.");
    Console.WriteLine("Clipboard round-trip passed; sequence number advanced.");
}
finally
{
    await clipboard.SetTextAsync(original.Text, CancellationToken.None);
}
