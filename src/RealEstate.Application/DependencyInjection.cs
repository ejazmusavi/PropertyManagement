using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using RealEstate.Application.Auth;
using RealEstate.Application.Users;
using RealEstate.Application.Administration;
using RealEstate.Application.Common;
using RealEstate.Application.Companies;
using RealEstate.Application.Documents;
using RealEstate.Application.LandManagement;
using RealEstate.Application.Inventory;
using RealEstate.Application.Pricing;
using RealEstate.Application.Projects;
using RealEstate.Application.Customers;
using RealEstate.Application.Sales;
using RealEstate.Application.Installments;
using RealEstate.Application.Payments;
using RealEstate.Application.Recovery;
using RealEstate.Application.Expenses;
using RealEstate.Application.GovernmentFees;

namespace RealEstate.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<LoginRequestValidator>(includeInternalTypes: false);
        services.AddScoped<AuthService>();
        services.AddScoped<UserService>();
        services.AddScoped<ProjectAccessService>();
        services.AddScoped<DocumentUploader>();
        services.AddScoped<CompanyService>();
        services.AddScoped<ProjectService>();
        services.AddScoped<LandService>();
        services.AddScoped<PropertySetupService>();
        services.AddScoped<PropertyService>();
        services.AddScoped<InventoryReportService>();
        services.AddScoped<PricingSupport>();
        services.AddScoped<PricingRuleService>();
        services.AddScoped<PricingCalculationService>();
        services.AddScoped<PropertyPricingService>();
        services.AddScoped<PricingApprovalService>();
        services.AddScoped<CustomerService>();
        services.AddScoped<BookingService>();
        services.AddScoped<InstallmentSupport>();
        services.AddScoped<InstallmentSetupService>();
        services.AddScoped<InstallmentPlanService>();
        services.AddScoped<AgreementScheduleService>();
        services.AddScoped<AgreementAmendmentService>();
        services.AddScoped<PaymentSupport>();
        services.AddScoped<PaymentService>();
        services.AddScoped<ReceiptService>();
        services.AddScoped<LedgerService>();
        services.AddScoped<RefundService>();
        services.AddScoped<RecoveryService>();
        services.AddScoped<ExpenseSupport>();
        services.AddScoped<VendorService>();
        services.AddScoped<ExpenseService>();
        services.AddScoped<VendorInvoiceService>();
        services.AddScoped<GovernmentFeeService>();
        services.AddScoped<UserAdministrationService>();
        services.AddScoped<RoleService>();
        services.AddScoped<ProjectAccessAdminService>();
        return services;
    }
}
