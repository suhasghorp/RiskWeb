namespace RiskWeb.Services.Chat;

public interface IChatSessionService
{
    ChatSession GetOrCreateSession(string userId);
    void ClearSession(string userId);
}

public class ChatSessionService : IChatSessionService
{
    private readonly Dictionary<string, ChatSession> _sessions = new();
    private readonly ILogger<ChatSessionService> _logger;
    private readonly object _lock = new();

    public ChatSessionService(ILogger<ChatSessionService> logger)
    {
        _logger = logger;
    }

    public ChatSession GetOrCreateSession(string userId)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(userId, out var session))
            {
                _logger.LogDebug("Retrieved existing session for user: {UserId}", userId);
                return session;
            }

            session = new ChatSession
            {
                UserId = userId
            };
            _sessions[userId] = session;

            _logger.LogInformation("Created new chat session for user: {UserId}, SessionId: {SessionId}",
                userId, session.SessionId);

            return session;
        }
    }

    public void ClearSession(string userId)
    {
        lock (_lock)
        {
            if (_sessions.Remove(userId))
            {
                _logger.LogInformation("Cleared chat session for user: {UserId}", userId);
            }
        }
    }
}
