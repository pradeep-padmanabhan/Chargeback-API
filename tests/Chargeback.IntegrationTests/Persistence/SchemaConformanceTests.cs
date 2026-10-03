using Chargeback.Api.Common.Security;
using Chargeback.Infrastructure.Persistence;
using Chargeback.Infrastructure.Persistence.Entities;
using Chargeback.IntegrationTests.Infrastructure;
using Chargeback.SharedKernel.Security;
using Chargeback.SharedKernel.ValueObjects;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Chargeback.IntegrationTests.Persistence;

/// <summary>The EF mapping must match the approved baseline exactly — no invented tables or columns.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SchemaConformanceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Every_baseline_table_and_column_is_mapped_exactly()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChargebackDbContext>();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        var columns = (await connection.QueryAsync<(string Table, string Column)>(
                "SELECT table_name, column_name FROM information_schema.columns WHERE table_schema = 'chargeback_diagram'"))
            .GroupBy(c => c.Table)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Column).Order().ToArray());

        var mapped = db.Model.GetEntityTypes().ToDictionary(
            e => e.GetTableName()!,
            e => e.GetProperties().Select(p => p.GetColumnName()).Where(c => c != "xmin").Order().ToArray());

        mapped.Keys.Should().BeEquivalentTo(columns.Keys, "every baseline table is mapped and nothing else");
        mapped.Should().HaveCount(24, "21 baseline tables + processed_domain_events (0003) + idempotency_keys (0004) + case_review_decisions (0006)");
        foreach (var (table, databaseColumns) in columns)
        {
            mapped[table].Should().Equal(databaseColumns, $"columns of {table} must match the baseline");
        }
    }

    [Fact]
    public async Task Baseline_seeds_exactly_the_documented_permissions()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        var names = await connection.QueryAsync<string>(
            "SELECT name FROM chargeback_diagram.permissions WHERE resource <> 'TEST' ORDER BY name");

        names.Should().BeEquivalentTo(Permissions.Seeded);
    }

    [Fact]
    public async Task Entities_round_trip_through_converters()
    {
        var bankId = await fixture.Data.CreateBankAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChargebackDbContext>();

        var role = new Role { Name = "rt-" + Guid.NewGuid().ToString("N"), RoleType = UserType.Processor };
        var user = new User { CognitoSub = Guid.NewGuid().ToString(), Email = "rt@example.test", FullName = "RT", UserType = UserType.Processor, RoleId = role.Id };
        var dispute = new Dispute { BankId = bankId, IntakeChannel = "API", CurrencyCode = "GBP", TransactionAmount = 12.34m, CardNumberMasked = "************4242" };
        var @case = new Case { DisputeId = dispute.Id, CaseReference = "RT-" + Guid.NewGuid().ToString("N"), FilingDeadlineDate = new DateOnly(2026, 12, 31) };
        var triage = new TriageResultRecord { CaseId = @case.Id, Outcome = TriageOutcome.RouteToHuman, RiskFlags = """["HIGH_VALUE"]""", RiskScore = 0.1234m };
        var document = new Document { CaseId = @case.Id, FileName = "a.pdf", S3Key = "k/" + Guid.NewGuid(), SchemeStage = DocumentStage.PreArbitration, DocumentStatus = DocumentStatus.Processing };
        db.AddRange(role, user, dispute, @case, triage, document);
        db.UserBankScopes.Add(new UserBankScope { UserId = user.Id, BankId = bankId, ValidFrom = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        (await fixture.Data.QueryScalarAsync<string>("SELECT user_type FROM chargeback_diagram.users WHERE id = @Id", new { user.Id })).Should().Be("PROCESSOR");
        (await fixture.Data.QueryScalarAsync<string>("SELECT role_type FROM chargeback_diagram.roles WHERE id = @Id", new { role.Id })).Should().Be("PROCESSOR");
        (await fixture.Data.QueryScalarAsync<string>("SELECT scheme_stage FROM chargeback_diagram.documents WHERE id = @Id", new { document.Id })).Should().Be("PreArbitration");
        (await fixture.Data.QueryScalarAsync<string>("SELECT outcome FROM chargeback_diagram.triage_results WHERE id = @Id", new { triage.Id })).Should().Be("RouteToHuman");

        using var readScope = fixture.Factory.Services.CreateScope();
        var read = readScope.ServiceProvider.GetRequiredService<ChargebackDbContext>();
        var readCase = await read.Cases.SingleAsync(c => c.Id == @case.Id);
        readCase.FilingDeadlineDate.Should().Be(new DateOnly(2026, 12, 31));
        readCase.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        readCase.Version.Should().NotBe(0u);
        (await read.Documents.SingleAsync(d => d.Id == document.Id)).DocumentStatus.Should().Be(DocumentStatus.Processing);
        (await read.Disputes.SingleAsync(d => d.Id == dispute.Id)).CurrencyCode.Should().Be("GBP");
    }

    [Fact]
    public async Task Concurrent_updates_are_detected_with_xmin()
    {
        var bankId = await fixture.Data.CreateBankAsync();
        using var scopeA = fixture.Factory.Services.CreateScope();
        using var scopeB = fixture.Factory.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<ChargebackDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<ChargebackDbContext>();

        var a = await dbA.Banks.SingleAsync(b => b.Id == bankId);
        var b = await dbB.Banks.SingleAsync(x => x.Id == bankId);
        a.BankName = "first";
        await dbA.SaveChangesAsync();
        b.BankName = "second";

        var act = () => dbB.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }
}
