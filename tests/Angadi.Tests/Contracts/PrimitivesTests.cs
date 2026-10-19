using Angadi.Application.Billing;
using Angadi.Domain.Common;

namespace Angadi.Tests.Contracts;

public class PrimitivesTests
{
    [Fact]
    public void Paise_Arithmetic_IsExact()
    {
        var a = Paise.FromRupees(10);
        var b = new Paise(1);
        Assert.Equal(1001, (a + b).Value);
        Assert.Equal(999, (a - b).Value);
        Assert.Equal("₹10.01", (a + b).ToString());
    }

    [Fact]
    public void Qty_HalfKilo_IsFiveHundredMilli()
    {
        Assert.Equal(500, new Qty(500).Milli);
        Assert.Equal(Qty.Whole(2), new Qty(1500) + new Qty(500));
    }

    [Fact]
    public void FR14_UpiLink_HasPayeeAmountAndCurrency()
    {
        var uri = UpiLink.Build("ramu@okaxis", "Ramu Kirana", new Paise(12350), "A-000007");
        Assert.Equal("upi://pay?pa=ramu@okaxis&pn=Ramu%20Kirana&am=123.50&cu=INR&tn=A-000007", uri);
    }
}
