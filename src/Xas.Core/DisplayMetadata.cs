namespace Xas.Core;

/// <summary>Physical panel mode and compositor coordinates from the interactive desktop session.</summary>
public sealed record DisplayMetadata
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Stable output/connector name within the local display server (for example eDP-1).</summary>
    public string? ConnectorId { get; init; }
    /// <summary>Unrotated active hardware mode. A virtual monitor should rotate these dimensions if needed.</summary>
    public int? NativeWidthPixels { get; init; }
    public int? NativeHeightPixels { get; init; }
    /// <summary>Desktop coordinate rectangle after rotation and scaling. Origin may be negative.</summary>
    public int? LogicalX { get; init; }
    public int? LogicalY { get; init; }
    public int? LogicalWidth { get; init; }
    public int? LogicalHeight { get; init; }
    /// <summary>Legacy logical, oriented dimensions retained for older peers and callers.</summary>
    public required int WidthPixels { get; init; }
    public required int HeightPixels { get; init; }
    public int? RefreshMilliHertz { get; init; }
    public int? PhysicalWidthMillimeters { get; init; }
    public int? PhysicalHeightMillimeters { get; init; }
    public int RotationDegrees { get; init; }
    public double? Scale { get; init; }
}
