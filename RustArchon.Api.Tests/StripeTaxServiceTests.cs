// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Billing;
using RustArchon.Api.Data;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="StripeTaxService"/>'s guard clause - the one case that never touches the real
/// Stripe API, so it needs no live key. The actual calculation path (a real Stripe Tax call) isn't
/// covered here for the same reason <see cref="StripeCheckoutServiceTests"/> doesn't cover a real
/// Checkout Session creation - see that class's own remarks.
/// </summary>
public class StripeTaxServiceTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    private static StripeTaxService CreateService(ApiDbContext context) => new(
        context, Mock.Of<IStripeCredentialProvider>(), NullLogger<StripeTaxService>.Instance);

    [Fact]
    public async Task ATenantWithNoBillingAddressOnFileGetsNoTaxCalculatedAtAll()
    {
        await using var context = CreateContext();

        var result = await CreateService(context)
            .CalculateAsync(Guid.NewGuid(), 15m, "USD", Guid.NewGuid().ToString());

        Assert.Null(result);
    }

    [Fact]
    public async Task ATenantWithABillingAddressButNoCountrySetGetsNoTaxCalculatedEither()
    {
        await using var context = CreateContext();
        var tenantId = Guid.NewGuid();

        // Shouldn't be reachable through OrganizationSettingsService (which clears the whole row when
        // Country is blank), but this guards the invariant directly rather than trusting that caller.
        context.Set<TenantBillingAddress>().Add(new TenantBillingAddress
        {
            TenantId = tenantId, City = "Austin", Country = string.Empty
        });
        await context.SaveChangesAsync();

        var result = await CreateService(context)
            .CalculateAsync(tenantId, 15m, "USD", Guid.NewGuid().ToString());

        Assert.Null(result);
    }
}
