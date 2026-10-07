using System.Reflection;

namespace GitHr.App;

/// <summary>Version of the running build, from the assembly metadata (set by Directory.Build.props or -p:Version).</summary>
public static class AppInfo
{
    static AppInfo()
    {
        // The SDK appends the source commit: "0.1.0+a1b2c3d4e5f6...".
        var informational = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+');
        Version = plus < 0 ? informational : informational[..plus];
        Commit = plus < 0 ? null : informational[(plus + 1)..][..System.Math.Min(7, informational.Length - plus - 1)];
    }

    /// <summary>"1.2.0", or "1.3.0-beta.1" for a pre-release.</summary>
    public static string Version { get; }

    /// <summary>Short SHA of the commit the build was made from, if the SDK recorded it.</summary>
    public static string? Commit { get; }

    /// <summary>"GitHr 1.2.0 (a1b2c3d)"</summary>
    public static string DisplayVersion => Commit is null ? $"GitHr {Version}" : $"GitHr {Version} ({Commit})";
}
