using System.Text.RegularExpressions;

namespace Orders;

public partial class Order
{
    public partial void Validate() { }

    [GeneratedRegex("^[A-Z]+$")]
    private static partial Regex CodePattern();
}
