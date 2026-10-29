namespace Text;

public sealed class Usage
{
    public string Run()
    {
        var a = Formatter.Format(42);
        var b = Formatter.Format(" x ");
        return a + b.Shout();
    }
}
