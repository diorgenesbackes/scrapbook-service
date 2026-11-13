using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Enums;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;
using System.Globalization;
using System.Security.Cryptography;

namespace ScrapBook.Domain.Policies;

public static class EventPolicy
{
    public const string DefaultWelcomeMessage = "Seja bem-vindo! Compartilhe uma lembrança deste evento.";
    public const string DefaultCoverAssetKey = "event-cover-default-v1";

    public static string EffectiveWelcomeMessage(string? message) => message ?? DefaultWelcomeMessage;

    private static EventRuleException Invalid(string field, string message) =>
        new(EventRuleFailureEnum.Validation, field, message);

    private static string NormalizeText(string value, string field)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current == '\0') throw Invalid(field, "O texto contém caractere inválido.");
            if (!char.IsSurrogate(current)) continue;
            if (!char.IsHighSurrogate(current) || index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                throw Invalid(field, "O texto contém Unicode inválido.");
            index++;
        }

        var start = 0;
        var end = value.Length;
        static bool Space(char ch) => char.IsWhiteSpace(ch) || ch == '\uFEFF';
        while (start < end && Space(value[start])) start++;
        while (end > start && Space(value[end - 1])) end--;
        return value[start..end];
    }

    public static string NormalizeName(string? value)
    {
        var normalized = NormalizeText(value ?? "", "name");
        var count = normalized.EnumerateRunes().Count();
        if (count is < 1 or > 120) throw Invalid("name", "Informe um nome de 1 a 120 caracteres.");
        return normalized;
    }

    public static string? NormalizeWelcome(string? value)
    {
        if (value is null) return null;
        var normalized = NormalizeText(value, "welcomeMessage");
        if (normalized.EnumerateRunes().Count() > 2000)
            throw Invalid("welcomeMessage", "Use até 2.000 caracteres na mensagem.");
        return normalized.Length == 0 ? null : normalized;
    }

    public static DateTime ParseStartsAt(string? value)
    {
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date))
            throw Invalid("startsAt", "Informe data e hora de Brasília, sem fuso ou offset.");
        return DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
    }

    public static DateOnly? ParseOpeningDate(string? value)
    {
        if (value is null) return null;
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw Invalid("openingDate", "Informe uma data no formato AAAA-MM-DD.");
        return date;
    }

    public static (DateTime OpensAt, DateTime ClosesAt, DateTime DeadlineAt) Window(DateTime startsAt)
    {
        if (startsAt.Kind != DateTimeKind.Unspecified) throw Invalid("startsAt", "A data deve representar o horário civil de Brasília.");
        try
        {
            return (startsAt.AddHours(-2), startsAt.Date.AddDays(1).AddHours(12), startsAt.Date.AddDays(1).AddHours(13));
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Invalid("startsAt", "Data fora do intervalo suportado para a janela do evento.");
        }
    }

    public static DateOnly SuggestOpening(DateTime startsAt)
    {
        try { return DateOnly.FromDateTime(startsAt).AddYears(1); }
        catch (ArgumentOutOfRangeException) { throw Invalid("openingDate", "Não é possível sugerir abertura para esta data."); }
    }

    public static DateTime? ValidateOpening(string? mode, DateOnly? openingDate, DateTime closesAt, DateOnly today)
    {
        if (mode is not ("immediate" or "scheduled")) throw Invalid("openingMode", "Selecione abertura imediata ou programada.");
        if (mode == "immediate")
        {
            if (openingDate is not null) throw Invalid("openingDate", "Abertura imediata não recebe uma data.");
            return null;
        }
        if (openingDate is null) throw Invalid("openingDate", "Informe a data de abertura.");
        var instant = openingDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (openingDate <= today || instant <= closesAt)
            throw Invalid("openingDate", "A abertura deve ser posterior a hoje e ao encerramento dos envios.");
        return instant;
    }

    public static EventEntity Effective(EventEntity current, DateTime now)
    {
        if (current.AccessState != "flexible" || now < current.SubmissionClosesAt) return current;
        return current with
        {
            AccessState = current.OpeningMode == "scheduled" ? "scheduled_locked" : "immediate_once",
            AccessFrozenAt = current.AccessFrozenAt ?? current.SubmissionClosesAt
        };
    }

    public static string CreateCode() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static EventEntity Create(long creatorId, string? name, DateTime startsAt,
        string? welcomeMessage, string mode, DateOnly? openingDate, DateTime now)
    {
        var window = Window(startsAt);
        if (mode == "scheduled" && openingDate is null) openingDate = SuggestOpening(startsAt);
        var opensAt = ValidateOpening(mode, openingDate, window.ClosesAt, DateOnly.FromDateTime(now));
        var state = new EventEntity
        {
            CreatorId = creatorId,
            Code = CreateCode(),
            Name = NormalizeName(name),
            WelcomeMessage = NormalizeWelcome(welcomeMessage),
            StartsAt = startsAt,
            SubmissionOpensAt = window.OpensAt,
            SubmissionClosesAt = window.ClosesAt,
            CompletionDeadlineAt = window.DeadlineAt,
            OpeningMode = mode,
            OpensAt = opensAt
        };
        return Effective(state, now);
    }

    public static EventChangeModel UpdateStructure(EventEntity current, string? name, DateTime startsAt,
        string? welcomeMessage, DateTime now)
    {
        if (current.LockedAt is not null) throw new EventRuleException(EventRuleFailureEnum.StructureLocked);
        // Aplicar o encerramento ANTIGO antes de mover a janela.
        var effective = Effective(current, now);
        var window = Window(startsAt);
        if (effective.OpeningMode == "scheduled" &&
            (effective.OpensAt is null || effective.OpensAt <= window.ClosesAt))
            throw Invalid("openingDate", "A abertura atual é incompatível com a nova data. Ajuste a abertura antes de alterar as informações.");
        var updated = effective with
        {
            Name = NormalizeName(name),
            StartsAt = startsAt,
            WelcomeMessage = NormalizeWelcome(welcomeMessage),
            SubmissionOpensAt = window.OpensAt,
            SubmissionClosesAt = window.ClosesAt,
            CompletionDeadlineAt = window.DeadlineAt
        };
        return new(updated, updated != current, false);
    }

    public static EventChangeModel UpdateAccess(EventEntity current, string? mode, DateOnly? openingDate, DateTime now)
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
            throw new EventRuleException(EventRuleFailureEnum.OpeningLocked);
        var opensAt = ValidateOpening(mode, openingDate, effective.SubmissionClosesAt, DateOnly.FromDateTime(now));
        var updated = effective with
        {
            OpeningMode = mode,
            OpensAt = opensAt,
            AccessState = effective.AccessState == "immediate_once" ? "scheduled_locked" : effective.AccessState
        };
        // Versões pertencem aos triggers; apenas identificar a mudança de acesso.
        return new(updated, true, true);
    }
}
