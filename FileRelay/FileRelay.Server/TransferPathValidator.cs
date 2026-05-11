using FileRelay.Core;

namespace FileRelay.Server;

internal static class TransferPathValidator
{
    private static readonly char[] PathSeparators = ['/', '\\'];
    private static readonly char[] CrossPlatformInvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static bool TryValidate(string filename, TransferContext? context, out string error)
    {
        if (!TryValidateFilename(filename, out error)) return false;
        return TryValidateRelativePath(context?.RelativePath ?? "", out error);
    }

    public static void ThrowIfInvalid(string filename, TransferContext? context)
    {
        if (!TryValidate(filename, context, out var error))
            throw new InvalidDataException(error);
    }

    public static string ResolveDirectory(string rootPath, string relativePath)
    {
        if (!TryValidateRelativePath(relativePath, out var error))
            throw new InvalidDataException(error);

        var root = GetRootFullPath(rootPath);
        var normalizedRelativePath = NormalizeRelativePath(relativePath);
        var fullPath = normalizedRelativePath.Length == 0
            ? root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : Path.GetFullPath(Path.Combine(root, normalizedRelativePath));

        EnsureUnderRoot(root, fullPath);
        return fullPath;
    }

    public static string ResolveFile(string rootPath, string relativePath, string filename)
    {
        if (!TryValidateFilename(filename, out var error))
            throw new InvalidDataException(error);

        var directory = ResolveDirectory(rootPath, relativePath);
        var fullPath = Path.GetFullPath(Path.Combine(directory, filename));
        EnsureUnderRoot(GetRootFullPath(rootPath), fullPath);
        return fullPath;
    }

    private static bool TryValidateFilename(string filename, out string error)
    {
        if (!TryValidateSegment(filename, "Filename", out error)) return false;
        if (filename.IndexOfAny(PathSeparators) >= 0)
        {
            error = "Filename must be a simple file name, not a path.";
            return false;
        }

        return true;
    }

    private static bool TryValidateRelativePath(string relativePath, out string error)
    {
        error = "";
        if (relativePath.Length == 0) return true;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "Relative path cannot be whitespace.";
            return false;
        }

        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
        {
            error = "Relative path must be a non-rooted subdirectory path.";
            return false;
        }

        var segments = relativePath.Split(PathSeparators, StringSplitOptions.None);
        foreach (var segment in segments)
        {
            if (!TryValidateSegment(segment, "Relative path segment", out error))
                return false;
        }

        return true;
    }

    private static bool TryValidateSegment(string segment, string label, out string error)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            error = $"{label} cannot be empty or whitespace.";
            return false;
        }

        if (segment is "." or "..")
        {
            error = $"{label} cannot be '.' or '..'.";
            return false;
        }

        if (segment.EndsWith(' ') || segment.EndsWith('.'))
        {
            error = $"{label} cannot end with a space or period.";
            return false;
        }

        if (segment.Any(c => char.IsControl(c) || CrossPlatformInvalidChars.Contains(c)))
        {
            error = $"{label} contains invalid path characters.";
            return false;
        }

        var reservedCheckName = segment.Split('.')[0];
        if (WindowsReservedNames.Contains(reservedCheckName))
        {
            error = $"{label} uses a reserved Windows device name.";
            return false;
        }

        error = "";
        return true;
    }

    private static string NormalizeRelativePath(string relativePath)
        => relativePath.Length == 0
            ? ""
            : Path.Combine(relativePath.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries));

    private static string GetRootFullPath(string rootPath)
        => Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

    private static void EnsureUnderRoot(string rootFullPath, string candidateFullPath)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var candidate = Path.GetFullPath(candidateFullPath);
        if (!candidate.StartsWith(rootFullPath, comparison) &&
            !string.Equals(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                rootFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison))
        {
            throw new InvalidDataException("Resolved transfer path escapes the configured target root.");
        }
    }
}
