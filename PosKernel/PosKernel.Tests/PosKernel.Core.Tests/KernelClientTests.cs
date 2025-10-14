using PosKernel.Client;
using PosKernel.Core.Domain;
using PosKernel.Core.Interfaces;
using PosKernel.Core.Services;

namespace PosKernel.Core.Tests;

[TestClass]
public class KernelClientTests
{
    private IKernelClient CreateClient()
    {
    var sessionManager = new SessionManager();
        var txService = new TransactionService();
        var engine = new KernelEngine(sessionManager, txService, new DefaultPaymentRules());
        return new DirectKernelClient(engine);
    }

    [TestMethod]
    public async Task DirectClient_CanCompleteBasicLifecycle()
    {
        var client = CreateClient();
        var session = await client.CreateSessionAsync("TERM1", "OP1");
        var start = await client.StartTransactionAsync(session, "USD");
        Assert.IsTrue(start.Success);
        var txId = start.Transaction!.Id.ToString();
        var add = await client.AddLineItemAsync(session, txId, "COFFEE.SMALL", 2, 3.50m);
        Assert.IsTrue(add.Success);
        Assert.AreEqual(TransactionState.ItemsPending, add.Transaction!.State);
        // Total due is 2 * 3.50 = 7.00; must tender full amount to complete
    var pay = await client.ProcessPaymentAsync(session, txId, 7.00m, "cash");
        Assert.IsTrue(pay.Success);
        Assert.AreEqual(TransactionState.EndOfTransaction, pay.Transaction!.State);
        Assert.AreEqual(7.00m, pay.Transaction.Tendered.Amount);
        Assert.AreEqual(0m, pay.Transaction.ChangeDue.Amount);
    }

    [TestMethod]
    public async Task Totals_AlwaysMatchSumOfNonVoidedLines()
    {
        // Arrange
        var client = CreateClient();
        var session = await client.CreateSessionAsync("TERM1", "OP1");
        var start = await client.StartTransactionAsync(session, "USD");
        Assert.IsTrue(start.Success, string.Join(';', start.Errors));
        var txId = start.Transaction!.Id.ToString();

        // Act - add multiple lines with varying quantities
        var add1 = await client.AddLineItemAsync(session, txId, "COFFEE.SMALL", 2, 3.50m);
        Assert.IsTrue(add1.Success);
        var after1 = add1.Transaction!;
        var expected1 = after1.Lines.Where(l => !l.IsVoided).Sum(l => l.Extended.Amount);
        Assert.AreEqual(expected1, after1.Total.Amount, "Total after first add mismatch");

        var add2 = await client.AddLineItemAsync(session, txId, "TEA.ICED", 1, 4.10m);
        Assert.IsTrue(add2.Success);
        var after2 = add2.Transaction!;
        var expected2 = after2.Lines.Where(l => !l.IsVoided).Sum(l => l.Extended.Amount);
        Assert.AreEqual(expected2, after2.Total.Amount, "Total after second add mismatch");

        var add3 = await client.AddLineItemAsync(session, txId, "SANDWICH.CLUB", 3, 6.25m);
        Assert.IsTrue(add3.Success);
        var after3 = add3.Transaction!;
        var expected3 = after3.Lines.Where(l => !l.IsVoided).Sum(l => l.Extended.Amount);
        Assert.AreEqual(expected3, after3.Total.Amount, "Total after third add mismatch");

        // Assert lifecycle state remains ItemsPending with lines present
        Assert.AreEqual(TransactionState.ItemsPending, after3.State);
    }

