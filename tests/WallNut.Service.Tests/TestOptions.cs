using Microsoft.Extensions.Options;

namespace WallNut.Service.Tests;

/// <summary>Fixed settings for components that watch for settings changes.</summary>
internal sealed class FixedOptions<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

internal static class TestOptions
{
    public static IOptionsMonitor<T> Fixed<T>(T value) => new FixedOptions<T>(value);
}
