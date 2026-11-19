using FluentValidation;

namespace RealEstate.Application.Common;

public static class RuleExtensions
{
    public static IRuleBuilderOptions<T, decimal> ValidArea<T>(this IRuleBuilder<T, decimal> rule) =>
        rule.GreaterThan(0).WithMessage("Area must be greater than zero.")
            .LessThanOrEqualTo(999_999_999m).WithMessage("Area is too large.")
            .Must(a => decimal.Round(a, 4) == a).WithMessage("Area supports at most 4 decimal places.");
}
