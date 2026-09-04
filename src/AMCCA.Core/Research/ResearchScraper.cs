using System;
using AMCCA.Core.Database;
using AMCCA.Core.Security;

namespace AMCCA.Core.Research;

/// <summary>
/// Specialized research scraper component integrating SSRF transport enforcement (DEF-CERT-004, SPEC/24).
/// </summary>
public class ResearchScraper : ResearchService
{
    public ResearchScraper(DatabaseConnectionFactory connectionFactory, ISafeHttpClientFactory? clientFactory = null)
        : base(connectionFactory, clientFactory)
    {
    }
}
