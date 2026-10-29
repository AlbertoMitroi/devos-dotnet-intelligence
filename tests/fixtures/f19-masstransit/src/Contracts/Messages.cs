namespace Contracts;

public record OrderSubmitted(int OrderId);

public record SubmitInvoice(int OrderId);

public record ApplicationReceived(string ApplicationId);
