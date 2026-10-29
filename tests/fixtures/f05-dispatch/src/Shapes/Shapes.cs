namespace Shapes;

public interface IShape
{
    double Area();
}

public sealed class Circle : IShape
{
    public double Area() => 3.0;
}

public sealed class Square : IShape
{
    public double Area() => 4.0;
}

public abstract class Animal
{
    public virtual string Sound() => "...";
}

public sealed class Dog : Animal
{
    public override string Sound() => "woof";
}

public static class Use
{
    public static double Total(IShape shape, Animal animal)
    {
        _ = animal.Sound();
        return shape.Area();
    }
}
