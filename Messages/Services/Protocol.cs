namespace Messages.Services;

/// <summary>
/// Works out whether a thread is currently RCS or SMS from its own history.
/// The backend's conversation label comes from whichever message it stored last, which during
/// history backfill is often the thread's oldest "created as SMS" event, so it can be stale.
/// </summary>
public static class Protocol
{
    public sealed record Result(string Label, bool Encrypted);

    /// <returns>"RCS" or "SMS" with encryption state, or null when the history gives no evidence.</returns>
    public static Result? Resolve(IEnumerable<ChatMessage> messages)
    {
        var encrypted = false;
        foreach (var message in messages.OrderByDescending(m => m.TimestampMs))
        {
            var status = message.Status.ToUpperInvariant();
            if (message.IsSystem)
            {
                if (status.Contains("ENCRYPTED")) encrypted = true;
                if (status.Contains("SWITCH_TO_TEXT") || status.Contains("SMS") || status.Contains("MMS")) return new Result("SMS", false);
                if (status.Contains("RCS")) return new Result("RCS", encrypted);
                continue;
            }
            // Read receipts only exist in RCS, so a newer one outranks an older switch to SMS.
            if (message.IsFromMe && (status.Contains("DISPLAYED") || status.Contains("READ"))) return new Result("RCS", encrypted);
        }
        return null;
    }

    /// <summary>Readable text for a tombstone that arrives without a body.</summary>
    public static string Describe(ChatMessage message)
    {
        if (message.Body.Trim().Length > 0) return message.Body.Trim();
        var status = message.Status.ToUpperInvariant();
        if (status.Contains("SWITCH_TO_TEXT")) return "Now texting with SMS/MMS";
        if (status.Contains("ENCRYPTED")) return "Chats are now end-to-end encrypted";
        if (status.Contains("SWITCH_TO_RCS")) return "Now chatting with RCS";
        if (status.Contains("RCS_GROUP_CREATED")) return "RCS group chat created";
        if (status.Contains("RCS_CREATED")) return "RCS chat started";
        if (status.Contains("SMS_CREATED")) return "Text conversation started";
        if (status.Contains("PARTICIPANT_JOINED")) return "Someone joined the conversation";
        if (status.Contains("PARTICIPANT_LEFT") || status.Contains("PARTICIPANT_REMOVED")) return "Someone left the conversation";
        var words = status.Replace("TOMBSTONE_", "").Replace('_', ' ').ToLowerInvariant();
        return words.Length > 0 ? char.ToUpperInvariant(words[0]) + words[1..] : "";
    }
}
