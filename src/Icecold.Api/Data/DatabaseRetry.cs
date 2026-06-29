using Npgsql;

namespace Icecold.Api.Data;

public static class DatabaseRetry
{
    public static bool IsSerializationFailure(Exception exception)
        => ContainsPostgresState(exception, PostgresErrorCodes.SerializationFailure);

    public static bool IsSerializationOrUniqueConflict(Exception exception)
        => ContainsPostgresState(exception, PostgresErrorCodes.SerializationFailure)
            || ContainsPostgresState(exception, PostgresErrorCodes.UniqueViolation);

    static bool ContainsPostgresState(Exception exception, string sqlState)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres && postgres.SqlState == sqlState)
                return true;
        }

        return false;
    }
}
