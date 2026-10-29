namespace Text;

public static class Formatter
{
    public static string Format(int value) => value.ToString();

    public static string Format(string value) => value.Trim();

    public static string Shout(this string value) => value.ToUpperInvariant();
}
