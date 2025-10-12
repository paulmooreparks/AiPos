using PosKernel.Core.Domain;
using PosKernel.Core.Services;

namespace PosKernel.Core.Tests;

[TestClass]
public class TransactionServiceTests
{
    private readonly ITransactionService _service = new TransactionService();

    [TestMethod]
    public void Begin_CreatesTransactionWithCurrency()
    {
        var tx = _service.Begin("USD");
        Assert.IsNotNull(tx);
        Assert.AreEqual("USD", tx.Currency);
        Assert.AreEqual(TransactionState.StartTransaction, tx.State);
        Assert.AreEqual(0, tx.Lines.Count);
        Assert.AreEqual(0m, tx.Total.Amount);
    }

    [TestMethod]
    [ExpectedException(typeof(InvalidOperationException))]
    public void Begin_FailsFast_OnMissingCurrency()
    {
        _ = _service.Begin("");
    }

    [TestMethod]
    public void AddLine_AppendsLineWithKernelProvidedValues()
    {
        var tx = _service.Begin("USD");
        var product = new ProductId("COFFEE.SMALL");
        var unit = new Money(3.50m, "USD");
        var extended = new Money(7.00m, "USD");
        tx = _service.AddLine(tx, product, 2, unit, extended);

        Assert.AreEqual(1, tx.Lines.Count);
        var line = tx.Lines[0];
        Assert.AreEqual(product, line.ProductId);
        Assert.AreEqual(2, line.Quantity);
        Assert.AreEqual(unit, line.UnitPrice);
        Assert.AreEqual(extended, line.Extended);
    }

    [TestMethod]
    [ExpectedException(typeof(InvalidOperationException))]
    public void AddLine_FailsFast_OnCurrencyMismatch()
    {
        var tx = _service.Begin("USD");
        _service.AddLine(tx, new ProductId("COFFEE.SMALL"), 1, new Money(3.50m, "USD"), new Money(3.50m, "EUR"));
    }

    [TestMethod]
    public void UpdateFromKernel_UpdatesTotalsAndState()
    {
        var tx = _service.Begin("USD");
        var total = new Money(10m, "USD");
        var tendered = new Money(20m, "USD");
        var change = new Money(10m, "USD");

        tx = _service.UpdateFromKernel(tx, total, tendered, change, TransactionState.EndOfTransaction);

        Assert.AreEqual(total, tx.Total);
        Assert.AreEqual(tendered, tx.Tendered);
        Assert.AreEqual(change, tx.ChangeDue);
        Assert.AreEqual(TransactionState.EndOfTransaction, tx.State);
    }

    [TestMethod]
    [ExpectedException(typeof(InvalidOperationException))]
    public void UpdateFromKernel_FailsFast_OnCurrencyMismatch()
    {
        var tx = _service.Begin("USD");
        _service.UpdateFromKernel(tx, new Money(1m, "USD"), new Money(1m, "USD"), new Money(0m, "EUR"), TransactionState.ItemsPending);
    }
}
