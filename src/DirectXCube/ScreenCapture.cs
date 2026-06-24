namespace DirectXCube;

/// <summary>Command-line helper for the headless screenshot used in the README.</summary>
internal static class ScreenCapture
{
    /// <summary>Returns the path following a <c>--capture</c> argument, or <see langword="null"/>.</summary>
    public static string? PathFrom(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--capture")
                return args[i + 1];
        return null;
    }
}
