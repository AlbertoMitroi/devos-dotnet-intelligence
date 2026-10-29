namespace Multi;

public static class Platform
{
#if NET10_0_OR_GREATER
    public static string Name() => "ten";
#else
    public static string Legacy() => "older";
#endif
}
