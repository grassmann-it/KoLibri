using System.Collections;
using System.Collections.ObjectModel;

namespace KolArbitrage.Core.Adventure;

/// <summary>
/// The outcome of evaluating one observed choice page. Manual is deliberately
/// the safe default: callers must opt into a concrete option through
/// <see cref="ChoicePolicy"/> before a choice can be emitted.
/// </summary>
public enum ChoiceDecisionKind
{
    Select = 0,
    Selected = Select,
    Manual = 1,
    ManualReview = Manual,
    ManualDefault = Manual
}

/// <summary>Why a choice was left for manual review.</summary>
public enum ChoiceDecisionReason
{
    None,
    MissingChoiceId,
    UnknownChoice,
    ManualOnly,
    NoAvailableOptions,
    NoAllowlistedOption,
    PreferredOptionUnavailable,
    HiddenFieldNotAllowlisted,
    HiddenFieldValueNotAllowed,
    RequiredHiddenFieldMissing,
    InvalidObservation
}

/// <summary>
/// A field that may be carried from a parsed choice form. The field name is
/// policy data; a page cannot introduce a new field by returning an arbitrary
/// hidden input. Empty <see cref="AllowedValues"/> means any non-empty value
/// is accepted for this explicitly named field (useful for KoL's short
/// per-page <c>pwd</c> token).
/// </summary>
public record ChoiceHiddenField
{
    public ChoiceHiddenField()
    {
    }

    public ChoiceHiddenField(
        string name,
        IEnumerable<string>? allowedValues = null,
        bool required = false)
    {
        Name = name?.Trim() ?? string.Empty;
        AllowedValues = NormalizeValues(allowedValues);
        Required = required;
    }

    public ChoiceHiddenField(
        string name,
        string? allowedValue,
        bool required = false)
        : this(name,
            string.IsNullOrWhiteSpace(allowedValue)
                ? Array.Empty<string>()
                : new[] { allowedValue },
            required)
    {
    }

    /// <summary>The exact form field name, for example <c>pwd</c>.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Compatibility alias for callers that call it a field name.</summary>
    public string FieldName
    {
        get => Name;
        init => Name = value?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Explicit values accepted for this field. An empty set allows any
    /// non-empty value, but only for this allowlisted name.
    /// </summary>
    public IReadOnlySet<string> AllowedValues { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    public bool Required { get; init; }

    /// <summary>Alias used by policies that describe the field as optional.</summary>
    public bool IsRequired
    {
        get => Required;
        init => Required = value;
    }

    /// <summary>Whether the observed value is allowed by this rule.</summary>
    public bool AllowsValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim();
        return AllowedValues.Count == 0 || AllowedValues.Contains(normalized);
    }

    internal ChoiceHiddenField Normalize()
        => this with
        {
            Name = Name?.Trim() ?? string.Empty,
            AllowedValues = NormalizeValues(AllowedValues)
        };

    private static IReadOnlySet<string> NormalizeValues(IEnumerable<string>? values)
        => new HashSet<string>(
            (values ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()),
            StringComparer.Ordinal);
}

/// <summary>A value copied from an observed hidden form field.</summary>
public sealed record ChoiceHiddenFieldValue
{
    public ChoiceHiddenFieldValue()
    {
    }

    public ChoiceHiddenFieldValue(string name, string value)
    {
        Name = name?.Trim() ?? string.Empty;
        Value = value?.Trim() ?? string.Empty;
    }

    public string Name { get; init; } = string.Empty;

    public string FieldName
    {
        get => Name;
        init => Name = value?.Trim() ?? string.Empty;
    }

