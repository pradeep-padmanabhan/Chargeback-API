using Chargeback.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Chargeback.Infrastructure.Persistence;

/// <summary>
/// EF Core context over the approved baseline schema <c>chargeback_diagram</c>.
/// The schema is owned by docs/CHARGEBACK_DIAGRAM_BASELINE.sql: this context never creates,
/// migrates or alters it (ADR-0004). Use it for commands; use <see cref="Queries.IDapperQueryService"/> for reads.
/// </summary>
public sealed class ChargebackDbContext(DbContextOptions<ChargebackDbContext> options) : DbContext(options)
{
    public const string Schema = "chargeback_diagram";

    public DbSet<Bank> Banks => Set<Bank>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<Permission> Permissions => Set<Permission>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<User> Users => Set<User>();

    public DbSet<UserBankScope> UserBankScopes => Set<UserBankScope>();

    public DbSet<SchemeReasonCode> SchemeReasonCodes => Set<SchemeReasonCode>();

    public DbSet<SchemeRuleSpec> SchemeRuleSpecs => Set<SchemeRuleSpec>();

    public DbSet<Dispute> Disputes => Set<Dispute>();

    public DbSet<GateResultRecord> GateResults => Set<GateResultRecord>();

    public DbSet<Case> Cases => Set<Case>();

    public DbSet<TriageResultRecord> TriageResults => Set<TriageResultRecord>();

    public DbSet<DocumentSlot> DocumentSlots => Set<DocumentSlot>();

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<PortalMessage> PortalMessages => Set<PortalMessage>();

    public DbSet<ZendeskTicket> ZendeskTickets => Set<ZendeskTicket>();

    public DbSet<ZendeskEvent> ZendeskEvents => Set<ZendeskEvent>();

    public DbSet<MastercomFiling> MastercomFilings => Set<MastercomFiling>();

    public DbSet<FilingApiLogEntry> FilingApiLog => Set<FilingApiLogEntry>();

    public DbSet<AiDecisionLog> AiDecisionLogs => Set<AiDecisionLog>();

    public DbSet<DomainEventRecord> DomainEvents => Set<DomainEventRecord>();

    public DbSet<ProcessedDomainEvent> ProcessedDomainEvents => Set<ProcessedDomainEvent>();

    public DbSet<IdempotencyKeyRecord> IdempotencyKeys => Set<IdempotencyKeyRecord>();

    public DbSet<CaseReviewDecision> CaseReviewDecisions => Set<CaseReviewDecision>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => BaselineModel.Configure(modelBuilder);
}
