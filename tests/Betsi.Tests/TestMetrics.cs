namespace Betsi.Tests;

using Betsi.Infrastructure.Observability;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.Metrics;

/// <summary>
/// A real <see cref="BetsiMetrics"/> for tests that construct a component directly.
/// </summary>
/// <remarks>
/// The real one rather than a stub: recording to a meter nobody listens to costs nothing, and
/// a stub would let a metric call that throws on a null tag pass here and fail in production.
/// </remarks>
internal static class TestMetrics
{
    public static BetsiMetrics Instance { get; } = new(
        new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>());
}