    public string Value { get; init; } = string.Empty;
}

/// <summary>
/// A small read-only collection that can be consumed as typed values or as
/// form fields. The dictionary view is generated from already-filtered values;
/// callers cannot add arbitrary fields.
/// </summary>
public sealed class ChoiceHiddenFieldValues :
    IReadOnlyList<ChoiceHiddenFieldValue>,
    IReadOnlyDictionary<string, string>
{
    private readonly IReadOnlyList<ChoiceHiddenFieldValue> _values;
    private readonly IReadOnlyDictionary<string, string> _byName;

    public ChoiceHiddenFieldValues(IEnumerable<ChoiceHiddenFieldValue>? values = null)
    {
        var normalized = new List<ChoiceHiddenFieldValue>();
        foreach (var value in values ?? Array.Empty<ChoiceHiddenFieldValue>())
        {
            if (value is null
                || string.IsNullOrWhiteSpace(value.Name)
                || string.IsNullOrWhiteSpace(value.Value))
                continue;

            // A policy can list a name once. Keeping the first value makes
            // duplicate parser output deterministic and prevents a later
            // arbitrary field from replacing an approved value.
            var field = new ChoiceHiddenFieldValue(value.Name.Trim(), value.Value.Trim());
            if (normalized.Any(existing =>
                    string.Equals(existing.Name, field.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            normalized.Add(field);
        }

        _values = new ReadOnlyCollection<ChoiceHiddenFieldValue>(normalized);
        _byName = new ReadOnlyDictionary<string, string>(
            normalized.ToDictionary(value => value.Name, value => value.Value, StringComparer.OrdinalIgnoreCase));
    }

    public static ChoiceHiddenFieldValues Empty { get; } = new();

    public int Count => _values.Count;
    public ChoiceHiddenFieldValue this[int index] => _values[index];
    public string this[string key] => _byName[key];
    public IEnumerable<string> Keys => _byName.Keys;
    public IEnumerable<string> Values => _byName.Values;

    public bool ContainsKey(string key) => _byName.ContainsKey(key);
    public bool TryGetValue(string key, out string value) => _byName.TryGetValue(key, out value!);

    public IEnumerator<ChoiceHiddenFieldValue> GetEnumerator() => _values.GetEnumerator();
    IEnumerator<KeyValuePair<string, string>> IEnumerable<KeyValuePair<string, string>>.GetEnumerator()
        => _byName.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _values.GetEnumerator();
}

/// <summary>One option that a choice policy may select.</summary>
public record ChoiceOption
{
    public ChoiceOption()
    {
    }

    public ChoiceOption(string optionId, int priority = 0, bool isDefault = false)
    {
        OptionId = optionId?.Trim() ?? string.Empty;
        Priority = priority;
        IsDefault = isDefault;
    }

    public ChoiceOption(int optionId, int priority = 0, bool isDefault = false)
        : this(optionId.ToString(System.Globalization.CultureInfo.InvariantCulture), priority, isDefault)
    {
    }

    public string OptionId { get; init; } = string.Empty;

    public string Id
    {
        get => OptionId;
        init => OptionId = value?.Trim() ?? string.Empty;
    }

    public string? Label { get; init; }

    /// <summary>Lower values are preferred when no explicit default is set.</summary>
    public int Priority { get; init; }

    public bool IsDefault { get; init; }

    /// <summary>Marking an option manual keeps it visible but never selectable.</summary>
    public bool ManualOnly { get; init; }

    public bool RequiresManual
    {
        get => ManualOnly;
        init => ManualOnly = value;
    }

    public bool Manual
    {
        get => ManualOnly;
        init => ManualOnly = value;
    }

    /// <summary>Hidden fields specific to this option.</summary>
    public IReadOnlyList<ChoiceHiddenField> HiddenFields { get; init; }
        = Array.Empty<ChoiceHiddenField>();

    public IReadOnlyList<ChoiceHiddenField> AllowedHiddenFields
    {
        get => HiddenFields;
        init => HiddenFields = value ?? Array.Empty<ChoiceHiddenField>();
    }

    internal ChoiceOption Normalize()
        => this with
        {
            OptionId = OptionId?.Trim() ?? string.Empty,
            Label = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim(),
            HiddenFields = (HiddenFields ?? Array.Empty<ChoiceHiddenField>())
                .Where(field => field is not null)
                .Select(field => field.Normalize())
                .ToArray()
        };
}

/// <summary>Typed allowlist entry for one choice id.</summary>
public record ChoiceDefinition
{
    public ChoiceDefinition()
    {
    }

    public ChoiceDefinition(
        string choiceId,
        IEnumerable<ChoiceOption>? options = null,
        IEnumerable<ChoiceHiddenField>? hiddenFields = null,
        string? encounterId = null)
    {
        ChoiceId = choiceId?.Trim() ?? string.Empty;
        Options = (options ?? Array.Empty<ChoiceOption>()).ToArray();
        HiddenFields = (hiddenFields ?? Array.Empty<ChoiceHiddenField>()).ToArray();
        EncounterId = Normalize(encounterId);
    }

    public ChoiceDefinition(
        int choiceId,
        IEnumerable<ChoiceOption>? options = null,
        IEnumerable<ChoiceHiddenField>? hiddenFields = null,
        string? encounterId = null)
        : this(choiceId.ToString(System.Globalization.CultureInfo.InvariantCulture), options, hiddenFields, encounterId)
    {
    }

    public string ChoiceId { get; init; } = string.Empty;

    public string Id
    {
        get => ChoiceId;
        init => ChoiceId = value?.Trim() ?? string.Empty;
    }

    public string? EncounterId { get; init; }

    public IReadOnlyList<ChoiceOption> Options { get; init; }
        = Array.Empty<ChoiceOption>();

    public IReadOnlyList<ChoiceOption> AllowedOptions
    {
        get => Options;
        init => Options = value ?? Array.Empty<ChoiceOption>();
    }

    public IReadOnlyList<ChoiceHiddenField> HiddenFields { get; init; }
        = Array.Empty<ChoiceHiddenField>();

    public IReadOnlyList<ChoiceHiddenField> AllowedHiddenFields
    {
        get => HiddenFields;
        init => HiddenFields = value ?? Array.Empty<ChoiceHiddenField>();
    }

    /// <summary>
    /// Optional preferred option. If it is present on the page it wins over
    /// priorities; if it is absent the engine falls back to manual review.
    /// </summary>
    public string? DefaultOptionId { get; init; }

    public string? DefaultOption
    {
        get => DefaultOptionId;
        init => DefaultOptionId = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public bool ManualOnly { get; init; }

    public bool RequiresManual
    {
        get => ManualOnly;
        init => ManualOnly = value;
    }

    internal ChoiceDefinition Normalize()
        => this with
        {
            ChoiceId = ChoiceId?.Trim() ?? string.Empty,
            EncounterId = Normalize(EncounterId),
            DefaultOptionId = string.IsNullOrWhiteSpace(DefaultOptionId)
                ? null
                : DefaultOptionId.Trim(),
            Options = (Options ?? Array.Empty<ChoiceOption>())
                .Where(option => option is not null)
                .Select(option => option.Normalize())
                .ToArray(),
            HiddenFields = (HiddenFields ?? Array.Empty<ChoiceHiddenField>())
                .Where(field => field is not null)
                .Select(field => field.Normalize())
                .ToArray()
        };

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Compatibility alias for callers that call a definition a rule.</summary>
public record ChoiceRule : ChoiceDefinition
{
    public ChoiceRule()
    {
    }

    public ChoiceRule(
        string choiceId,
        IEnumerable<ChoiceOption>? options = null,
        IEnumerable<ChoiceHiddenField>? hiddenFields = null,
        string? encounterId = null)
        : base(choiceId, options, hiddenFields, encounterId)
    {
    }
}

/// <summary>Short compatibility alias for a typed choice definition.</summary>
public record Choice : ChoiceDefinition
{
    public Choice()
    {
    }

    public Choice(
        string choiceId,
        IEnumerable<ChoiceOption>? options = null,
        IEnumerable<ChoiceHiddenField>? hiddenFields = null,
        string? encounterId = null)
        : base(choiceId, options, hiddenFields, encounterId)
    {
    }
}

/// <summary>Compatibility alias for an option rule.</summary>
public record ChoiceOptionRule : ChoiceOption
{
    public ChoiceOptionRule()
    {
    }

    public ChoiceOptionRule(string optionId, int priority = 0, bool isDefault = false)
        : base(optionId, priority, isDefault)
    {
    }
}

/// <summary>Compatibility alias for a hidden-field rule.</summary>
public record ChoiceHiddenFieldRule : ChoiceHiddenField
{
    public ChoiceHiddenFieldRule()
    {
    }

    public ChoiceHiddenFieldRule(
        string name,
        IEnumerable<string>? allowedValues = null,
        bool required = false)
        : base(name, allowedValues, required)
    {
    }
}

/// <summary>Read-only input to <see cref="ChoiceEngine"/>.</summary>
public record ChoiceObservation
{
    public ChoiceObservation()
    {
    }

    public ChoiceObservation(
        string choiceId,
        IEnumerable<string>? availableOptions = null,
        IReadOnlyDictionary<string, string>? hiddenFields = null,
        string? encounterId = null)
    {
        ChoiceId = choiceId?.Trim() ?? string.Empty;
        AvailableOptions = NormalizeOptions(availableOptions);
        HiddenFields = CopyFields(hiddenFields);
        EncounterId = string.IsNullOrWhiteSpace(encounterId) ? null : encounterId.Trim();
    }

    public ChoiceObservation(
        int choiceId,
        IEnumerable<int>? availableOptions = null,
        IReadOnlyDictionary<string, string>? hiddenFields = null,
        string? encounterId = null)
        : this(
            choiceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (availableOptions ?? Array.Empty<int>())
                .Select(option => option.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            hiddenFields,
            encounterId)
    {
    }

    public string ChoiceId { get; init; } = string.Empty;

    public string? EncounterId { get; init; }

    public IReadOnlyList<string> AvailableOptions { get; init; }
        = Array.Empty<string>();

    public IReadOnlyList<string> Options
    {
        get => AvailableOptions;
        init => AvailableOptions = NormalizeOptions(value);
    }

    public IReadOnlyDictionary<string, string> HiddenFields { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> ExtraFields
    {
        get => HiddenFields;
        init => HiddenFields = value ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public ChoiceObservation Normalize()
        => this with
        {
            ChoiceId = ChoiceId?.Trim() ?? string.Empty,
            EncounterId = string.IsNullOrWhiteSpace(EncounterId) ? null : EncounterId.Trim(),
            AvailableOptions = NormalizeOptions(AvailableOptions),
            HiddenFields = CopyFields(HiddenFields)
        };

    private static IReadOnlyList<string> NormalizeOptions(IEnumerable<string>? values)
        => (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyDictionary<string, string> CopyFields(
        IEnumerable<KeyValuePair<string, string>>? values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values ?? Array.Empty<KeyValuePair<string, string>>())
        {
            if (string.IsNullOrWhiteSpace(pair.Key)) continue;
            var key = pair.Key.Trim();
            if (!result.ContainsKey(key)) result[key] = pair.Value ?? string.Empty;
        }
        return result;
    }

}

/// <summary>Alias for callers that use the page terminology.</summary>
public record ChoicePage : ChoiceObservation
{
    public ChoicePage()
    {
    }

    public ChoicePage(
        string choiceId,
        IEnumerable<string>? availableOptions = null,
        IReadOnlyDictionary<string, string>? hiddenFields = null,
        string? encounterId = null)
        : base(choiceId, availableOptions, hiddenFields, encounterId)
    {
    }
}

/// <summary>Short aliases for hosts that call a page an input or context.</summary>
public record ChoiceInput : ChoiceObservation
{
    public ChoiceInput()
    {
    }

    public ChoiceInput(
        string choiceId,
        IEnumerable<string>? availableOptions = null,
        IReadOnlyDictionary<string, string>? hiddenFields = null,
        string? encounterId = null)
        : base(choiceId, availableOptions, hiddenFields, encounterId)
    {
    }
}

public record ChoiceContext : ChoiceObservation
{
    public ChoiceContext()
    {
    }

    public ChoiceContext(
        string choiceId,
        IEnumerable<string>? availableOptions = null,
        IReadOnlyDictionary<string, string>? hiddenFields = null,
        string? encounterId = null)
        : base(choiceId, availableOptions, hiddenFields, encounterId)
    {
    }
}

/// <summary>
/// Declarative, transport-free choice policy. Definitions are copied and
/// normalized on construction so later mutation of caller-owned collections
/// cannot change a decision already being evaluated.
/// </summary>
public sealed record ChoicePolicy
{
    public ChoicePolicy()
    {
        Choices = Array.Empty<ChoiceDefinition>();
        HiddenFields = Array.Empty<ChoiceHiddenField>();
    }

    public ChoicePolicy(
        IEnumerable<ChoiceDefinition>? choices,
        IEnumerable<ChoiceHiddenField>? hiddenFields = null,
        bool manualByDefault = true)
    {
        Choices = (choices ?? Array.Empty<ChoiceDefinition>()).ToArray();
        HiddenFields = (hiddenFields ?? Array.Empty<ChoiceHiddenField>()).ToArray();
        ManualByDefault = manualByDefault;
    }

    public IReadOnlyList<ChoiceDefinition> Choices { get; init; }

    public IReadOnlyList<ChoiceDefinition> Definitions
    {
        get => Choices;
        init => Choices = value ?? Array.Empty<ChoiceDefinition>();
    }

    public IReadOnlyList<ChoiceDefinition> Entries
    {
        get => Choices;
        init => Choices = value ?? Array.Empty<ChoiceDefinition>();
    }

    /// <summary>Fields allowed for every definition unless overridden below.</summary>
    public IReadOnlyList<ChoiceHiddenField> HiddenFields { get; init; }

    public IReadOnlyList<ChoiceHiddenField> AllowedHiddenFields
    {
        get => HiddenFields;
        init => HiddenFields = value ?? Array.Empty<ChoiceHiddenField>();
    }

    /// <summary>Unknown or incomplete pages resolve to manual review by default.</summary>
    public bool ManualByDefault { get; init; } = true;

    public bool ManualDefault
    {
        get => ManualByDefault;
        init => ManualByDefault = value;
    }

    public static ChoicePolicy Empty { get; } = new();
    public static ChoicePolicy Default => Empty;

    public ChoicePolicy Normalize()
        => this with
        {
            Choices = (Choices ?? Array.Empty<ChoiceDefinition>())
                .Where(choice => choice is not null)
                .Select(choice => choice.Normalize())
                .ToArray(),
            HiddenFields = (HiddenFields ?? Array.Empty<ChoiceHiddenField>())
                .Where(field => field is not null)
                .Select(field => field.Normalize())
                .ToArray()
        };

    public void Validate()
    {
        var value = Normalize();
        ValidateHiddenFields(value.HiddenFields, "policy");

        var duplicateChoices = value.Choices
            .GroupBy(choice => (choice.ChoiceId, choice.EncounterId), StringTupleComparer.Instance)
            .FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key.ChoiceId) || group.Count() > 1);
        if (duplicateChoices is not null)
            throw new ArgumentException("Choice ids must be non-empty and unique per encounter.", nameof(Choices));

        foreach (var choice in value.Choices)
        {
            if (string.IsNullOrWhiteSpace(choice.ChoiceId))
                throw new ArgumentException("Choice ids must be non-empty.", nameof(Choices));

            var options = choice.Options ?? Array.Empty<ChoiceOption>();
            if (options.Any(option => string.IsNullOrWhiteSpace(option.OptionId)))
                throw new ArgumentException("Option ids must be non-empty.", nameof(Choices));
            if (options.GroupBy(option => option.OptionId, StringComparer.Ordinal).Any(group => group.Count() > 1))
                throw new ArgumentException("Option ids must be unique within a choice.", nameof(Choices));

            ValidateHiddenFields(choice.HiddenFields, $"choice '{choice.ChoiceId}'");
            foreach (var option in options)
                ValidateHiddenFields(option.HiddenFields, $"option '{choice.ChoiceId}/{option.OptionId}'");

            if (choice.DefaultOptionId is not null
                && !options.Any(option => string.Equals(option.OptionId, choice.DefaultOptionId, StringComparison.Ordinal)))
                throw new ArgumentException(
                    $"Default option '{choice.DefaultOptionId}' is not configured for choice '{choice.ChoiceId}'.",
                    nameof(Choices));
        }
    }

    public ChoicePolicy AddChoice(
        string choiceId,
        IEnumerable<ChoiceOption>? options = null,
        IEnumerable<ChoiceHiddenField>? hiddenFields = null,
        string? encounterId = null)
    {
        var normalized = Normalize();
        var id = Require(choiceId, nameof(choiceId));
        var list = normalized.Choices.ToList();
        list.RemoveAll(choice => string.Equals(choice.ChoiceId, id, StringComparison.Ordinal)
            && string.Equals(choice.EncounterId, NormalizeId(encounterId), StringComparison.Ordinal));
        list.Add(new ChoiceDefinition(id, options, hiddenFields, encounterId));
        return normalized with { Choices = list };
    }

    public ChoicePolicy AllowChoice(
        string choiceId,
        IEnumerable<ChoiceOption>? options = null,
        IEnumerable<ChoiceHiddenField>? hiddenFields = null,
        string? encounterId = null)
        => AddChoice(choiceId, options, hiddenFields, encounterId);

    public ChoicePolicy AddOption(
        string choiceId,
        string optionId,
        int priority = 0,
        bool isDefault = false,
        string? encounterId = null)
    {
        var normalized = Normalize();
        var current = normalized.Choices.FirstOrDefault(choice =>
            string.Equals(choice.ChoiceId, choiceId?.Trim(), StringComparison.Ordinal)
            && string.Equals(choice.EncounterId, NormalizeId(encounterId), StringComparison.Ordinal));
        var options = (current?.Options ?? Array.Empty<ChoiceOption>()).ToList();
        options.Add(new ChoiceOption(optionId, priority, isDefault));
        if (current is null)
            return normalized with
            {
                Choices = normalized.Choices.Append(new ChoiceDefinition(
                    choiceId,
                    options,
                    encounterId: encounterId)).ToArray()
            };

        var replacement = current with { Options = options };
        return normalized with
        {
            Choices = normalized.Choices.Select(choice => ReferenceEquals(choice, current) ? replacement : choice).ToArray()
        };
    }

    public ChoicePolicy AllowOption(
        string choiceId,
        string optionId,
        int priority = 0,
        bool isDefault = false,
        string? encounterId = null)
        => AddOption(choiceId, optionId, priority, isDefault, encounterId);

    public ChoicePolicy AddHiddenField(
        string choiceId,
        string fieldName,
        IEnumerable<string>? allowedValues = null,
        bool required = false,
        string? optionId = null,
        string? encounterId = null)
    {
        var normalized = Normalize();
        var current = normalized.Choices.FirstOrDefault(choice =>
            string.Equals(choice.ChoiceId, choiceId?.Trim(), StringComparison.Ordinal)
            && string.Equals(choice.EncounterId, NormalizeId(encounterId), StringComparison.Ordinal));
        var field = new ChoiceHiddenField(fieldName, allowedValues, required);
        if (current is null)
            return normalized with
            {
                Choices = normalized.Choices.Append(new ChoiceDefinition(
                    choiceId,
                    hiddenFields: new[] { field },
                    encounterId: encounterId)).ToArray()
            };

        var choices = normalized.Choices.ToArray();
        var index = Array.IndexOf(choices, current);
        if (string.IsNullOrWhiteSpace(optionId))
            choices[index] = current with { HiddenFields = current.HiddenFields.Append(field).ToArray() };
        else
        {
            var options = current.Options.ToArray();
            var optionIndex = Array.FindIndex(options, option => string.Equals(option.OptionId, optionId.Trim(), StringComparison.Ordinal));
            if (optionIndex < 0)
                throw new ArgumentException($"Option '{optionId}' is not configured for choice '{choiceId}'.", nameof(optionId));
            options[optionIndex] = options[optionIndex] with
            {
                HiddenFields = options[optionIndex].HiddenFields.Append(field).ToArray()
            };
            choices[index] = current with { Options = options };
        }
        return normalized with { Choices = choices };
    }

    public ChoicePolicy AllowHiddenField(
        string choiceId,
        string fieldName,
        IEnumerable<string>? allowedValues = null,
        bool required = false,
        string? optionId = null,
        string? encounterId = null)
        => AddHiddenField(choiceId, fieldName, allowedValues, required, optionId, encounterId);

    private static void ValidateHiddenFields(IEnumerable<ChoiceHiddenField>? fields, string scope)
    {
        var values = fields ?? Array.Empty<ChoiceHiddenField>();
        if (values.Any(field => string.IsNullOrWhiteSpace(field.Name)))
            throw new ArgumentException($"Hidden field names in {scope} must be non-empty.");
        if (values.Any(field => IsReserved(field.Name)))
            throw new ArgumentException($"Choice identity fields cannot be used as hidden fields in {scope}.");
        if (values.GroupBy(field => field.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new ArgumentException($"Hidden field names in {scope} must be unique.");
    }

    internal static bool IsReserved(string name)
        => name.Equals("whichchoice", StringComparison.OrdinalIgnoreCase)
            || name.Equals("option", StringComparison.OrdinalIgnoreCase)
            || name.Equals("action", StringComparison.OrdinalIgnoreCase);

    private static string Require(string? value, string parameter)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("An id is required.", parameter)
            : value.Trim();

    private static string? NormalizeId(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class StringTupleComparer : IEqualityComparer<(string ChoiceId, string? EncounterId)>
    {
        public static StringTupleComparer Instance { get; } = new();

        public bool Equals((string ChoiceId, string? EncounterId) x, (string ChoiceId, string? EncounterId) y)
            => string.Equals(x.ChoiceId, y.ChoiceId, StringComparison.Ordinal)
                && string.Equals(x.EncounterId, y.EncounterId, StringComparison.Ordinal);

        public int GetHashCode((string ChoiceId, string? EncounterId) obj)
            => HashCode.Combine(obj.ChoiceId, obj.EncounterId);
    }
}

/// <summary>Decision returned by the pure choice evaluator.</summary>
public sealed record ChoiceDecision
{
    public ChoiceDecisionKind Kind { get; init; }
    public ChoiceDecisionKind Outcome => Kind;
    public ChoiceDecisionReason ReasonCode { get; init; }
    public ChoiceDecisionReason ReasonKind => ReasonCode;
    public string? Reason { get; init; }
    public string? ChoiceId { get; init; }
    public ChoiceOption? SelectedOption { get; init; }
    public ChoiceOption? Selected => SelectedOption;
    public ChoiceOption? Option => SelectedOption;
    public string? OptionId => SelectedOption?.OptionId;
    public ChoiceHiddenFieldValues HiddenFieldValues { get; init; } = ChoiceHiddenFieldValues.Empty;
    public ChoiceHiddenFieldValues HiddenFields => HiddenFieldValues;
    public ChoiceHiddenFieldValues Fields => HiddenFieldValues;
    public ChoiceHiddenFieldValues ExtraFields => HiddenFieldValues;
    public bool IsManual => Kind == ChoiceDecisionKind.Manual;
    public bool IsSelected => Kind == ChoiceDecisionKind.Select;
    public bool HasSelection => IsSelected && SelectedOption is not null;

    public static ChoiceDecision Manual(
        string? choiceId,
        ChoiceDecisionReason reasonCode,
        string reason)
        => new()
        {
            Kind = ChoiceDecisionKind.Manual,
            ChoiceId = string.IsNullOrWhiteSpace(choiceId) ? null : choiceId.Trim(),
            ReasonCode = reasonCode,
            Reason = reason,
            HiddenFieldValues = ChoiceHiddenFieldValues.Empty
        };

    public static ChoiceDecision Select(
        string choiceId,
        ChoiceOption option,
        IEnumerable<ChoiceHiddenFieldValue>? fields = null)
        => new()
        {
            Kind = ChoiceDecisionKind.Select,
            ChoiceId = choiceId.Trim(),
            SelectedOption = option,
            ReasonCode = ChoiceDecisionReason.None,
            HiddenFieldValues = new ChoiceHiddenFieldValues(fields)
        };
}

/// <summary>
/// Deterministic choice evaluator. It only evaluates supplied observations; it
/// owns no HTTP client, URL, session, or transport implementation.
/// </summary>
public sealed class ChoiceEngine
{
    private readonly ChoicePolicy _policy;

    public ChoiceEngine(ChoicePolicy? policy = null)
    {
        _policy = (policy ?? ChoicePolicy.Empty).Normalize();
        _policy.Validate();
    }

    public ChoicePolicy Policy => _policy;

    public ChoiceDecision Decide(ChoiceObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return Evaluate(observation);
    }

    public ChoiceDecision Choose(ChoiceObservation observation)
        => Decide(observation);

    public ChoiceDecision Resolve(ChoiceObservation observation)
        => Decide(observation);

    public ChoiceDecision Evaluate(ChoiceObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var page = observation.Normalize();
        if (string.IsNullOrWhiteSpace(page.ChoiceId))
            return Manual(page.ChoiceId, ChoiceDecisionReason.MissingChoiceId,
                "The choice page has no choice id.");

        var definition = FindDefinition(page.ChoiceId, page.EncounterId);
        if (definition is null)
            return Manual(page.ChoiceId, ChoiceDecisionReason.UnknownChoice,
                $"Choice '{page.ChoiceId}' is not allowlisted.");
        if (definition.ManualOnly)
            return Manual(page.ChoiceId, ChoiceDecisionReason.ManualOnly,
                $"Choice '{page.ChoiceId}' is configured for manual review.");
        if (page.AvailableOptions.Count == 0)
            return Manual(page.ChoiceId, ChoiceDecisionReason.NoAvailableOptions,
                "The choice page has no available options.");

        var available = new HashSet<string>(page.AvailableOptions, StringComparer.Ordinal);
        var configured = (definition.Options ?? Array.Empty<ChoiceOption>())
            .Where(option => !option.ManualOnly && available.Contains(option.OptionId))
            .ToArray();
        if (configured.Length == 0)
            return Manual(page.ChoiceId, ChoiceDecisionReason.NoAllowlistedOption,
                "No available option is allowlisted for this choice.");

        ChoiceOption? selected;
        if (!string.IsNullOrWhiteSpace(definition.DefaultOptionId))
        {
            selected = configured.FirstOrDefault(option =>
                string.Equals(option.OptionId, definition.DefaultOptionId, StringComparison.Ordinal));
            if (selected is null)
                return Manual(page.ChoiceId, ChoiceDecisionReason.PreferredOptionUnavailable,
                    $"Preferred option '{definition.DefaultOptionId}' is not available on the page.");
        }
        else
        {
            selected = configured
                .OrderBy(option => option.IsDefault ? 0 : 1)
                .ThenBy(option => option.Priority)
                .ThenBy(option => option.OptionId, StringComparer.Ordinal)
                .First();
        }

        var fields = AllowedFields(definition, selected, page.HiddenFields, out var reason);
        if (reason is not null)
            return Manual(page.ChoiceId, reason.Value.Code, reason.Value.Message);
        return ChoiceDecision.Select(page.ChoiceId, selected, fields);
    }

    public ChoiceDecision Evaluate(AdventureKernelObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return Evaluate(new ChoiceObservation(
            observation.ChoiceId ?? string.Empty,
            observation.AvailableChoices,
            observation.ExtraFields,
            observation.EncounterId));
    }

    public ChoiceDecision Decide(
        string choiceId,
        IEnumerable<string>? availableOptions,
        IReadOnlyDictionary<string, string>? hiddenFields = null,
        string? encounterId = null)
        => Decide(new ChoiceObservation(choiceId, availableOptions, hiddenFields, encounterId));

    private ChoiceDefinition? FindDefinition(string choiceId, string? encounterId)
    {
        var exact = _policy.Choices.FirstOrDefault(choice =>
            string.Equals(choice.ChoiceId, choiceId, StringComparison.Ordinal)
            && string.Equals(choice.EncounterId, encounterId, StringComparison.Ordinal));
        if (exact is not null) return exact;
        return _policy.Choices.FirstOrDefault(choice =>
            string.Equals(choice.ChoiceId, choiceId, StringComparison.Ordinal)
            && choice.EncounterId is null);
    }

    private IReadOnlyList<ChoiceHiddenFieldValue> AllowedFields(
        ChoiceDefinition definition,
        ChoiceOption option,
        IReadOnlyDictionary<string, string> observed,
        out (ChoiceDecisionReason Code, string Message)? reason)
    {
        reason = null;
        var rules = new List<ChoiceHiddenField>();
        rules.AddRange(_policy.HiddenFields ?? Array.Empty<ChoiceHiddenField>());
        rules.AddRange(definition.HiddenFields ?? Array.Empty<ChoiceHiddenField>());
        rules.AddRange(option.HiddenFields ?? Array.Empty<ChoiceHiddenField>());

        var byName = new Dictionary<string, ChoiceHiddenField>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Name) || ChoicePolicy.IsReserved(rule.Name)) continue;
            if (!byName.ContainsKey(rule.Name)) byName[rule.Name] = rule;
        }

        var output = new List<ChoiceHiddenFieldValue>();
        foreach (var pair in observed ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(pair.Key)) continue;
            if (!byName.TryGetValue(pair.Key, out var rule))
            {
                // Unknown hidden fields are intentionally ignored. A caller
                // may still act on the approved option; no page-controlled
                // field is copied into a request.
                continue;
            }
            if (!rule.AllowsValue(pair.Value))
            {
                reason = (ChoiceDecisionReason.HiddenFieldValueNotAllowed,
                    $"Hidden field '{pair.Key}' has a value that is not allowed by policy.");
                return Array.Empty<ChoiceHiddenFieldValue>();
            }
            output.Add(new ChoiceHiddenFieldValue(rule.Name, pair.Value));
        }

        foreach (var rule in byName.Values)
        {
            var present = output.Any(value =>
                string.Equals(value.Name, rule.Name, StringComparison.OrdinalIgnoreCase));
            if (rule.Required && !present)
            {
                reason = (ChoiceDecisionReason.RequiredHiddenFieldMissing,
                    $"Required hidden field '{rule.Name}' is missing from the choice page.");
                return Array.Empty<ChoiceHiddenFieldValue>();
            }
        }

        return output;
    }

    private ChoiceDecision Manual(string? choiceId, ChoiceDecisionReason reasonCode, string message)
        // Manual is the only safe fallback, including when a caller chooses a
        // policy with ManualByDefault=false. The flag remains as metadata for
        // compatibility but never enables guessing.
        => ChoiceDecision.Manual(choiceId, reasonCode, message);
}

