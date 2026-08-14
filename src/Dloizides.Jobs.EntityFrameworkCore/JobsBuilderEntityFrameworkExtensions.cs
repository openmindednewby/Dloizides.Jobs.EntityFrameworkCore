using Dloizides.Jobs.Abstractions;
using Dloizides.Jobs.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dloizides.Jobs.EntityFrameworkCore;

/// <summary>
/// The store selector for the EF Core backing: call inside the <c>AddDloizidesJobs</c> callback to back the
/// runtime with <see cref="EfJobStore{TContext}"/> over the app's own DbContext.
/// </summary>
public static class JobsBuilderEntityFrameworkExtensions
{
    /// <summary>
    /// Use the app's <typeparamref name="TContext"/> as the job store. The context must map
    /// <see cref="Dloizides.Jobs.Model.JobRun"/> — call
    /// <see cref="JobRunModelBuilderExtensions.ApplyJobRunConfiguration(ModelBuilder)"/> in its
    /// <c>OnModelCreating</c> and add a migration. The store is scoped, matching the DbContext lifetime.
    /// </summary>
    public static JobsBuilder UseEntityFrameworkStore<TContext>(this JobsBuilder builder)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddScoped<IJobStore, EfJobStore<TContext>>();
        return builder;
    }
}
