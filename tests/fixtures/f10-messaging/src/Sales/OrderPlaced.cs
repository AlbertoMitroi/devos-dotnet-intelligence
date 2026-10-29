namespace Sales;

public sealed record OrderPlaced(int OrderId) : Bus.EventBase;
