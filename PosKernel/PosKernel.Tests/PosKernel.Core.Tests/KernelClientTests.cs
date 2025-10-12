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
        var engine = new KernelEngine(sessionManager, txService);
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
        var pay = await client.ProcessPaymentAsync(session, txId, 0m);
        Assert.IsTrue(pay.Success);
        Assert.AreEqual(TransactionState.EndOfTransaction, pay.Transaction!.State);
    }
}
