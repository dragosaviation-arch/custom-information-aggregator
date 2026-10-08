using System.ComponentModel;
using System.Diagnostics;

namespace CIA.Desktop.Presentation;

public interface IExternalLinkLauncher
{
    bool TryOpen(Uri destination);
}

public sealed class WindowsExternalLinkLauncher : IExternalLinkLauncher
{
    public bool TryOpen(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.IsAbsoluteUri
            || !string.Equals(destination.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = destination.AbsoluteUri,
                UseShellExecute = true
            }) is not null;
        }
        catch (Exception exception) when (exception is Win32Exception
                                          or InvalidOperationException
                                          or NotSupportedException)
        {
            return false;
        }
    }
}
