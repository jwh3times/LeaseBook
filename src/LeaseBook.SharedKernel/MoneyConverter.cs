using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LeaseBook.SharedKernel;

/// <summary>Maps <see cref="Money"/> &lt;-&gt; <see cref="decimal"/> (NUMERIC(14,2) in Postgres).</summary>
public sealed class MoneyConverter : ValueConverter<Money, decimal>
{
    public MoneyConverter()
        : base(money => money.Amount, amount => new Money(amount))
    {
    }
}
