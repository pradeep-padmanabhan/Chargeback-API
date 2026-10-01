using System.Text;
using Chargeback.Infrastructure.Persistence.Entities;
using Chargeback.SharedKernel.Entities;
using Chargeback.SharedKernel.Events;
using Chargeback.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Chargeback.Infrastructure.Persistence;

/// <summary>Maps entities to the baseline tables exactly. Mapping only; never DDL.</summary>
internal static class BaselineModel
{
    private static readonly ValueConverter<UserType, string> UserTypeConverter = new(
        v => v.ToString().ToUpperInvariant(),
        v => Enum.Parse<UserType>(v, true));

    public static void Configure(ModelBuilder model)
    {
        model.HasDefaultSchema(ChargebackDbContext.Schema);
        model.Ignore<DomainEvent>();

        model.Entity<Bank>().ToTable("banks");
        model.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.Property(x => x.RoleType).HasConversion(UserTypeConverter);
        });
        model.Entity<Permission>().ToTable("permissions");
        model.Entity<RolePermission>(e =>
        {
            e.ToTable("role_permissions");
            e.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId);
            e.HasOne<Permission>().WithMany().HasForeignKey(x => x.PermissionId);
        });
        model.Entity<User>(e =>
        {
            e.ToTable("users");
            e.Property(x => x.UserType).HasConversion(UserTypeConverter);
            e.HasOne<Bank>().WithMany().HasForeignKey(x => x.BankId);
            e.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId);
        });
        model.Entity<UserBankScope>(e =>
        {
            e.ToTable("user_bank_scopes");
            e.HasKey(x => new { x.UserId, x.BankId });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
            e.HasOne<Bank>().WithMany().HasForeignKey(x => x.BankId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.GrantedBy);
        });
        model.Entity<SchemeReasonCode>().ToTable("scheme_reason_codes");
        model.Entity<SchemeRuleSpec>(e =>
        {
            e.ToTable("scheme_rule_specs");
            e.Property(x => x.ConditionsJson).HasColumnType("jsonb");
            e.Property(x => x.RequiredDocs).HasColumnType("jsonb");
            e.HasOne<SchemeReasonCode>().WithMany().HasForeignKey(x => x.ReasonCodeId);
        });
        model.Entity<Dispute>(e =>
        {
            e.ToTable("disputes");
            e.Property(x => x.TransactionAmount).HasPrecision(18, 2);
            e.Property(x => x.CurrencyCode).HasColumnType("char(3)");
            e.HasOne<Bank>().WithMany().HasForeignKey(x => x.BankId);
        });
        model.Entity<GateResultRecord>(e =>
        {
            e.ToTable("gate_results");
            e.HasOne<Dispute>().WithMany().HasForeignKey(x => x.DisputeId);
        });
        model.Entity<Case>(e =>
        {
            e.ToTable("cases");
            e.Property(x => x.DerivedReasonCodeId).HasColumnName("derived_reason_code");
            e.HasOne<Dispute>().WithOne().HasForeignKey<Case>(x => x.DisputeId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.AssignedTo);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.HumanReviewedBy);
            e.HasOne<SchemeReasonCode>().WithMany().HasForeignKey(x => x.DerivedReasonCodeId);
        });
        model.Entity<TriageResultRecord>(e =>
        {
            e.ToTable("triage_results");
            e.Property(x => x.RiskScore).HasPrecision(8, 4);
            e.Property(x => x.RiskFlags).HasColumnType("jsonb");
            e.Property(x => x.Outcome).HasConversion<string>();
            e.HasOne<Case>().WithMany().HasForeignKey(x => x.CaseId);
        });
        model.Entity<DocumentSlot>(e =>
        {
            e.ToTable("document_slots");
            e.HasOne<Case>().WithMany().HasForeignKey(x => x.CaseId);
        });
        model.Entity<Document>(e =>
        {
            e.ToTable("documents");
            e.Property(x => x.AiConfidence).HasPrecision(5, 4);
            e.Property(x => x.SchemeStage).HasConversion<string>();
            e.Property(x => x.DocumentStatus).HasConversion<string>();
            e.HasOne<Case>().WithMany().HasForeignKey(x => x.CaseId);
            e.HasOne<DocumentSlot>().WithMany().HasForeignKey(x => x.DocumentSlotId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UploadedBy);
        });
        model.Entity<PortalMessage>(e =>
        {
            e.ToTable("portal_messages");
            e.HasOne<Case>().WithMany().HasForeignKey(x => x.CaseId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.SenderId);
        });
        model.Entity<ZendeskTicket>(e =>
        {
            e.ToTable("zendesk_tickets");
            e.HasOne<Case>().WithMany().HasForeignKey(x => x.CaseId);
        });
        model.Entity<ZendeskEvent>(e =>
        {
            e.ToTable("zendesk_events");
            e.Property(x => x.EventData).HasColumnType("jsonb");
            e.HasOne<ZendeskTicket>().WithMany().HasForeignKey(x => x.ZendeskTicketId);
        });
        model.Entity<MastercomFiling>(e =>
        {
            e.ToTable("mastercom_filings");
            e.HasOne<Case>().WithMany().HasForeignKey(x => x.CaseId);
        });
        model.Entity<FilingApiLogEntry>(e =>
        {
            e.ToTable("filing_api_log");
            e.Property(x => x.RequestPayload).HasColumnType("jsonb");
            e.Property(x => x.ResponsePayload).HasColumnType("jsonb");
            e.HasOne<MastercomFiling>().WithMany().HasForeignKey(x => x.FilingId);
        });
        model.Entity<AiDecisionLog>(e =>
        {
            e.ToTable("ai_decision_logs");
            e.Property(x => x.RawResponse).HasColumnType("jsonb");
            e.Property(x => x.ParsedOutput).HasColumnType("jsonb");
            e.HasOne<Case>().WithMany().HasForeignKey(x => x.CaseId);
        });
        model.Entity<DomainEventRecord>(e =>
        {
            e.ToTable("domain_events");
            e.Property(x => x.EventData).HasColumnType("jsonb");
            e.HasOne<Case>().WithMany().HasForeignKey(x => x.CaseId);
        });

        // Migration 0003.
        model.Entity<ProcessedDomainEvent>(e =>
        {
            e.ToTable("processed_domain_events");
            e.HasKey(x => x.EventId);
        });

        // Migration 0004 (ADR-0106).
        model.Entity<IdempotencyKeyRecord>(e =>
        {
            e.ToTable("idempotency_keys");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.RequestHash).HasColumnType("char(64)");
            e.Property(x => x.ResponseBody).HasColumnType("jsonb");
            e.HasIndex(x => new { x.PrincipalId, x.Operation, x.IdempotencyKey }).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.PrincipalId);
        });

        ApplyConventions(model);
    }

    private static void ApplyConventions(ModelBuilder model)
    {
        foreach (var entity in model.Model.GetEntityTypes())
        {
            var builder = model.Entity(entity.ClrType);

            if (typeof(IHasDomainEvents).IsAssignableFrom(entity.ClrType))
            {
                builder.Ignore(nameof(IHasDomainEvents.DomainEvents));
            }

            if (typeof(BaseEntity).IsAssignableFrom(entity.ClrType))
            {
                // Ids are assigned by the application (UUIDv7); the DB default is a fallback only.
                builder.Property(nameof(BaseEntity.Id)).ValueGeneratedNever();
            }

            if (typeof(AuditableEntity).IsAssignableFrom(entity.ClrType))
            {
                // PostgreSQL system column xmin as the optimistic concurrency token.
                builder.Property(nameof(AuditableEntity.Version))
                    .HasColumnName("xmin")
                    .HasColumnType("xid")
                    .ValueGeneratedOnAddOrUpdate()
                    .IsConcurrencyToken();
            }

            foreach (var property in entity.GetProperties())
            {
                if (property.GetColumnName() == property.Name)
                {
                    property.SetColumnName(ToSnakeCase(property.Name));
                }
            }
        }
    }

    internal static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
