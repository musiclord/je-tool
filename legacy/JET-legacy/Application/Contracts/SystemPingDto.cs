namespace JET.Application.Contracts
{
    public sealed record SystemPingDto(string Message, DateTimeOffset UtcNow);
}
