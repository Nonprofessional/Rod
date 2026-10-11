using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Rod.CoreState.Campaigns;

namespace Rod.Transport.Campaigns;

/// <summary>
/// The SMTP arm of the send engine (architecture.md Sec 11.5): one
/// connection per message through the campaign's named relay, in the TLS
/// posture the campaign names. MailKit rather than
/// <c>System.Net.Mail.SmtpClient</c> -- Microsoft's own docs mark the BCL
/// client not-recommended, and the send path needs a maintained client
/// that speaks STARTTLS, implicit TLS, and AUTH correctly.
///
/// The sender holds nothing: no pooled connection (a relay per campaign,
/// credentials per campaign), no retry (delivery is single-attempt by
/// design -- the trail is the record), no queue. A failure throws with the
/// reason the engine records on the recipient row.
/// </summary>
public sealed class CampaignMailSender
{
    /// <summary>Sends one rendered message. Throws on any leg.</summary>
    public async Task SendAsync(
        Campaign campaign,
        string recipientEmail,
        string recipientName,
        string subject,
        string body,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(campaign.FromAddress));
        message.To.Add(string.IsNullOrWhiteSpace(recipientName)
            ? MailboxAddress.Parse(recipientEmail)
            : new MailboxAddress(recipientName, recipientEmail));
        message.Subject = subject;
        message.Body = campaign.BodyIsHtml
            ? new BodyBuilder { HtmlBody = body }.ToMessageBody()
            : new TextPart("plain") { Text = body };

        using var timeoutScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutScope.CancelAfter(timeout);

        // One connection per message: the relay is engagement infrastructure
        // the campaign names, not a server-level resource worth pooling, and
        // a fresh connection per send is one fewer thing a relay can observe
        // about the fleet's shape.
        using var client = new SmtpClient();
        await client.ConnectAsync(
            campaign.RelayHost,
            campaign.RelayPort,
            SecureSocketOptionsOf(campaign.RelayTls),
            timeoutScope.Token);
        if (!string.IsNullOrEmpty(campaign.RelayUsername))
        {
            await client.AuthenticateAsync(
                campaign.RelayUsername, campaign.RelayPassword ?? string.Empty, timeoutScope.Token);
        }
        await client.SendAsync(message, timeoutScope.Token);
        await client.DisconnectAsync(quit: true, timeoutScope.Token);
    }

    private static SecureSocketOptions SecureSocketOptionsOf(CampaignRelayTls tls) => tls switch
    {
        CampaignRelayTls.None => SecureSocketOptions.None,
        CampaignRelayTls.ImplicitTls => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.StartTlsWhenAvailable,
    };
}
