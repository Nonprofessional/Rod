using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;
using Rod.CoreState;
using Rod.CoreState.Pki;

namespace Rod.Transport.Endpoints;

/// <summary>
/// Reads an implant's <c>(implant_id, engagement_id)</c> identity off the
/// client certificate that terminated an mTLS connection (architecture.md Sec 9).
/// The certificate has already chained to the CA at the TLS layer; this is the
/// application-layer binding that complements it: the subject's common name is
/// the implant id and the Rod engagement extension carries the engagement id.
///
/// Returns null when no certificate is present or the binding is unreadable, so
/// the endpoint can map that to a handshake status rather than throwing across
/// the stream boundary.
/// </summary>
internal static class ClientCertificateIdentity
{
    /// <summary>
    /// The presenting certificate's identity, or null when none is present or the
    /// binding cannot be read. The implant id comes from the subject CN; the
    /// engagement id from the Rod engagement extension.
    /// </summary>
    public static ClientIdentity? Read(HttpContext context)
    {
        var cert = context.Connection.ClientCertificate;
        return cert is null ? null : Read(cert);
    }

    /// <summary>Same as <see cref="Read(HttpContext)"/> but from a certificate.</summary>
    public static ClientIdentity? Read(X509Certificate2 certificate)
    {
        // CN=<implant id>. A real implant leaf always has this; anything else is
        // not a Rod implant certificate.
        var cn = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        if (string.IsNullOrWhiteSpace(cn) || !ImplantId.TryParse(cn, out var implantId))
            return null;

        if (!RodImplantEngagementExtension.TryRead(certificate, out var engagementText)
            || !EngagementId.TryParse(engagementText, out var engagementId))
            return null;

        return new ClientIdentity(implantId, engagementId);
    }
}

/// <summary>An implant identity read off a client certificate.</summary>
internal sealed record ClientIdentity(ImplantId ImplantId, EngagementId EngagementId);
