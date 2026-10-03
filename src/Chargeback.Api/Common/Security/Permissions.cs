namespace Chargeback.Api.Common.Security;

/// <summary>
/// Permission names (<c>permissions.name</c>). Permissions are read from the database via
/// role_permissions — never from Cognito groups (ADR-0003).
/// </summary>
public static class Permissions
{
    // ---- Seeded by docs/CHARGEBACK_DIAGRAM_BASELINE.sql ----------------------------------
    public const string ViewBanks = "VIEW_BANKS";
    public const string ViewBankUsers = "VIEW_BANK_USERS";
    public const string CreateBankUser = "CREATE_BANK_USER";
    public const string UpdateBankUser = "UPDATE_BANK_USER";
    public const string DisableBankUser = "DISABLE_BANK_USER";
    public const string ViewCases = "VIEW_CASES";
    public const string UpdateCaseStatus = "UPDATE_CASE_STATUS";
    public const string UploadDocument = "UPLOAD_DOCUMENT";
    public const string SubmitMastercom = "SUBMIT_MASTERCOM";
    public const string ViewReports = "VIEW_REPORTS";

    /// <summary>ADR-0111 approved: added to the baseline and migration 0002. Assign to roles only with product/security approval.</summary>
    public const string CreateDispute = "CREATE_DISPUTE";

    /// <summary>Phase 7 decision: added to the baseline and migration 0003. Not assigned to any role.</summary>
    public const string AssignCase = "ASSIGN_CASE";

    /// <summary>Guide v1.4 §3.1 (approved): defined in the baseline; assigned to roles by migration 0005.</summary>
    public const string ViewTriage = "VIEW_TRIAGE";

    /// <summary>Guide v1.4 §3.1 (approved): defined in the baseline; assigned to roles by migration 0005.</summary>
    public const string RetriageCase = "RETRIAGE_CASE";

    /// <summary>Human Review (approved 2026-10-03): defined in the baseline; assigned to all four roles by migration 0006.</summary>
    public const string ReviewCase = "REVIEW_CASE";

    /// <summary>Evidence &amp; Documents (approved 2026-10-03): defined in the baseline; assigned to all four roles by migration 0007.</summary>
    public const string ViewDocuments = "VIEW_DOCUMENTS";

    /// <summary>Admin &amp; Configuration (approved 2026-10-03): defined in the baseline; assigned to all four roles by migration 0008.</summary>
    public const string ManageBankUsers = "MANAGE_BANK_USERS";

    // ---- PROPOSED, NOT SEEDED (ADR-0111) -------------------------------------------------
    // Operations guarded by these are denied to every user until product/security approve the
    // names and seed them. Do not seed them from application code.
    public const string ViewFilings = "VIEW_FILINGS";
    public const string SendPortalMessage = "SEND_PORTAL_MESSAGE";
    public const string ManageBanks = "MANAGE_BANKS";
    public const string ManageRoles = "MANAGE_ROLES";
    public const string ManageBankScopes = "MANAGE_BANK_SCOPES";
    public const string ViewSchemeRules = "VIEW_SCHEME_RULES";
    public const string ManageSchemeRules = "MANAGE_SCHEME_RULES";

    public static readonly IReadOnlyList<string> Seeded =
    [
        ViewBanks, ViewBankUsers, CreateBankUser, UpdateBankUser, DisableBankUser,
        ViewCases, UpdateCaseStatus, UploadDocument, SubmitMastercom, ViewReports, CreateDispute, AssignCase,
        ViewTriage, RetriageCase, ReviewCase, ViewDocuments, ManageBankUsers,
    ];

    public static readonly IReadOnlyList<string> Proposed =
    [
        ViewFilings, SendPortalMessage,
        ManageBanks, ManageRoles, ManageBankScopes, ViewSchemeRules, ManageSchemeRules,
    ];
}
