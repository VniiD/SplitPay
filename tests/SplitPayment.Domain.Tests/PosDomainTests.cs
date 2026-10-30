using SplitPayment.Domain.Pos;

namespace SplitPayment.Domain.Tests;

public sealed class PosDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 15, 0, 0, TimeSpan.FromHours(-3));
    private static readonly FeeSchedule Fees = FeeSchedule.Demo;

    [Theory]
    [InlineData(10000, 3)]
    [InlineData(1, 3)]
    [InlineData(99999, 7)]
    public void Allocate_AlwaysSumsToTheOriginalAmount(long cents, int parts)
    {
        var shares = new Money(cents).AllocateEvenly(parts);

        Assert.Equal(cents, shares.Sum(share => share.Cents));
        Assert.True(shares.Max(share => share.Cents) - shares.Min(share => share.Cents) <= 1);
    }

    [Fact]
    public void Money_FormatsInBrazilianCurrency() => Assert.Equal("R$ 1.234,56", new Money(123456).ToString());

    [Fact]
    public void ApplyBasisPoints_RoundsHalfAwayFromZero() =>
        Assert.Equal(new Money(1), new Money(50).ApplyBasisPoints(100));

    [Fact]
    public void Split_Informative2026_DoesNotRetainTax()
    {
        var split = SplitEngine.Calculate(new SplitInput(new Money(100_00), PaymentMethod.Debit, 1, TaxMode.Informative2026), Fees);

        Assert.False(split.TaxRetainedAtSale);
        Assert.Equal(new Money(1_00), split.TaxInformational);
        Assert.Equal(Money.Zero, split.TaxRetained);
        Assert.Equal(new Money(1_99), split.TotalFee);
        Assert.Equal(new Money(98_01), split.MerchantNet);
    }

    [Fact]
    public void Split_SimulatedRetention_RoutesTaxAndRecipients()
    {
        var split = SplitEngine.Calculate(
            new SplitInput(new Money(1000_00), PaymentMethod.Credit, 1, TaxMode.SimulatedRetention, 1770, 880,
                [new SplitRecipient("Fornecedor X", "11222333000181", 1000)]),
            Fees);

        Assert.Equal(new Money(177_00), split.Lines.Single(line => line.Kind == SplitLineKind.TaxIbs).Amount);
        Assert.Equal(new Money(88_00), split.Lines.Single(line => line.Kind == SplitLineKind.TaxCbs).Amount);
        Assert.Equal(new Money(100_00), split.Lines.Single(line => line.Kind == SplitLineKind.Recipient).Amount);
        Assert.Equal(split.Gross, split.TotalDistributed);
    }

    [Fact]
    public void Split_Property_LinesAlwaysAddUpToGross()
    {
        var random = new Random(7);
        for (var iteration = 0; iteration < 500; iteration++)
        {
            var method = (PaymentMethod)random.Next(1, 6);
            var installments = method == PaymentMethod.Credit ? random.Next(1, 13) : 1;
            var gross = new Money(random.Next(6_000, 5_000_000));
            var split = SplitEngine.Calculate(
                new SplitInput(gross, method, installments, (TaxMode)random.Next(1, 3), 1770, 880), Fees);

            Assert.Equal(gross, split.TotalDistributed);
        }
    }

    [Fact]
    public void Split_RejectsDistributionAboveGross()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SplitEngine.Calculate(
            new SplitInput(new Money(100_00), PaymentMethod.Debit, 1, TaxMode.SimulatedRetention, 5000, 4000,
                [new SplitRecipient("Y", "", 2000)]), Fees));
    }

    [Theory]
    [InlineData(PaymentMethod.Debit, 3)]
    [InlineData(PaymentMethod.Pix, 2)]
    [InlineData(PaymentMethod.Credit, 13)]
    [InlineData(PaymentMethod.Credit, 0)]
    public void Installments_AreValidated(PaymentMethod method, int installments)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Fees.ValidateInstallments(method, installments, new Money(100_00)));
    }

    [Fact]
    public void Installments_RespectMinimumInstallmentAmount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Fees.ValidateInstallments(PaymentMethod.Credit, 12, new Money(30_00)));
    }

    [Fact]
    public void Receivables_AreCentExactAndSpacedEvery30Days()
    {
        var schedule = ReceivableScheduler.Build(PaymentMethod.Credit, 3, new Money(100_00), new Money(4_49), new DateOnly(2026, 10, 29));

        Assert.Equal(100_00, schedule.Sum(item => item.Gross.Cents));
        Assert.Equal(4_49, schedule.Sum(item => item.Fee.Cents));
        Assert.Equal(95_51, schedule.Sum(item => item.Net.Cents));
        Assert.Equal([new DateOnly(2026, 11, 28), new DateOnly(2026, 12, 28), new DateOnly(2027, 1, 27)], schedule.Select(item => item.DueDate));
    }

    [Fact]
    public void Receivables_DebitSettlesNextDayAndPixSameDay()
    {
        var day = new DateOnly(2026, 10, 29);

        Assert.Equal(day.AddDays(1), ReceivableScheduler.Build(PaymentMethod.Debit, 1, new Money(10_00), new Money(20), day)[0].DueDate);
        Assert.Equal(day, ReceivableScheduler.Build(PaymentMethod.Pix, 1, new Money(10_00), new Money(10), day)[0].DueDate);
    }

    [Fact]
    public void PixBrCode_HasValidCrcAndStandardFields()
    {
        var payload = PixBrCode.Build("5f2d9c3e-7a41-4c1e-8b0a-0d6e9b1c2a77", "Mercado Boa Vista", "São Paulo", new Money(12_50), "SP0001");

        Assert.True(PixBrCode.IsValid(payload));
        Assert.StartsWith("000201", payload, StringComparison.Ordinal);
        Assert.Contains("br.gov.bcb.pix", payload, StringComparison.Ordinal);
        Assert.Contains("540512.50", payload, StringComparison.Ordinal);
        Assert.Contains("5303986", payload, StringComparison.Ordinal);
        Assert.False(PixBrCode.IsValid(payload[..^1] + (payload[^1] == '0' ? "1" : "0")));
    }

    [Fact]
    public void Crc16_MatchesCcittFalseReferenceVector() => Assert.Equal(0x29B1, PixBrCode.Crc16("123456789"));

    [Fact]
    public void CardSale_ApprovedIsCapturedAndDeclinedIsNot()
    {
        var approved = NewSale(PaymentMethod.Credit, 1);
        approved.ApplyCardAuthorization(CardAuthorizer.NormalizeCard("visa", "4242", CardEntryMode.ChipAndPin, PaymentMethod.Credit), CardAuthorizer.Authorize(SimulatedOutcome.Approve, 1), Now);
        var declined = NewSale(PaymentMethod.Credit, 1);
        declined.ApplyCardAuthorization(CardAuthorizer.NormalizeCard("visa", "4242", CardEntryMode.ChipAndPin, PaymentMethod.Credit), CardAuthorizer.Authorize(SimulatedOutcome.InsufficientFunds, 2), Now);

        Assert.Equal(SaleStatus.Captured, approved.Status);
        Assert.Equal(6, approved.Authorization!.AuthorizationCode!.Length);
        Assert.Equal(SaleStatus.Declined, declined.Status);
        Assert.Equal("51", declined.Authorization!.ResponseCode);
        Assert.Throws<DomainRuleException>(() => declined.Refund(new Money(1), "x", Now));
    }

    [Theory]
    [InlineData("XXXX", "4242")]
    [InlineData("VISA", "42")]
    [InlineData("VISA", "4242424242424242")]
    public void Card_RejectsInvalidBrandOrFullPan(string brand, string last4)
    {
        Assert.Throws<DomainRuleException>(() => CardAuthorizer.NormalizeCard(brand, last4, CardEntryMode.ChipAndPin, PaymentMethod.Credit));
    }

    [Fact]
    public void PixSale_ExpiresAfterLifetime()
    {
        var sale = NewSale(PaymentMethod.Pix, 1);
        sale.AttachPix(new PixInfo("TX1", "payload", Now.AddMinutes(15), null, null));

        Assert.False(sale.ExpirePixIfDue(Now.AddMinutes(14)));
        Assert.True(sale.ExpirePixIfDue(Now.AddMinutes(16)));
        Assert.Equal(SaleStatus.Expired, sale.Status);
    }

    [Fact]
    public void Refund_TracksBalanceAndPreventsOverRefund()
    {
        var sale = CapturedCardSale(PaymentMethod.Debit);

        sale.Refund(new Money(30_00), "Item devolvido", Now);
        Assert.Equal(SaleStatus.PartiallyRefunded, sale.Status);
        Assert.Equal(new Money(70_00), sale.RefundableBalance);
        Assert.Throws<DomainRuleException>(() => sale.Refund(new Money(70_01), "x", Now));

        sale.Refund(new Money(70_00), "Resto", Now);
        Assert.Equal(SaleStatus.Refunded, sale.Status);
        Assert.Equal(Money.Zero, sale.MerchantNetAfterRefunds);
    }

    [Fact]
    public void Cancel_OnlySameDayAndNeverForPix()
    {
        var sale = CapturedCardSale(PaymentMethod.Credit);

        Assert.Throws<DomainRuleException>(() => sale.Cancel(Now.AddDays(1), "x", TimeSpan.FromHours(-3)));
        sale.Cancel(Now.AddHours(1), "Erro de digitação", TimeSpan.FromHours(-3));
        Assert.Equal(SaleStatus.Cancelled, sale.Status);
    }

    [Fact]
    public void Receipt_MasksCardAndHidesFeesFromCustomerCopy()
    {
        var sale = CapturedCardSale(PaymentMethod.Credit);
        var customer = ReceiptBuilder.Build(sale, DemoMerchants.Demo, DemoMerchants.DemoTerminal, ReceiptVia.Customer, TimeSpan.FromHours(-3));
        var merchant = ReceiptBuilder.Build(sale, DemoMerchants.Demo, DemoMerchants.DemoTerminal, ReceiptVia.Merchant, TimeSpan.FromHours(-3));
        var text = ReceiptBuilder.RenderText(customer);

        Assert.Contains("**** **** **** 4242", text, StringComparison.Ordinal);
        Assert.Contains("NSU: 000001", text, StringComparison.Ordinal);
        Assert.Contains("11.222.333/0001-81", text, StringComparison.Ordinal);
        Assert.Contains("VIA CLIENTE", text, StringComparison.Ordinal);
        Assert.Contains(customer.Notices, notice => notice.Contains("SEM VALOR FISCAL", StringComparison.Ordinal));
        Assert.DoesNotContain("MDR", text, StringComparison.Ordinal);
        Assert.Contains(merchant.Split, field => field.Value.Contains("R$", StringComparison.Ordinal));
        Assert.All(text.Split('\n'), line => Assert.True(line.TrimEnd('\r').Length <= 40));
    }

    [Fact]
    public void Receipt_MerchantCopyShowsNetAmountAfterSplitAndCustomerCopyDoesNot()
    {
        var sale = CapturedCardSale(PaymentMethod.Credit);
        var customer = ReceiptBuilder.Build(sale, DemoMerchants.Demo, DemoMerchants.DemoTerminal, ReceiptVia.Customer, TimeSpan.FromHours(-3));
        var merchant = ReceiptBuilder.Build(sale, DemoMerchants.Demo, DemoMerchants.DemoTerminal, ReceiptVia.Merchant, TimeSpan.FromHours(-3));
        var text = ReceiptBuilder.RenderText(merchant);

        Assert.Empty(customer.Settlement);
        Assert.Contains(merchant.Settlement, field => field.Label == "LÍQUIDO ESTABELECIMENTO" && field.Value == sale.Split.MerchantNet.ToString());
        Assert.Contains("VALOR A RECEBER", text, StringComparison.Ordinal);
        Assert.All(text.Split('\n'), line => Assert.True(line.TrimEnd('\r').Length <= 40));
    }

    [Fact]
    public void Receipt_DeclinedShowsNoChargeNotice()
    {
        var sale = NewSale(PaymentMethod.Debit, 1);
        sale.ApplyCardAuthorization(CardAuthorizer.NormalizeCard("elo", "1111", CardEntryMode.ChipAndPin, PaymentMethod.Debit), CardAuthorizer.Authorize(SimulatedOutcome.DoNotHonor, 9), Now);

        var receipt = ReceiptBuilder.Build(sale, DemoMerchants.Demo, DemoMerchants.DemoTerminal, ReceiptVia.Customer, TimeSpan.FromHours(-3));

        Assert.Equal("TRANSAÇÃO NEGADA", receipt.Title);
        Assert.Contains("Nenhum valor foi cobrado", string.Join(' ', receipt.Notices), StringComparison.Ordinal);
    }

    [Fact]
    public void Health_ReflectsRevenueRefundsReceivablesAndGoesRedWithoutRecentSales()
    {
        var healthy = Enumerable.Range(0, 8).Select(index => CapturedCardSale(PaymentMethod.Debit, Now.AddDays(-index))).ToList();
        var green = FinancialHealthCalculator.Calculate(healthy, Now, TimeSpan.FromHours(-3));

        Assert.Equal(HealthStatus.Green, green.Status);
        Assert.Equal(8, green.ApprovedCountMonth);
        Assert.Equal(new Money(100_00), green.GrossToday);

        healthy[0].Refund(new Money(100_00), "Estorno total", Now);
        Assert.Equal(new Money(0), FinancialHealthCalculator.Calculate(healthy, Now, TimeSpan.FromHours(-3)).GrossToday);

        var stale = FinancialHealthCalculator.Calculate(healthy, Now.AddDays(20), TimeSpan.FromHours(-3));
        Assert.NotEqual(HealthStatus.Green, stale.Status);
        Assert.Contains(stale.Alerts, alert => alert.Contains("Sem vendas", StringComparison.Ordinal));
    }

    private static Sale NewSale(PaymentMethod method, int installments, DateTimeOffset? at = null)
    {
        var now = at ?? Now;
        var split = SplitEngine.Calculate(new SplitInput(new Money(100_00), method, installments, TaxMode.Informative2026), Fees);
        var receivables = ReceivableScheduler.Build(method, installments, split.Gross, split.Gross - split.MerchantNet, DateOnly.FromDateTime(now.DateTime));
        return new Sale(Guid.NewGuid(), DemoMerchants.DemoMerchantId, "TERM-0001", method, installments, new Money(100_00), split, receivables, now, 1);
    }

    private static Sale CapturedCardSale(PaymentMethod method, DateTimeOffset? at = null)
    {
        var now = at ?? Now;
        var sale = NewSale(method, 1, now);
        sale.ApplyCardAuthorization(CardAuthorizer.NormalizeCard("visa", "4242", CardEntryMode.ChipAndPin, method), CardAuthorizer.Authorize(SimulatedOutcome.Approve, 1), now);
        return sale;
    }
}
