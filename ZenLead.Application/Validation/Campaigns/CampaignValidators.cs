using System.Globalization;
using FluentValidation;
using ZenLead.Application.Dtos.Campaigns;
using ZenLead.Application.Validation.Leads;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Scheduling;
using ZenLead.Domain.Templates;

namespace ZenLead.Application.Validation.Campaigns;

public static class CampaignTimes
{
    public const string Format = "HH:mm";

    public static bool TryParse(string? value, out TimeOnly time)
        => TimeOnly.TryParseExact(value, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public static string ToText(TimeOnly time) => time.ToString(Format, CultureInfo.InvariantCulture);
}

public class CampaignRequestValidator : AbstractValidator<CampaignRequest>
{
    public CampaignRequestValidator()
    {
        RuleFor(x => (x.Name ?? string.Empty).Trim()).NotEmpty().WithMessage("Campaign name is required.").MaximumLength(200)
            .OverridePropertyName(nameof(CampaignRequest.Name));
        RuleFor(x => x.FromName).MaximumLength(100)
            .Must(n => n is null || !n.Any(char.IsControl)).WithMessage("From name must be a single line.");
        RuleFor(x => x.DailySendCap).InclusiveBetween(1, 500);
        RuleFor(x => x.WindowStart).Must(t => CampaignTimes.TryParse(t, out _)).WithMessage("Window start must be HH:mm.");
        RuleFor(x => x.WindowEnd).Must(t => CampaignTimes.TryParse(t, out _)).WithMessage("Window end must be HH:mm.");
        RuleFor(x => x)
            .Must(x => CampaignTimes.TryParse(x.WindowStart, out var s) && CampaignTimes.TryParse(x.WindowEnd, out var e) && s < e)
            .WithMessage("The send window must start before it ends.").OverridePropertyName(nameof(CampaignRequest.WindowEnd))
            .When(x => CampaignTimes.TryParse(x.WindowStart, out _) && CampaignTimes.TryParse(x.WindowEnd, out _));
        RuleFor(x => x.SendDays).Must(d => d != SendDays.None && (d & ~SendDays.All) == 0).WithMessage("Pick at least one sending day.");
        RuleFor(x => x.TimeZoneId).Must(tz => TimeZones.TryFind(tz, out _)).WithMessage("Unknown time zone.")
            .When(x => x.TimeZoneId is not null);
    }
}

public class StepRequestValidator : AbstractValidator<StepRequest>
{
    public StepRequestValidator()
    {
        RuleFor(x => x.DelayDays).InclusiveBetween(0, 90);
        RuleFor(x => x.SubjectTemplate).NotEmpty().MaximumLength(300)
            .Must(s => s is null || !s.Contains('\r') && !s.Contains('\n')).WithMessage("The subject must be a single line.");
        RuleFor(x => x.BodyTemplate).NotEmpty().MaximumLength(10_000);
        RuleFor(x => x.SubjectTemplate).Must(NoUnknownTokens).WithMessage(x => UnknownTokenMessage(x.SubjectTemplate));
        RuleFor(x => x.BodyTemplate).Must(NoUnknownTokens).WithMessage(x => UnknownTokenMessage(x.BodyTemplate));
    }

    private static bool NoUnknownTokens(string? template) => template is null || TemplateRenderer.UnknownTokens(template).Count == 0;

    private static string UnknownTokenMessage(string? template)
        => $"Unknown token(s): {string.Join(", ", TemplateRenderer.UnknownTokens(template ?? string.Empty).Select(t => "{{" + t + "}}"))}. " +
           $"Known: {string.Join(", ", TemplateRenderer.KnownTokens.Select(t => "{{" + t + "}}"))}.";
}

public class EnrollRequestValidator : AbstractValidator<EnrollRequest>
{
    public const int MaxLeadIds = 5000;

    public EnrollRequestValidator()
    {
        RuleFor(x => x).Must(x => (x.LeadIds is null) != (x.Filter is null))
            .WithMessage("Send either leadIds or filter, not both.").OverridePropertyName(nameof(EnrollRequest.LeadIds));
        RuleFor(x => x.LeadIds!.Count).InclusiveBetween(1, MaxLeadIds).OverridePropertyName(nameof(EnrollRequest.LeadIds))
            .When(x => x.LeadIds is not null);
        RuleFor(x => x.Filter!).SetValidator(new LeadQueryValidator()).When(x => x.Filter is not null);
    }
}

public class ReorderStepsRequestValidator : AbstractValidator<ReorderStepsRequest>
{
    public ReorderStepsRequestValidator()
    {
        RuleFor(x => x.OrderedStepIds).NotEmpty()
            .Must(ids => ids.Distinct().Count() == ids.Count).WithMessage("Each step may appear only once.");
    }
}
