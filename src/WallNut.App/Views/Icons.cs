using Avalonia.Media;

namespace WallNut.App.Views;

/// <summary>Simple line icons for the sidebar, drawn on a 24x24 grid.</summary>
public static class Icons
{
    public static readonly Geometry Overview = Geometry.Parse("M3,3H10V10H3Z M14,3H21V10H14Z M3,14H10V21H3Z M14,14H21V21H14Z");
    public static readonly Geometry History = Geometry.Parse(
        "M2,2H4V20H22V22H2Z M6,16.6L10.5,10.8L14,13.6L19.4,6.4L21,7.6L14.4,16.4L10.9,13.6L7.6,17.8Z");
    public static readonly Geometry Events = Geometry.Parse(
        "M3,5H6V8H3Z M8,5.5H21V7.5H8Z M3,10.5H6V13.5H3Z M8,11H21V13H8Z M3,16H6V19H3Z M8,16.5H21V18.5H8Z");
    public static readonly Geometry Settings = Geometry.Parse(
        "M3,6H11V8H3Z M15,6H21V8H15Z M11,4H15V10H11Z M3,16H7V18H3Z M11,16H21V18H11Z M7,14H11V20H7Z");
    public static readonly Geometry Ups = Geometry.Parse(
        "M6,2H18A2,2 0 0 1 20,4V20A2,2 0 0 1 18,22H6A2,2 0 0 1 4,20V4A2,2 0 0 1 6,2Z M7,5V9H17V5Z M12.8,11L9,16.5H11.6L11,20L15,14.4H12.4Z");
}
