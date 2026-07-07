using System.Reflection;

namespace bzTorrentClient.Avalonia.Features.About;

public sealed class AboutViewModel
{
    /// <summary>The build version, formatted YY.MM.DD for release builds (CI stamps it via -p:Version); a dev build shows its default assembly version.</summary>
    public string Version { get; } = ResolveVersion();

    public string VersionLine => $"bzTorrentClient version {Version}";

    public string Copyright => "Copyright Darren Horrocks 2026";

    private static string ResolveVersion()
    {
        var informational = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
            informational = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

        // Drop any SourceLink build-metadata suffix (e.g. "26.07.07+<commit sha>").
        var plusIndex = informational.IndexOf('+');
        return plusIndex >= 0 ? informational[..plusIndex] : informational;
    }
}
