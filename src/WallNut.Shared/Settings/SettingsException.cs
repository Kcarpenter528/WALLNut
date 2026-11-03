namespace WallNut.Shared;

/// <summary>The service refused a settings change; <see cref="Problems"/> says why, in plain words.</summary>
public sealed class SettingsException(IReadOnlyList<string> problems) : Exception(string.Join(" ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
