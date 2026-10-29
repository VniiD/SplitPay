namespace SplitPayment.Domain.Pos;

public enum HealthStatus
{
    Green = 1,
    Yellow = 2,
    Red = 3
}

public sealed record MethodMix(PaymentMethod Method, int Count, Money Gross, int ShareBasisPoints);

public sealed record ReceivableForecast(DateOnly Date, Money Net);

public sealed record FinancialHealth(
    DateOnly Today,
    HealthStatus Status,
    int Score,
    string Headline,
    IReadOnlyList<string> Alerts,
    bool InsufficientData,
    Money GrossToday,
    Money GrossMonth,
    Money NetMonth,
    Money FeesMonth,
    Money TaxRetainedMonth,
    Money TaxInformationalMonth,
    int ApprovedCountMonth,
    Money AverageTicket,
    Money RefundsMonth,
    int DeclineRateBasisPoints,
    int EffectiveCostBasisPoints,
    Money ReceivablesNext7Days,
    Money ReceivablesNext30Days,
    Money ReceivablesTotal,
    IReadOnlyList<MethodMix> Mix,
    IReadOnlyList<ReceivableForecast> Forecast);

public static class FinancialHealthCalculator
{
    private const int MinimumSalesForConfidence = 5;

    public static FinancialHealth Calculate(IEnumerable<Sale> sales, DateTimeOffset now, TimeSpan utcOffset)
    {
        var all = sales.ToList();
        var today = DateOnly.FromDateTime(now.ToOffset(utcOffset).DateTime);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        DateOnly Day(Sale sale) => DateOnly.FromDateTime((sale.CapturedAt ?? sale.CreatedAt).ToOffset(utcOffset).DateTime);

        var revenue = all.Where(sale => sale.CountsAsRevenue).ToList();
        var month = revenue.Where(sale => Day(sale) >= monthStart && Day(sale) <= today).ToList();
        var todaySales = month.Where(sale => Day(sale) == today).ToList();

        Money Sum(IEnumerable<Money> values) => values.Aggregate(Money.Zero, (total, value) => total + value);
        Money Kept(Sale sale) => sale.Gross - sale.RefundedTotal;

        var grossMonth = Sum(month.Select(Kept));
        var grossToday = Sum(todaySales.Select(Kept));
        var netMonth = Sum(month.Select(sale => sale.MerchantNetAfterRefunds));
        var feesMonth = Sum(month.Select(sale => Scale(sale.Split.TotalFee, sale)));
        var taxRetained = Sum(month.Select(sale => Scale(sale.Split.TaxRetained, sale)));
        var taxInformational = Sum(month.Select(sale => Scale(sale.Split.TaxInformational, sale)));
        var refundsMonth = Sum(month.Select(sale => sale.RefundedTotal));
        var ticket = month.Count == 0 ? Money.Zero : new Money(grossMonth.Cents / month.Count);

        var attempts = all.Where(sale => Day(sale) >= monthStart && sale.Status != SaleStatus.PendingPayment).ToList();
        var declineBps = attempts.Count == 0 ? 0 : (int)(attempts.Count(sale => sale.Status == SaleStatus.Declined) * 10_000L / attempts.Count);
        var costBps = grossMonth.Cents == 0 ? 0 : (int)(feesMonth.Cents * 10_000 / grossMonth.Cents);
        var refundBps = month.Sum(sale => sale.Gross.Cents) == 0 ? 0 : (int)(refundsMonth.Cents * 10_000 / month.Sum(sale => sale.Gross.Cents));

        var forecast = new SortedDictionary<DateOnly, long>();
        foreach (var sale in revenue.Where(sale => sale.Status != SaleStatus.Refunded))
        {
            var kept = Kept(sale).Cents;
            foreach (var receivable in sale.Receivables.Where(item => item.DueDate >= today))
            {
                var amount = sale.RefundedTotal.Cents == 0
                    ? receivable.Net.Cents
                    : receivable.Net.Cents * kept / sale.Gross.Cents;
                forecast[receivable.DueDate] = forecast.GetValueOrDefault(receivable.DueDate) + amount;
            }
        }

        var next7 = Sum(forecast.Where(item => item.Key <= today.AddDays(7)).Select(item => new Money(item.Value)));
        var next30 = Sum(forecast.Where(item => item.Key <= today.AddDays(30)).Select(item => new Money(item.Value)));
        var total = Sum(forecast.Select(item => new Money(item.Value)));

        var last7 = Sum(revenue.Where(sale => Day(sale) > today.AddDays(-7) && Day(sale) <= today).Select(Kept));
        var previous7 = Sum(revenue.Where(sale => Day(sale) > today.AddDays(-14) && Day(sale) <= today.AddDays(-7)).Select(Kept));

        var mix = month.GroupBy(sale => sale.Method)
            .Select(group =>
            {
                var gross = Sum(group.Select(Kept));
                return new MethodMix(group.Key, group.Count(), gross, grossMonth.Cents == 0 ? 0 : (int)(gross.Cents * 10_000 / grossMonth.Cents));
            })
            .OrderByDescending(item => item.Gross.Cents)
            .ToList();

        var score = 100;
        var alerts = new List<string>();
        void Penalize(int points, string alert)
        {
            score -= points;
            alerts.Add(alert);
        }

        if (refundBps > 500) Penalize(20, "Estornos acima de 5% do faturamento do mês.");
        else if (refundBps > 200) Penalize(10, "Estornos acima de 2% do faturamento do mês.");

        if (costBps > 450) Penalize(15, "Custo efetivo de taxas acima de 4,5%.");
        else if (costBps > 350) Penalize(5, "Custo efetivo de taxas acima de 3,5%.");

        if (declineBps > 2000) Penalize(15, "Mais de 20% das tentativas foram negadas.");
        else if (declineBps > 1000) Penalize(5, "Mais de 10% das tentativas foram negadas.");

        if (previous7.Cents > 0 && last7.Cents * 100 < previous7.Cents * 70) Penalize(20, "Faturamento dos últimos 7 dias caiu mais de 30%.");
        else if (previous7.Cents > 0 && last7.Cents * 100 < previous7.Cents * 90) Penalize(10, "Faturamento dos últimos 7 dias caiu mais de 10%.");

        if (total.Cents > 0 && (total.Cents - next30.Cents) * 100 > total.Cents * 60) Penalize(10, "Mais de 60% dos recebíveis vencem após 30 dias.");
        if (revenue.Count > 0 && last7.Cents == 0) Penalize(30, "Sem vendas nos últimos 7 dias.");

        score = Math.Clamp(score, 0, 100);
        var status = score >= 75 ? HealthStatus.Green : score >= 50 ? HealthStatus.Yellow : HealthStatus.Red;
        var insufficient = month.Count < MinimumSalesForConfidence;
        var headline = status switch
        {
            HealthStatus.Green => "Saúde financeira boa",
            HealthStatus.Yellow => "Atenção: acompanhe seus indicadores",
            _ => "Alerta: ação recomendada"
        };

        if (insufficient)
        {
            headline = month.Count == 0 ? "Sem vendas no mês ainda" : $"{headline} (poucos dados)";
            alerts.Add("Poucas vendas no mês: o indicador ainda tem baixa confiança.");
        }

        return new FinancialHealth(
            today, status, score, headline, alerts, insufficient,
            grossToday, grossMonth, netMonth, feesMonth, taxRetained, taxInformational,
            month.Count, ticket, refundsMonth, declineBps, costBps,
            next7, next30, total, mix,
            forecast.Select(item => new ReceivableForecast(item.Key, new Money(item.Value))).ToList());
    }

    private static Money Scale(Money value, Sale sale) =>
        sale.RefundedTotal.Cents == 0 ? value : new Money(value.Cents * (sale.Gross - sale.RefundedTotal).Cents / sale.Gross.Cents);
}
