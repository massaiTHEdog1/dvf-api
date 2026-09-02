namespace DvfApi.Models;

/// <summary>
/// Comparison operators usable in a mutation filter.
/// Which operators are accepted on a given column depends on the declared type of the column
/// (see <see cref="Database.MutationsSchema.AllowedOperators"/>).
/// The request model carries this as a plain string; <see cref="Database.MutationsQuery"/>
/// parses it (case-insensitively) so that every validation failure produces the same
/// {"error": "..."} 400 response.
/// </summary>
public enum FilterOperator
{
    EQUALS,
    NOT_EQUALS,
    LIKE,
    GREATER_THAN,
    GREATER_THAN_OR_EQUALS,
    LOWER_THAN,
    LOWER_THAN_OR_EQUALS
}
