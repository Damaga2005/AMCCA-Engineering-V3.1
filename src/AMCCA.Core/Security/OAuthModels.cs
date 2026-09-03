using System;
using System.Collections.Generic;

namespace AMCCA.Core.Security;

public record OAuthTokenBundle(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAt,
    string TokenType = "Bearer",
    IReadOnlyList<string>? Scopes = null
);

public record OAuthAuthorizationRequest(
    string AuthorizationUrl,
    string State,
    string CodeVerifier,
    string RedirectUri
);

public record OAuthCallbackResult(
    bool Success,
    string? AuthorizationCode,
    string? State,
    string? Error
);
