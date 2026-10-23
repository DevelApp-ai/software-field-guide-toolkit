using System;
using System.Collections.Generic;
using System.Linq;

namespace QuoteTree.Quote
{
    public QuoteIssue Quote(IReadOnlyList<LineItem> items, string customerId)
    {
        var total = items.Sum(i => i.Price * i.Quantity);
        return new QuoteIssue(total, customerId, DateTime.UtcNow.AddDays(30));
    }
}

public sealed record QuoteIssue(decimal Total, string CustomerId, DateTime ValidUntil);
