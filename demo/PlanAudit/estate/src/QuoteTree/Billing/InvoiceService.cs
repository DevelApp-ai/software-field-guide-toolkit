using System;

namespace QuoteTree.Billing
{
    public Invoice Issue(decimal totalDue, string quoteId, DateTime paymentDeadline)
    {
        return new Invoice(quoteId, totalDue, paymentDeadline, isPaid: false);
    }
}

public sealed record Invoice(string QuoteId, decimal TotalDue, DateTime PaymentDeadline, bool IsPaid);