    [TestMethod]
    public async Task Payment_ComputesCorrectChangeAgainstKernelTotal()
    {
        // Arrange
        var client = CreateClient();
        var session = await client.CreateSessionAsync("TERM1", "OP1");
        var start = await client.StartTransactionAsync(session, "USD");
        Assert.IsTrue(start.Success, string.Join(';', start.Errors));
        var txId = start.Transaction!.Id.ToString();
        // Add known priced items
        await client.AddLineItemAsync(session, txId, "ITEM.A", 1, 2.00m);
        await client.AddLineItemAsync(session, txId, "ITEM.B", 2, 1.25m); // 2 * 1.25 = 2.50
        var snapshot = await client.GetTransactionAsync(session, txId);
        Assert.IsTrue(snapshot.Success);
        var tx = snapshot.Transaction!;
        var expectedTotal = tx.Lines.Sum(l => l.Extended.Amount);
        Assert.AreEqual(expectedTotal, tx.Total.Amount, "Pre-payment total mismatch");

        // Act - pay with amount producing clean change
        var tenderAmount = 10.00m;
    var payment = await client.ProcessPaymentAsync(session, txId, tenderAmount, "cash");
        Assert.IsTrue(payment.Success, string.Join(';', payment.Errors));
        var paid = payment.Transaction!;
        Assert.AreEqual(TransactionState.EndOfTransaction, paid.State);
        Assert.AreEqual(expectedTotal, paid.Total.Amount);
        var expectedChange = tenderAmount - expectedTotal;
        Assert.AreEqual(expectedChange, paid.ChangeDue.Amount);
        Assert.AreEqual(tenderAmount, paid.Tendered.Amount);
        // Lines: 2 items + 1 tender + optional change (if overpay) -> here overpay expected
        Assert.IsTrue(paid.Lines.Any(l => l.LineType == TransactionLineType.Tender));
        Assert.IsTrue(paid.Lines.Any(l => l.LineType == TransactionLineType.Change));
    // Tender line stored negative internally
    var tenderLine = paid.Lines.First(l => l.LineType == TransactionLineType.Tender);
    Assert.IsTrue(tenderLine.Extended.Amount < 0m, "Tender line amount should be negative internally");
    var changeLine = paid.Lines.First(l => l.LineType == TransactionLineType.Change);
    Assert.IsTrue(changeLine.Extended.Amount > 0m, "Change line amount should be positive");
    }

    [TestMethod]
    public async Task Payment_PartialTenderAccumulatesUntilFullyPaid()
    {
        var client = CreateClient();
        var session = await client.CreateSessionAsync("TERM1", "OP1");
        var start = await client.StartTransactionAsync(session, "USD");
        var txId = start.Transaction!.Id.ToString();
        await client.AddLineItemAsync(session, txId, "ITEM.A", 1, 5.00m); // total 5.00
    var p1 = await client.ProcessPaymentAsync(session, txId, 2.00m, "cash");
        Assert.IsTrue(p1.Success);
        Assert.AreEqual(TransactionState.ItemsPending, p1.Transaction!.State, "Should remain pending after partial tender");
    Assert.AreEqual(2.00m, p1.Transaction.Tendered.Amount);
    Assert.AreEqual(0m, p1.Transaction.ChangeDue.Amount);
    Assert.AreEqual(1, p1.Transaction.Lines.Count(l => l.LineType == TransactionLineType.Tender));
    var p2 = await client.ProcessPaymentAsync(session, txId, 3.00m, "cash"); // cumulative 5.00
        Assert.IsTrue(p2.Success);
        Assert.AreEqual(TransactionState.EndOfTransaction, p2.Transaction!.State, "Should finalize after reaching total");
    Assert.AreEqual(5.00m, p2.Transaction.Tendered.Amount);
    Assert.AreEqual(0m, p2.Transaction.ChangeDue.Amount);
    Assert.AreEqual(2, p2.Transaction.Lines.Count(l => l.LineType == TransactionLineType.Tender));
    Assert.AreEqual(0, p2.Transaction.Lines.Count(l => l.LineType == TransactionLineType.Change));
    var p3 = await client.ProcessPaymentAsync(session, txId, 1.00m, "cash"); // extra after closed
        Assert.IsFalse(p3.Success, "Should not accept payment after end of transaction");
    }

