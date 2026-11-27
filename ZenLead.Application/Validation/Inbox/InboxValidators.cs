using FluentValidation;
using ZenLead.Application.Dtos.Inbox;
using ZenLead.Domain.Enums;

namespace ZenLead.Application.Validation.Inbox;

public class ThreadListQueryValidator : AbstractValidator<ThreadListQuery>
{
    public ThreadListQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Q).MaximumLength(100);
        RuleFor(x => x.Classification).IsInEnum();
    }
}

public class ReplyRequestValidator : AbstractValidator<ReplyRequest>
{
    public const int MaxBodyLength = 10_000;

    public ReplyRequestValidator()
    {
        RuleFor(x => x.ClientRequestId).NotEmpty();
        RuleFor(x => x.Body)
            .Must(b => !string.IsNullOrWhiteSpace(b)).WithMessage("Write a reply first.")
            .Must(b => b is null || b.Trim().Length <= MaxBodyLength).WithMessage("A reply can be at most 10,000 characters.")
            .Must(b => b is null || !b.Contains('\0')).WithMessage("The reply contains an invalid character.");
    }
}

public class ReclassifyRequestValidator : AbstractValidator<ReclassifyRequest>
{
    /// <summary>A human can't set Unclassified (that means "no answer") or Bounce (only delivery headers prove a bounce).</summary>
    public static readonly ReplyClassification[] Allowed =
        [ReplyClassification.Interested, ReplyClassification.NotInterested, ReplyClassification.Unsubscribe, ReplyClassification.OutOfOffice];

    public ReclassifyRequestValidator()
    {
        RuleFor(x => x.Classification).Must(c => Allowed.Contains(c))
            .WithMessage("Classification must be Interested, NotInterested, Unsubscribe or OutOfOffice.");
    }
}
