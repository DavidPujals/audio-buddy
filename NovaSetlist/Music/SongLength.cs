namespace NovaSetlist.Music;

/// <summary>Parses and formats song lengths from the sheet's Length column.</summary>
public static class SongLength
{
    /// <summary>
    /// "3:45" (m:ss), "1:02:30" (h:mm:ss) or "0:03:45:12" (h:mm:ss:ff, frames at 25 fps).
    /// Returns 0 for blank/unparsable input.
    /// </summary>
    public static double ParseSeconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var parts = text.Trim().Split(':');
        if (parts.Length == 1)
            return int.TryParse(parts[0], out var plain) && plain > 0 ? plain : 0; // bare seconds ("105")
        if (parts.Length > 4)
            return 0;

        var nums = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i].Trim(), out nums[i]) || nums[i] < 0)
                return 0;
        }

        return parts.Length switch
        {
            2 => nums[0] * 60 + nums[1],
            3 => nums[0] * 3600 + nums[1] * 60 + nums[2],
            _ => nums[0] * 3600 + nums[1] * 60 + nums[2] + nums[3] / 25.0,
        };
    }

    /// <summary>Seconds → "m:ss" (or "h:mm:ss" over an hour). Negative values are clamped to 0.</summary>
    public static string Format(double seconds)
    {
        var s = (long)Math.Max(0, Math.Round(seconds));
        return s >= 3600
            ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}"
            : $"{s / 60}:{s % 60:00}";
    }
}
