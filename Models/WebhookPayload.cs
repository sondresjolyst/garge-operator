public class WebhookPayload
{
    public int Id { get; set; }
    public int SwitchId { get; set; }
    public Switch Switch { get; set; } = null!;
    public string Value { get; set; } = null!;
    public DateTime Timestamp { get; set; }
}
