using System.Security.Cryptography;

namespace ScrapBook.Domain.Modules.Events;

public sealed record EventChange(EventConfiguration State, bool Changed, bool AccessChanged);

public static partial class EventRules
{
    public static string CreateCode() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static EventConfiguration Create(long creatorId, string? name, DateTime startsAt,
        string? welcomeMessage, string mode, DateOnly? openingDate, DateTime now)
    {
        var window = Window(startsAt);
        if (mode == "scheduled" && openingDate is null) openingDate = SuggestOpening(startsAt);
        var opensAt = ValidateOpening(mode, openingDate, window.ClosesAt, DateOnly.FromDateTime(now));
        var state = new EventConfiguration
        {
            CreatorId = creatorId, Code = CreateCode(), Name = NormalizeName(name),
            WelcomeMessage = NormalizeWelcome(welcomeMessage), StartsAt = startsAt,
            SubmissionOpensAt = window.OpensAt, SubmissionClosesAt = window.ClosesAt,
            CompletionDeadlineAt = window.DeadlineAt, OpeningMode = mode, OpensAt = opensAt
        };
        return Effective(state, now);
    }

    public static EventChange UpdateStructure(EventConfiguration current, string? name, DateTime startsAt,
        string? welcomeMessage, DateTime now)
    {
        if (current.LockedAt is not null) throw new EventRuleException(EventRuleFailure.StructureLocked);
        // Aplicar o encerramento ANTIGO antes de mover a janela.
        var effective = Effective(current, now);
        var window = Window(startsAt);
        if (effective.OpeningMode == "scheduled" &&
            (effective.OpensAt is null || effective.OpensAt <= window.ClosesAt))
            throw Invalid("openingDate", "A abertura atual é incompatível com a nova data. Ajuste a abertura antes de alterar as informações.");
        var updated = effective with
        {
            Name = NormalizeName(name), StartsAt = startsAt, WelcomeMessage = NormalizeWelcome(welcomeMessage),
            SubmissionOpensAt = window.OpensAt, SubmissionClosesAt = window.ClosesAt,
            CompletionDeadlineAt = window.DeadlineAt
        };
        return new(updated, updated != current, false);
    }

    public static EventChange UpdateAccess(EventConfiguration current, string? mode, DateOnly? openingDate, DateTime now)
    {
        if (mode is not ("immediate" or "scheduled")) throw Invalid("openingMode", "Selecione abertura imediata ou programada.");
        if (mode == "immediate" && openingDate is not null)
            throw Invalid("openingDate", "Abertura imediata não recebe uma data.");
        if (mode == "scheduled" && openingDate is null)
            throw Invalid("openingDate", "Informe a data de abertura.");
        var effective = Effective(current, now);
        var requested = openingDate?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var changed = mode != current.OpeningMode || requested != current.OpensAt;
        if (!changed) return new(effective, effective != current, false);
        if (effective.AccessState == "scheduled_locked"
            || (effective.AccessState == "immediate_once" && mode != "scheduled"))
            throw new EventRuleException(EventRuleFailure.OpeningLocked);
        var opensAt = ValidateOpening(mode, openingDate, effective.SubmissionClosesAt, DateOnly.FromDateTime(now));
        var updated = effective with
        {
            OpeningMode = mode, OpensAt = opensAt,
            AccessState = effective.AccessState == "immediate_once" ? "scheduled_locked" : effective.AccessState
        };
        // Versões pertencem aos triggers; apenas identificar a mudança de acesso.
        return new(updated, true, true);
    }
}
