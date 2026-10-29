namespace Legacy;

#if SDK_MAJOR_9
public class EvaluatedBySdk9 { }
#endif

#if SDK_MAJOR_10
public class EvaluatedBySdk10 { }
#endif

public class Service
{
    public string Describe() => Formatter.Format(1);
}

public static class Formatter
{
    public static string Format(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
