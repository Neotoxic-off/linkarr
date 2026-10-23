using System.Security.Cryptography;
using System.Text;
using Linkarr.Api.Data;

namespace Linkarr.Api.Auth;

// A session as its owner or an admin sees it. The stored id is the cookie's secret, so the UI gets a hash of it instead.
public static class SessionDtos
{
    public static string PublicId(string sessionId) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)))[..16];

    public static object Dto(Session session, string? currentSessionId)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new
        {
            id = PublicId(session.Id),
            session.Created,
            session.LastSeen,
            ip = session.IpAddress,
            session.LastIp,
            session.UserAgent,
            current = session.Id == currentSessionId,
        };
    }
}
