namespace Edzio.SignalingServer;

/// <summary>
/// Resolves which TCP port the signaling server should listen on, based on
/// the platform-provided <c>PORT</c> environment variable, falling back to
/// a fixed default when the variable is absent, empty, or not a valid
/// positive port number.
/// </summary>
public static class ServerPortResolver
{
    /// <summary>
    /// The port used when no valid <c>PORT</c> environment variable is set.
    /// This is also the port the published Docker image exposes.
    /// </summary>
    public const int DefaultPort = 8080;

    /// <summary>
    /// Resolves the port to listen on from the given environment variable value.
    /// </summary>
    /// <param name="portEnvironmentVariable">
    /// The raw value of the <c>PORT</c> environment variable, or <c>null</c> if unset.
    /// </param>
    /// <returns>
    /// The parsed port number if <paramref name="portEnvironmentVariable"/> is a valid
    /// positive integer; otherwise <see cref="DefaultPort"/>.
    /// </returns>
    public static int Resolve(string? portEnvironmentVariable)
    {
        if (int.TryParse(portEnvironmentVariable, out var parsedPort) && parsedPort > 0)
        {
            return parsedPort;
        }

        return DefaultPort;
    }
}