    [TestMethod]
    public async Task Payment_OverTenderProducesChangeAndEnds()
    {
        var client = CreateClient();
        var session = await client.CreateSessionAsync("TERM1", "OP1");
        var start = await client.StartTransactionAsync(session, "USD");
        var txId = start.Transaction!.Id.ToString();
        await client.AddLineItemAsync(session, txId, "ITEM.A", 2, 4.00m); // total 8.00
    var pay = await client.ProcessPaymentAsync(session, txId, 10.00m, "cash");
        Assert.IsTrue(pay.Success);
        Assert.AreEqual(TransactionState.EndOfTransaction, pay.Transaction!.State);
        Assert.AreEqual(10.00m, pay.Transaction.Tendered.Amount);
        Assert.AreEqual(2.00m, pay.Transaction.ChangeDue.Amount);
        Assert.AreEqual(1, pay.Transaction.Lines.Count(l => l.LineType == TransactionLineType.Tender));
        Assert.AreEqual(1, pay.Transaction.Lines.Count(l => l.LineType == TransactionLineType.Change));
    Assert.IsTrue(pay.Transaction.Lines.First(l => l.LineType == TransactionLineType.Tender).Extended.Amount < 0m);
    Assert.IsTrue(pay.Transaction.Lines.First(l => l.LineType == TransactionLineType.Change).Extended.Amount > 0m);
    }

    [TestMethod]
    public async Task AddLine_InvalidQuantityOrPrice_Fails()
    {
        var client = CreateClient();
        var session = await client.CreateSessionAsync("TERM1", "OP1");
        var start = await client.StartTransactionAsync(session, "USD");
        var txId = start.Transaction!.Id.ToString();
        var badQty = await client.AddLineItemAsync(session, txId, "ITEM.A", 0, 1.00m);
        Assert.IsFalse(badQty.Success, "Zero quantity should fail");
        var badPrice = await client.AddLineItemAsync(session, txId, "ITEM.A", 1, -1.00m);
        Assert.IsFalse(badPrice.Success, "Negative price should fail");
        var good = await client.AddLineItemAsync(session, txId, "ITEM.A", 1, 1.00m);
        Assert.IsTrue(good.Success, "Valid line should succeed");
    }

    [TestMethod]
    public async Task Payment_NegativeAmount_Fails()
    {
        var client = CreateClient();
        var session = await client.CreateSessionAsync("TERM1", "OP1");
        var start = await client.StartTransactionAsync(session, "USD");
        var txId = start.Transaction!.Id.ToString();
        await client.AddLineItemAsync(session, txId, "ITEM.A", 1, 2.00m);
    var pay = await client.ProcessPaymentAsync(session, txId, -0.01m, "cash");
        Assert.IsFalse(pay.Success, "Negative payment should fail");
    }

    [TestMethod]
    public async Task Overpay_With_NonCashTender_Fails()
    {
        var client = CreateClient();
        var session = await client.CreateSessionAsync("TERM1", "OP1");
        var start = await client.StartTransactionAsync(session, "USD");
        var txId = start.Transaction!.Id.ToString();
        await client.AddLineItemAsync(session, txId, "ITEM.A", 1, 5.00m);
        // Exact pay with card should succeed
        var exact = await client.ProcessPaymentAsync(session, txId, 5.00m, "card");
        Assert.IsTrue(exact.Success, string.Join(';', exact.Errors));
        // New transaction to test overpay
        var start2 = await client.StartTransactionAsync(session, "USD");
        var txId2 = start2.Transaction!.Id.ToString();
        await client.AddLineItemAsync(session, txId2, "ITEM.A", 1, 5.00m);
        var over = await client.ProcessPaymentAsync(session, txId2, 10.00m, "card");
        Assert.IsFalse(over.Success, "Overpay with non-cash tender should fail");
    }
}
