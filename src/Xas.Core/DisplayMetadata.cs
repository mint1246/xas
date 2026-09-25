namespace Xas.Core;

/// <summary>Display mode and panel facts gathered from the local interactive Linux session.</summary>
public sealed record DisplayMetadata
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int WidthPixels { get; init; }
    public required int HeightPixels { get; init; }
    public int? RefreshMilliHertz { get; init; }
    public int? PhysicalWidthMillimeters { get; init; }
    public int? PhysicalHeightMillimeters { get; init; }
    public int RotationDegrees { get; init; }
    public double? Scale { get; init; }
}
