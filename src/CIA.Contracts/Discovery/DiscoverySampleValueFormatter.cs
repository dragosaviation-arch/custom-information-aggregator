using System.Text;

namespace CIA.Contracts.Discovery;

public static class DiscoverySampleValueFormatter
{
    public const int MaximumLength = 160;

    public static string Format(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var sample = new StringBuilder(Math.Min(value.Length, MaximumLength));
        var pendingWhitespace = false;
        var truncated = false;

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingWhitespace = sample.Length > 0;
                continue;
            }

            if (pendingWhitespace)
            {
                if (sample.Length >= MaximumLength - 1)
                {
                    truncated = true;
                    break;
                }

                sample.Append(' ');
                pendingWhitespace = false;
            }

            if (sample.Length >= MaximumLength)
            {
                truncated = true;
                break;
            }

            sample.Append(character);
        }

        if (!truncated)
        {
            return sample.ToString();
        }

        if (sample.Length >= MaximumLength)
        {
            sample.Length = MaximumLength - 1;
        }

        sample.Append('…');
        return sample.ToString();
    }
}
