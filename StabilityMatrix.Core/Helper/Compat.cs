﻿using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Semver;
using StabilityMatrix.Core.Models.FileInterfaces;

namespace StabilityMatrix.Core.Helper;

/// <summary>
/// Compatibility layer for checks and file paths on different platforms.
/// </summary>
[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
public static class Compat
{
    private const string AppName = "StabilityMatrix";

    public static SemVersion AppVersion { get; set; }

    // OS Platform
    public static PlatformKind Platform { get; }

    [SupportedOSPlatformGuard("windows")]
    public static bool IsWindows => Platform.HasFlag(PlatformKind.Windows);

    [SupportedOSPlatformGuard("linux")]
    public static bool IsLinux => Platform.HasFlag(PlatformKind.Linux);

    [SupportedOSPlatformGuard("macos")]
    public static bool IsMacOS => Platform.HasFlag(PlatformKind.MacOS);

    [UnsupportedOSPlatformGuard("windows")]
    public static bool IsUnix => Platform.HasFlag(PlatformKind.Unix);

    public static bool IsArm => Platform.HasFlag(PlatformKind.Arm);
    public static bool IsX64 => Platform.HasFlag(PlatformKind.X64);

    // Paths

    /// <summary>
    /// AppData directory path. On Windows this is %AppData%, on Linux and MacOS this is ~/.config
    /// </summary>
    public static DirectoryPath AppData { get; }

    /// <summary>
    /// AppData + AppName (e.g. %AppData%\StabilityMatrix)
    /// </summary>
    public static DirectoryPath AppDataHome { get; private set; }

    /// <summary>
    /// Set AppDataHome to a custom path. Used for testing.
    /// </summary>
    internal static void SetAppDataHome(string path)
    {
        AppDataHome = path;
    }

    /// <summary>
    /// Current directory the app is in.
    /// </summary>
    public static DirectoryPath AppCurrentDir { get; }

    /// <summary>
    /// Current path to the app.
    /// </summary>
    public static FilePath AppCurrentPath => AppCurrentDir.JoinFile(GetExecutableName());

    // File extensions
    /// <summary>
    /// Platform-specific executable extension.
    /// ".exe" on Windows, Empty string on Linux and MacOS.
    /// </summary>
    public static string ExeExtension { get; }

    /// <summary>
    /// Platform-specific dynamic library extension.
    /// ".dll" on Windows, ".dylib" on MacOS, ".so" on Linux.
    /// </summary>
    public static string DllExtension { get; }

    /// <summary>
    /// Delimiter for $PATH environment variable.
    /// </summary>
    public static char PathDelimiter => IsWindows ? ';' : ':';

    static Compat()
    {
        var infoVersion = Assembly
            .GetCallingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        AppVersion = SemVersion.Parse(infoVersion ?? "0.0.0", SemVersionStyles.Strict);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Platform = PlatformKind.Windows;
            AppCurrentDir = AppContext.BaseDirectory;
            ExeExtension = ".exe";
            DllExtension = ".dll";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Platform = PlatformKind.MacOS | PlatformKind.Unix;
            AppCurrentDir = AppContext.BaseDirectory; // TODO: check this
            ExeExtension = "";
            DllExtension = ".dylib";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Platform = PlatformKind.Linux | PlatformKind.Unix;

            // For AppImage builds, the path is in `$APPIMAGE`
            var appPath =
                Environment.GetEnvironmentVariable("APPIMAGE") ?? AppContext.BaseDirectory;
            AppCurrentDir =
                Path.GetDirectoryName(appPath)
                ?? throw new Exception("Could not find application directory");
            ExeExtension = "";
            DllExtension = ".so";
        }
        else
        {
            throw new PlatformNotSupportedException();
        }

        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            Platform |= PlatformKind.Arm;
        }
        else
        {
            Platform |= PlatformKind.X64;
        }

        AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (
            Environment.GetEnvironmentVariable("STABILITY_MATRIX_APPDATAHOME") is
            { } appDataOverride
        )
        {
            AppDataHome = appDataOverride;
        }
        else
        {
            AppDataHome = AppData + AppName;
        }
    }

    /// <summary>
    /// Generic function to return different objects based on platform flags.
    /// Parameters are checked in sequence with Compat.Platform.HasFlag,
    /// the first match is returned.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">Thrown when no targets match</exception>
    public static T Switch<T>(params (PlatformKind platform, T target)[] targets)
    {
        foreach (var (platform, target) in targets)
        {
            if (Platform.HasFlag(platform))
            {
                return target;
            }
        }

        throw new PlatformNotSupportedException(
            $"Platform {Platform.ToString()} is not in supported targets: "
                + $"{string.Join(", ", targets.Select(t => t.platform.ToString()))}"
        );
    }

    /// <summary>
    /// Get the current application executable name.
    /// </summary>
    public static string GetExecutableName()
    {
        if (IsLinux)
        {
            // Use name component of APPIMAGE
            var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
            if (string.IsNullOrEmpty(appImage))
            {
                throw new Exception("Could not find APPIMAGE environment variable");
            }
            return Path.GetFileName(appImage);
        }
        using var process = Process.GetCurrentProcess();
        var fullPath = process.MainModule?.ModuleName;

        if (string.IsNullOrEmpty(fullPath))
        {
            throw new Exception("Could not find executable name");
        }
        return Path.GetFileName(fullPath);
    }

    public static string GetEnvPathWithExtensions(params string[] paths)
    {
        var currentPath = Environment.GetEnvironmentVariable(
            "PATH",
            EnvironmentVariableTarget.Process
        );
        var newPath = string.Join(PathDelimiter, paths);

        if (string.IsNullOrEmpty(currentPath))
        {
            return string.Join(PathDelimiter, paths);
        }
        else
        {
            return newPath + PathDelimiter + currentPath;
        }
    }
}
