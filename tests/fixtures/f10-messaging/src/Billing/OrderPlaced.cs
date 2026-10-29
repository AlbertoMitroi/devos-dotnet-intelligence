namespace Billing;

public sealed record OrderPlaced(int OrderId) : Bus.EventBase;
