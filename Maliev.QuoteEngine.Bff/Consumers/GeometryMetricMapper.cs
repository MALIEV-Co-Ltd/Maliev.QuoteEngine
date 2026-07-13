namespace Maliev.QuoteEngine.Bff.Consumers;

internal static class GeometryMetricMapper
{
    public static decimal? Positive(double? value) => Convert(value, requirePositive: true);

    public static decimal? NonNegative(double? value) => Convert(value, requirePositive: false);

    public static int? Positive(int? value) => value > 0 ? value : null;

    public static int PositiveOrDefault(int? value, int fallback = 1) => value > 0 ? value.Value : fallback;

    private static decimal? Convert(double? value, bool requirePositive)
    {
        if (value is not { } number ||
            double.IsNaN(number) ||
            double.IsInfinity(number) ||
            (requirePositive ? number <= 0 : number < 0))
        {
            return null;
        }

        try
        {
            return (decimal)number;
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
