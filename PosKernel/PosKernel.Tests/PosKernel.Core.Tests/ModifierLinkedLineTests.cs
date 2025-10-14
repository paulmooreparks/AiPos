using PosKernel.Client;
using PosKernel.Core.Domain;
using PosKernel.Core.Interfaces;
using PosKernel.Core.Services;

namespace PosKernel.Core.Tests;

/// <summary>
/// Tests for linked modifier line behavior (hierarchical line items) without performing pricing math outside kernel.
/// </summary>
[TestClass]
public class ModifierLinkedLineTests
{
    private IKernelClient CreateClient()
    {
        var sessionManager = new SessionManager();
        var txService = new TransactionService();
        var engine = new KernelEngine(sessionManager, txService, new DefaultPaymentRules());
        return new DirectKernelClient(engine);
    }

    [TestMethod]
    public async Task Adding_Surcharge_Modifier_CreatesLinkedLine_WithCorrectTotals()
    {
        var client = CreateClient();
        var session = await client.CreateSessionAsync("TERM", "OP");
        var start = await client.StartTransactionAsync(session, "USD");
        var txId = start.Transaction!.Id.ToString();
        // Parent item quantity 2 @ 3.00 = 6.00
        var parent = await client.AddLineItemAsync(session, txId, "COFFEE", 2, 3.00m, "Coffee", null, null);
        Assert.IsTrue(parent.Success, string.Join(';', parent.Errors));
        var parentLine = parent.Transaction!.Lines.Last();
        Assert.AreEqual(6.00m, parent.Transaction.Total.Amount);
        // Add modifier with surcharge per unit 0.10; quantity mirrors parent (2) => extended 0.20
        var mod = await client.AddLineItemAsync(session, txId, "MOD_ICED", parentLine.Quantity, 0.10m, "Iced (Peng)", null, parentLine.LineItemId);
        Assert.IsTrue(mod.Success, string.Join(';', mod.Errors));
        var tx = mod.Transaction!;
        var child = tx.Lines.Last();
        Assert.AreEqual(parentLine.LineItemId, child.ParentLineItemId, "Child should reference parent line id");
        Assert.AreEqual(parentLine.Quantity, child.Quantity, "Modifier quantity must mirror parent");
        Assert.AreEqual(0.10m, child.UnitPrice.Amount);
        Assert.AreEqual(0.20m, child.Extended.Amount);
        // Total now parent 6.00 + modifier 0.20 = 6.20
        Assert.AreEqual(6.20m, tx.Total.Amount, "Transaction total should include modifier surcharge");
        // Integrity: merchandise total equals sum of non-void item lines
        var recomputed = tx.Lines.Where(l => !l.IsVoided && l.LineType == TransactionLineType.Item).Sum(l => l.Extended.Amount);
        Assert.AreEqual(recomputed, tx.Total.Amount);
    }

    [TestMethod]
    public async Task Adding_ZeroPriced_Modifier_StillCreatesLinkedLine_NoTotalChange()
    {
        var client = CreateClient();
        var session = await client.CreateSessionAsync("TERM", "OP");
        var start = await client.StartTransactionAsync(session, "USD");
        var txId = start.Transaction!.Id.ToString();
        var parent = await client.AddLineItemAsync(session, txId, "TEA", 1, 2.50m, "Tea", null, null);
        Assert.IsTrue(parent.Success);
        var parentLine = parent.Transaction!.Lines.Last();
        var beforeTotal = parent.Transaction.Total.Amount;
        var mod = await client.AddLineItemAsync(session, txId, "MOD_WEAK", parentLine.Quantity, 0m, "Weak (Po)", null, parentLine.LineItemId);
        Assert.IsTrue(mod.Success);
        var tx = mod.Transaction!;
        var child = tx.Lines.Last();
        Assert.AreEqual(0m, child.UnitPrice.Amount);
        Assert.AreEqual(0m, child.Extended.Amount);
        Assert.AreEqual(beforeTotal, tx.Total.Amount, "Zero priced modifier must not change total");
    }

    [TestMethod]
    public async Task Void_Parent_Cascades_To_Modifiers()
    {
        var client = CreateClient();
        var session = await client.CreateSessionAsync("TERM", "OP");
        var start = await client.StartTransactionAsync(session, "USD");
        var txId = start.Transaction!.Id.ToString();
        var parent = await client.AddLineItemAsync(session, txId, "DRINK", 1, 5.00m, "Drink", null, null);
        Assert.IsTrue(parent.Success);
        var parentLine = parent.Transaction!.Lines.Last();
        var mod1 = await client.AddLineItemAsync(session, txId, "MOD_ICED", 1, 0.10m, "Iced", null, parentLine.LineItemId);
        Assert.IsTrue(mod1.Success);
        var mod2 = await client.AddLineItemAsync(session, txId, "MOD_LESS_SUGAR", 1, 0m, "Less Sugar", null, parentLine.LineItemId);
        Assert.IsTrue(mod2.Success);
        var withMods = mod2.Transaction!;
        Assert.AreEqual(3, withMods.Lines.Count(l => l.LineType == TransactionLineType.Item));
        // Void parent -> cascade void children
        var voidResult = await client.VoidLineItemAsync(session, txId, parentLine.LineItemId, "Customer canceled");
        Assert.IsTrue(voidResult.Success, string.Join(';', voidResult.Errors));
        var after = voidResult.Transaction!;
        var voidedParent = after.Lines.First(l => l.LineItemId == parentLine.LineItemId);
        Assert.IsTrue(voidedParent.IsVoided, "Parent should be voided");
        var childLines = after.Lines.Where(l => l.ParentLineItemId == parentLine.LineItemId).ToList();
        Assert.IsTrue(childLines.All(c => c.IsVoided), "All child modifier lines must be voided");
        // Merchandise total now excludes all voided item lines -> expect zero
        Assert.AreEqual(0m, after.Total.Amount, "Total should drop to zero after voiding parent + modifiers");
    }
}
