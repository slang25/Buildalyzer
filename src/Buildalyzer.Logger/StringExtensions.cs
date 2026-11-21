namespace System;

/// <summary>
/// Internal string helpers, mirroring the ones in Buildalyzer's own <c>Extensions/System.String.cs</c>.
/// </summary>
/// <remarks>
/// This assembly runs inside MSBuild and cannot reference Buildalyzer itself, so it keeps its own copy.
/// </remarks>
internal static class BuildalyzerLoggerStringExtensions
{
    /// <summary>
    /// Returns true if the <paramref name="value"/> string has the same value, ignoring casing.
    /// </summary>
    public static bool IsMatch(this string? self, string? value) => string.Equals(self, value, StringComparison.OrdinalIgnoreCase);
}
