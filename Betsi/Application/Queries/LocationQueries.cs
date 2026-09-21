namespace Betsi.Application.Queries;

using Betsi.Domain.Aggregates;
using Betsi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed record LocationChoice(Guid Id, string Name, int AvailableSpaces);

/// <summary>Read models used when assigning a patient to a treatment location.</summary>
public sealed class LocationQueries(BetsiDbContext context)
{
    public async Task<IReadOnlyList<LocationChoice>> GetAvailableAsync(CancellationToken cancellationToken) =>
        await context.Locations.AsNoTracking()
            .Where(location => location.State != Location.LocationState.Maintenance &&
                               location.CurrentOccupancy < location.Capacity)
            .OrderBy(location => location.Name)
            .Select(location => new LocationChoice(
                location.Id, location.Name, location.Capacity - location.CurrentOccupancy))
            .ToListAsync(cancellationToken);
}
