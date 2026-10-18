using Habita.Application.Abstractions;
using Habita.Application.Audit;
using Habita.Application.Access;
using Habita.Application.Charges;
using Habita.Application.Deposits;
using Habita.Application.Documents;
using Habita.Application.Imports;
using Habita.Application.Collections;
using Habita.Application.Files;
using Habita.Application.Leases;
using Habita.Application.Maintenance;
using Habita.Application.Notifications;
using Habita.Application.Members;
using Habita.Application.Payments;
using Habita.Application.Portal;
using Habita.Application.Reports;
using Habita.Application.Settings;
using Habita.Application.Properties;
using Habita.Application.Tenants;
using Habita.Infrastructure.Identity;
using Habita.Infrastructure.Persistence;
using Habita.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Habita.Infrastructure;

public static class DependencyInjection
{
    /// <param name="withIdentityServices">The API signs people in and needs UserManager and SignInManager. The background worker does not, and leaves them out.</param>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config, bool withIdentityServices = true)
    {
        services.AddScoped<ICurrentOrganization, CurrentOrganization>();
        services.AddScoped<TenantSaveChangesInterceptor>();
        services.AddDbContext<HabitaDbContext>((sp, o) => o
            .UseNpgsql(config.GetConnectionString("Default"))
            .AddInterceptors(sp.GetRequiredService<TenantSaveChangesInterceptor>()));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<HabitaDbContext>());
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IEmailSender, SmtpEmailSender>();
        services.AddScoped<MemberService>();
        services.AddScoped<PropertyService>();
        services.AddScoped<TenantService>();
        services.AddScoped<OrgCalendar>();
        services.AddScoped<LeaseService>();
        services.AddScoped<IChargeStore, ChargeStore>();
        services.AddScoped<IDbTransactions, DbTransactions>();
        services.AddScoped<ChargeService>();
        services.AddScoped<IFinanceLocks, FinanceLocks>();
        services.AddScoped<PaymentService>();
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<MaintenanceService>();
        services.AddScoped<ReportService>();
        services.AddScoped<AuditService>();
        services.AddScoped<SettingsService>();
        services.AddScoped<PortalService>();
        services.AddSingleton<IFileStore, FileSystemFileStore>();
        services.AddScoped<FileService>();
        services.AddScoped<AccessCodeService>();
        services.AddScoped<BankAccountService>();
        services.AddScoped<PaymentClaimService>();
        services.AddScoped<PaymentPromiseService>();
        services.AddScoped<CashCollectionService>();
        services.AddScoped<PortalCollectionService>();
        services.TryAddScoped<ICurrentUser, SystemCurrentUser>(); // the API replaces it with the signed-in person; the worker keeps this one
        services.AddScoped<INotificationStore, NotificationStore>();
        services.AddSingleton<IPushSender, WebPushSender>();
        services.AddSingleton<IWebLinks, WebLinks>();
        services.AddScoped<Notifier>();
        services.AddScoped<NotificationService>();
        services.AddScoped<NotificationDispatcher>();
        services.AddScoped<ReminderService>();
        services.AddSingleton<NotificationJobs>();
        services.AddSingleton<IContractTemplateEngine, OpenXmlTemplateEngine>();
        services.AddSingleton<IPdfConverter, GotenbergPdfConverter>();
        services.AddScoped<IReceiptFolios, ReceiptFolios>();
        services.AddScoped<ContractService>();
        services.AddScoped<ReceiptService>();
        services.AddScoped<StatementService>();
        services.AddScoped<InventoryService>();
        services.AddScoped<DepositService>();
        services.AddSingleton<ISpreadsheetReader, SpreadsheetReader>();
        services.AddScoped<ImportService>();

        if (!withIdentityServices) return services;
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 10;
                o.Lockout.MaxFailedAccessAttempts = 5;
            })
            .AddEntityFrameworkStores<HabitaDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();
        return services;
    }
}
