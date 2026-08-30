namespace JET.Application.Contracts
{
    public sealed record BridgeResponse(string RequestId, bool Ok, object? Data, BridgeError? Error)
    {
        public static BridgeResponse Success(string requestId, object? data)
        {
            return new BridgeResponse(requestId, true, data, null);
        }

        public static BridgeResponse Failure(string requestId, string code, string message)
        {
            return new BridgeResponse(requestId, false, null, new BridgeError(code, message));
        }
    }

    public sealed record BridgeError(string Code, string Message);
}
