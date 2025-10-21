﻿namespace Camera.MAUI;

public class CameraInfo
{
    public string Name { get; internal set; }
    public string DeviceId { get; internal set; }
    public CameraPosition Position { get; internal set; }
    public bool IsVirtual { get; internal set; }
    public bool HasFlashUnit { get; internal set; }
    public float MinZoomFactor { get; internal set; }
    public float MaxZoomFactor { get; internal set; }
    public float ZoomFactorMultiplier { get; internal set; } = 1.0f;
    public float HorizontalViewAngle { get; internal set; }
    public float VerticalViewAngle { get; internal set; }
    public float MinimumFocusDistance { get; internal set; }
    public bool SupportsFixedFocus { get; internal set; }
    public List<Size> AvailableResolutions { get; internal set; }
    public override string ToString()
    {
        return Name;
    }
}
